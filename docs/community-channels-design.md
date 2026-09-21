# Community channels as managed content

Status: proposed, 2026-09-15.

## What this is

The site's "Getting in touch" section points at a Discord, a subreddit, a
Twitter account, a Facebook group and a Patreon. Every one of those is
somebody's account on somebody else's platform, and this is an open-source
community project whose leadership is expected to change hands.

Hard-coding them would mean that the day the Discord moves, or the day
somebody other than the current maintainers runs this, the fix is a code edit
and a deploy by whoever still has commit rights. That is the wrong dependency
for a community reference to have on one person.

So the channels are **content, managed by administrators through the CMS**,
exactly like the books that describe themselves.

## Patreon is not a special case

An earlier draft designed a dedicated Patreon setting: admin-only, a toggle, a
URL, defaulting to off and empty. That was a special case built for one link,
and it should not exist.

A Patreon is a channel in the Support group. "Default off with nothing filled
in" is not a toggle state. **It is the absence of a Support channel**, which
requires no code at all. A group with no enabled channels does not render, so
the section ships with Development and Connect and grows a Support column the
day an administrator adds one.

The existing Patreon is deliberately not carried over. It belongs to the
previous maintainer, it is shared with another project, and its description
makes claims about who manages this site that will not survive the handover.

## The model

A `channel` is a content type with a schema in `sw5e-database` beside every
other type, which means it inherits the authoring engine unchanged: drafts,
publication, **append-only revision history**, and revert. That inheritance is
most of this design's security story and none of its cost. The audit trail for
"who repointed the Discord, when, and to what" is the revision table that
already exists and that the database already refuses to let anybody edit.

Fields:

| Field | Notes |
| --- | --- |
| `key` | Stable identifier. |
| `platform` | A closed enum. Determines the icon, the label, **and the hosts the URL may use.** |
| `group` | `development`, `connect`, or `support`. |
| `url` | Where it goes. |
| `blurb` | The group's paragraph. Rendered as text, never as markup. |
| `order` | Position within its group. |
| `enabled` | Off hides it without discarding its history. |

**The label comes from the platform, not from a text field.** An administrator
picks "Discord" and gets the Discord icon and the word Discord. Free-text
labelling would let a compromised account publish a button reading "Official
Discord" that points somewhere else, and there is no reason to accept that risk
for a naming flexibility nobody wants.

## Administrators only

Content types today authorise against `Sw5ePolicies.Contribute`. A channel must
require `Sw5ePolicies.Administer`, so this adds a **per-type authorisation
level** to the content type definition rather than a special-cased endpoint.

Both policies already demand strong authentication (a passkey or authenticator
app, never an emailed code) so no mailbox-code session can change an outbound
link regardless of the account's role.

## The security of a change

An outbound link on a community's reference site is a phishing primitive. If an
administrator account is compromised, the attacker's most valuable move is not
defacing the corpus; it is repointing "Discord" at a credential-harvesting
clone that thousands of people will click while trusting this domain.

Four layers, and the honest limits of each.

**1. Scheme.** `https` only. The existing `safeExternalHref` helper already
refuses `javascript:` and `data:` but permits plain `http`; for channels that
is tightened, since every platform here serves HTTPS and a downgrade has no
legitimate use.

**2. Host allowlist, bound to the platform.** A `discord` channel may point
only at `discord.gg` or `discord.com`; a `patreon` channel only at
`patreon.com`; and so on. The allowlist lives in code and changing it is a pull
request, which is the point. It is the one part of this that a compromised
administrator account cannot reach.

**This stops domain substitution and nothing more.** It prevents
`discord-invite.example` entirely. It does **not** prevent
`discord.gg/ours` being changed to `discord.gg/theirs`, because that is a valid
Discord invite and no allowlist can tell which server is the community's. That
residual risk is real and is what the third layer is for.

**3. Notification and visibility.** Every channel publish emails **every**
administrator, not only the one who made it, naming the channel, the old
URL, the new URL and the account responsible. A compromised administrator can
make the change; they cannot make it quietly. The revision history holds the
same record permanently and cannot be edited by anyone.

**4. Prove it again, now.** A channel change requires a **fresh** passkey
assertion or authenticator code, performed at the moment of the change. This
replaces the two-person confirmation an earlier draft proposed, and it is the
better control for this project: it stops the realistic attack without making
the community's Discord link unfixable during a handover, which is precisely
when the administrator roster is thinnest and when these links change.

## Recent authentication

This is a real addition, not a restatement of what exists, and the difference
is worth being exact about because the two look identical from a distance.

`StrongAuthenticationRequirement` asks **how this session was established**, and
its own documentation is clear that it reads nothing but the principal, because
"the fact being checked was settled when the session was created and cannot
change while it lasts." The reauthentication endpoints then soften that: a
session can prove a passkey mid-flight and be re-issued carrying the method.
Their documentation says what the claim means afterwards. "Demonstrated during
this session," no longer "demonstrated at the instant the session opened."

**Neither records when.** A session that proved a passkey this morning is
indistinguishable from one that proved it a second ago, so an attacker holding
a stolen session cookie from an administrator's browser inherits every
privilege that session ever earned.

So:

- The authentication-method claim gains a **timestamp**, written whenever a
  method is actually demonstrated. At sign-in, and at each reauthentication.
- A `RecentAuthenticationRequirement` reads it and demands the demonstration
  fall within a **short window**, on the order of ten minutes. Long enough to
  make a handful of consecutive edits without re-tapping a key each time; far
  short of a working day.
- Failing it is not an error but a **prompt**: the API answers with a problem
  the client recognises, the reader is asked for their passkey or code, and the
  original request is retried. The ceremony already exists; only the reason for
  invoking it is new.

### What requires it

Named explicitly, because a control applied by judgement is a control somebody
eventually forgets to apply:

- Publishing or changing a **channel**. The outbound links this document is
  about.
- **Granting or revoking a role**, most of all `Administrator`. This is the
  classic privilege-escalation step and the most valuable single action a
  stolen administrator session could take.
- **Permanently deleting** an uploaded file. The one operation in the
  resources pipeline with no undo.

And, as deliberately, what does not: ordinary corpus authoring. Editing a
species or publishing a rule is reviewed, versioned and revertible, and
demanding a passkey tap per edit would train the people who do it all day to
resent the mechanism that protects the things that matter.

### What this does and does not cover

It stops the dominant realistic attack: a **stolen or hijacked session**. An
attacker with the cookie (from malware, a borrowed laptop, a session left open)
cannot complete a high-risk change, because completing it needs the passkey
or the authenticator secret, which the cookie does not carry.

It does not stop an administrator who is compromised at the device level, or
one acting in bad faith. Both can satisfy a fresh assertion. Nothing short of a
second person can prevent that, and the decision above is that preventing it is
not worth making the site unmaintainable by a lone administrator.

That residual is what layer 3 is for. Prevention stops the session attack;
notification and the append-only history catch the rest. Which is why the
emails go to **every** administrator rather than only the actor. They are the
control that covers the case re-authentication cannot.

## How a channel reaches a reader

The same way resources do: **prerendered from the build's snapshot, revalidated
on the client.** The section is complete in the HTML for a reader with no
JavaScript, and an administrator's change is live without a deploy.

A group with no enabled channels renders nothing. No heading, no empty column.

## Testing

The channel content itself is ordinary CMS content and gets ordinary coverage.
These are the ones that matter, and every one of them is a control that fails
silently if it regresses:

- **Authorisation.** Signed-out, `Community` and **`Contributor`** are all
  refused. Contributor is the interesting case: it may publish game content and
  must not be able to repoint the community's Discord.
- **Strong authentication.** An administrator on a mailbox-code session is
  refused.
- **Recent authentication.** An administrator whose passkey assertion is older
  than the window is refused and prompted, and the same request succeeds after
  a fresh assertion. Both halves matter: a test that only proved the refusal
  would pass against a mechanism nobody could ever satisfy.
- **The window is enforced against a recorded instant**, not against session
  age. A long-lived session that reauthenticates a moment ago passes; a session
  created a moment ago whose claim was minted earlier does not.
- **Scheme.** `http:`, `javascript:`, `data:` and a relative path are each
  refused.
- **Host allowlist.** A `discord` channel pointing at `discord-invite.example`
  is refused; one pointing at `discord.gg` is accepted. Asserted per platform,
  since an allowlist with one entry missing is an allowlist with a hole.
- **Confusable hosts.** `discord.gg.example.com` and `xn--discord-...`
  punycode lookalikes are refused. This is host *matching*, not host
  *containment*, and substring matching here is the same mistake as substring
  matching the JavaScript allowlist.
- **The blurb is text.** Markup in it renders as characters.
- **Absence.** With no enabled Support channel, the rendered HTML contains no
  Support heading at all, hidden or otherwise.
- **Notification.** A publish emails every administrator, not just the actor,
  and names both the old and new URL. Test sends go to `@sw5e.test`.
- **History.** The prior URL is recoverable from the revision table after a
  change, and a revert restores it.

## Out of scope

- Adding a new platform to the enum. That is a pull request by design.
- Free-text channel labels. See the model.
- Any channel type that is not a link. Embedded feeds, live member counts,
  anything that makes a third party's availability this page's problem.
