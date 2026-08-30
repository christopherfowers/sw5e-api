# Sw5e.Email

Transactional email for the account flows: address verification and password
reset. Built so that swapping the sending provider is a configuration change,
never a code change.

## The seam

Everything that sends mail depends on one interface and nothing else.

```csharp
public interface IEmailSender
{
    Task<EmailDeliveryResult> SendAsync(
        EmailMessage message,
        CancellationToken cancellationToken = default);
}
```

Above that line live the things a caller owns: who, what, and did it work.
Below it live the things a provider owns: API tokens, base addresses, SMTP
hosts, TLS modes, HTTP status codes, SMTP reply codes. Nothing crosses.

```
IAccountEmailService          templates, sender identity, link validation
        │                     ── written once, provider-agnostic
        ▼
IEmailSender  ◄── RetryingEmailSender    backoff, jitter, Retry-After
        │                                ── written once, provider-agnostic
        ▼
   ┌────┴──────────────┬─────────────────┐
MailerSend            SMTP            Capture
HTTP + JSON        RFC 5321       in-memory, dev and tests
```

Adding a provider is a new class implementing `IEmailSender`, a new member on
the `EmailProvider` enum, and a new branch in `EmailServiceCollectionExtensions`.
It is not an edit to a template, a call site, or the identity system.

### What keeps the seam honest

Three deliberate choices, each of which would be easy to give up and expensive
to get back:

- **Two live implementations.** An interface with one implementation has never
  been shown to abstract anything — the first thing to leak is always a detail
  the sole implementation happened to expose. MailerSend and SMTP share nothing
  below the interface: one is JSON over HTTP, the other a stateful text
  protocol over a socket. `ProviderSeamTests` runs the same call through both
  and asserts the recovered messages are identical.
- **Templates live here, not in the provider.** MailerSend's server-side
  templates are nicer to edit and would move the wording of a password-reset
  email into a vendor dashboard, where it stops being reviewable, diffable,
  testable, and portable.
- **Resilience is a decorator, not a provider concern.** Retry is expressed
  against `EmailFailureKind`, so each adapter contributes one thing — a correct
  transient/permanent classification — and inherits the rest.

### The contract for implementers

A delivery problem is reported by returning a failed `EmailDeliveryResult`,
never by throwing. Callers steer on the result, and an implementation that
throws bypasses the retry decorator entirely. Exceptions are for programmer
error. The single exception is cancellation: `OperationCanceledException` from
the caller's token must propagate, because a cancelled request is not a failed
send and must not be retried.

Implementations are registered as singletons and must be safe for concurrent
use.

## The contract for the identity system

Two methods. The caller owns the token and the URL built from it; everything
else is this library's problem.

```csharp
public interface IAccountEmailService
{
    Task<EmailDeliveryResult> SendEmailVerificationAsync(
        EmailAddress recipient,
        string verificationUrl,
        TimeSpan? validFor = null,
        CancellationToken cancellationToken = default);

    Task<EmailDeliveryResult> SendPasswordResetAsync(
        EmailAddress recipient,
        string resetUrl,
        TimeSpan? validFor = null,
        CancellationToken cancellationToken = default);
}
```

Resolve `IAccountEmailService` from the container. Do not resolve `IEmailSender`
for account mail — that is the lower-level seam, and using it directly means
composing the message yourself.

- `recipient` — an `EmailAddress`, built with `EmailAddress.Create` or
  `TryCreate`. Carries an optional display name used in the greeting and treated
  as untrusted throughout.
- `verificationUrl` / `resetUrl` — an absolute `http` or `https` URL. Anything
  else throws `ArgumentException`; the scheme allow-list is what stops a
  `javascript:` URL reaching an `href`.
- `validFor` — how long the link is good for, if the reader should be told.
  Rendered as a sentence, rounded to a whole unit. This library does not enforce
  it; the identity system does.

**Inspect the result.** Failure is returned, not thrown, so a caller that
discards it has written an account flow that silently drops undeliverable mail
and nothing will fail to compile.

```csharp
var result = await accountEmail.SendPasswordResetAsync(
    recipient, resetUrl, TimeSpan.FromHours(2), cancellationToken);

if (!result.Succeeded)
{
    logger.LogError("Password reset email failed: {Reason}", result.Failure!.Reason);
}
```

`result.Failure.Kind` distinguishes `Transient` (already retried and still
failing — worth telling the user to try again) from `Permanent` (the address or
the configuration is wrong — retrying will not help).

What this library does **not** do, and will not start doing: generate tokens,
validate them, enforce expiry, or build URLs. Those belong to the identity
system, which is the only thing that can do them correctly.

## Configuration

Bound from the `Email` section. In deployed environments that means environment
variables, where a nested key uses a double underscore.

| Key | Required | Default | Notes |
|---|---|---|---|
| `Email:Provider` | Yes outside Development | `Capture` in Development | `MailerSend`, `Smtp` or `Capture`. |
| `Email:FromAddress` | Yes | — | Must be on a MailerSend-verified domain when that provider is used. |
| `Email:FromName` | No | — | An unnamed sender in an inbox list is the shape of spam. |
| `Email:ReplyToAddress` | No | — | Point it somewhere a human reads. |
| `Email:ProductName` | No | `SW5e` | Appears in every subject and body. |
| `Email:MailerSend:ApiToken` | With MailerSend | — | **Secret.** Never committed. |
| `Email:MailerSend:BaseAddress` | No | `https://api.mailersend.com/` | Overridable for a stub or proxy. |
| `Email:MailerSend:Timeout` | No | `00:00:10` | Per attempt. |
| `Email:Smtp:Host` | With Smtp | — | |
| `Email:Smtp:Port` | No | `587` | Port 465 is rejected — see below. |
| `Email:Smtp:UserName` | No | — | Omit both if the relay authenticates by IP. |
| `Email:Smtp:Password` | With UserName | — | **Secret.** Never committed. |
| `Email:Smtp:UseStartTls` | No | `true` | Cannot be disabled with credentials to a remote relay. |
| `Email:Smtp:Timeout` | No | `00:00:20` | Per attempt. |
| `Email:Retry:MaxAttempts` | No | `4` | Includes the first attempt; `1` disables retrying. |
| `Email:Retry:InitialDelay` | No | `00:00:00.500` | Doubles each attempt. |
| `Email:Retry:MaxDelay` | No | `00:00:05` | Also the limit on an honoured `Retry-After`. |

### Secrets

The API token and the SMTP password are the only secrets, and neither has a
default, a placeholder, or a committed file to live in. Supply them as
`Email__MailerSend__ApiToken` and `Email__Smtp__Password` from a container
secret, an App Service application setting, or a gitignored `.env`.

A missing or invalid value throws `EmailConfigurationException` during service
registration — before the host is even built. The message names the exact key in
both forms. This is deliberate: the failure being designed out is the quiet one,
where a deployment starts happily with no token, every send returns a failure
nobody reads, and the first person to notice is a locked-out user at three in
the morning.

### Development

With no `Email:Provider` set — and no `Email:FromAddress` either — a Development
host uses the capture provider with a placeholder sending identity. It logs the
recipient and subject of each message and delivers nothing. The application runs
with no credentials of any kind, and no developer machine ever holds a real
sending token.

The body is deliberately **not** logged. A verification or reset link is a
bearer credential, and anyone who can read the log can take over the account —
true of terminal scrollback and much more so of anywhere those logs get
shipped. To open a link, or to see how a message actually renders, run a local
catcher and point the SMTP provider at it:

```bash
docker run --rm -p 1025:1025 -p 8025:8025 axllent/mailpit
```

```
Email__Provider=Smtp
Email__FromAddress=noreply@sw5e.localhost
Email__Smtp__Host=127.0.0.1
Email__Smtp__Port=1025
Email__Smtp__UseStartTls=false
```

Mailpit's web UI on port 8025 shows both body parts. Disabling STARTTLS is
permitted here only because the host is loopback; configuration refuses the same
combination against a remote relay.

In tests, assert against `CapturingEmailSender.Sent`, which holds the whole
composed message.

## Known limitations

- **No implicit TLS.** The SMTP adapter is built on `System.Net.Mail.SmtpClient`,
  which speaks STARTTLS from a cleartext connection and has never supported
  implicit TLS on port 465. Configuring that port is rejected at startup rather
  than producing a connection that hangs until it times out. Every mainstream
  provider offers STARTTLS submission on 587; a deployment that genuinely needs
  465 needs a different SMTP client library behind the same adapter, which is a
  change confined to one file.
- **At-least-once, not exactly-once.** A send that reaches the provider and then
  fails on the way back is retried, which can deliver twice. Two verification
  emails beats none, so this is the chosen policy — but it is a policy. An
  outbox with idempotency keys is where it gets solved properly.
- **One recipient per message.** `EmailMessage.To` is singular by design. These
  messages carry bearer tokens; a collection would make "reset link delivered to
  two people" a plausible bug. Bulk sending is a different contract.
