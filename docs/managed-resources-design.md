# Managed resources: hosted files as content

Status: proposed, 2026-09-15.

## What this is

The site offers four character sheets. Today they are links to PDFs on a Google
Drive belonging to someone else, and the site neither holds them nor knows
anything about them: not their size, not their page count, not whether the
link still answers.

This makes a resource a piece of content like any other: a document with a
name, a description, an author and a place on the page, plus a file the site
actually holds and serves. Administrators and contributors upload them through
the CMS. Later, community members submit their own, which is the same mechanism
with a review queue in front of it, and is the reason this is designed now
rather than grown later.

Only PDFs. That constraint is the single largest reason this document can be as
short as it is: one format means one parser, one renderer and one attack
surface to harden. Widening it to images or audio later is a new decision, not
an extension of this one.

## The constraint that shapes everything

**The site is statically prerendered.** Every content route is rendered to HTML
at build time and served by nginx; images are baked into the bundle by Vite
with content hashes; `dataset.server.ts` runs during the build and never in a
browser. Nothing the CMS writes reaches a reader until a build runs.

That is fine for the corpus, which changes in reviewed batches. It is fatal for
uploads: an administrator who adds a character sheet would see nothing happen
until somebody shipped a deploy, and a community submission queue would mean a
production deploy per submission.

So the file bytes and the resource list are **runtime** state, served by the
API, while the rest of the site stays exactly as static as it is now.

### How a resource reaches a reader

The shelf is **prerendered from the build's snapshot, then revalidated on the
client.** The build bakes in the resources it knows about, so the page is
complete in its HTML, correct for a reader with no JavaScript, free of any
flash of an empty section, and not dependent on the API being up. On hydration
the page asks the API for the current published set and reconciles.

The cost, stated precisely: between a publish and the next deploy, a reader
with JavaScript disabled sees a list that is up to one deploy old. Nothing else
about the site's static character changes.

This is a better answer than making the section client-only, which was the
first proposal and was wrong. It traded the site's best property (that a page
is whole in its HTML) for an immediacy that progressive enhancement gives for
free.

## The trust boundary

**A PDF is an executable format.** It carries JavaScript, embedded files,
launch actions and additional-action triggers. Accepting one from the public
and serving it from the site's own origin would put hostile code in the origin
that holds sessions, and accounts are the thing this project puts above
everything else.

Three rules follow, and they are layered deliberately: the file is rebuilt so
that what we serve is inert, it is served as a download so no browser opens a
PDF engine on our behalf, and it is served from elsewhere so that if both of
those failed it still could not reach a session. Any one of them failing should
leave the other two standing.

**1. The site never serves a file somebody uploaded.** It serves a file it
built itself from that upload. Nothing crosses from the submitted document into
the published one except what was deliberately carried across. See
*Sanitization*, below, which is the substance of this rule.

**2. The site never renders a reader's PDF in a browser.** Not inline, not in
an embedded viewer, not in an iframe. What a reader sees is a *preview image*
the site rendered itself, server-side. The PDF is only ever a download, served
with `Content-Disposition: attachment` and `X-Content-Type-Options: nosniff`,
so the browser saves it rather than opening a PDF engine on our behalf.

**3. Uploaded bytes are served from a different origin**:
`sw5e-files.cfowers.io`, not `sw5e.cfowers.io`. The session cookies are
host-only: nothing in the identity configuration sets `Cookie.Domain`, so
ASP.NET Core issues them scoped to the exact host, and a sibling subdomain
cannot receive them even if something on it were made to run. This is why a second registrable domain is
not needed, and why that fact has to be asserted by a test rather than
remembered.

Together these mean the residual risk is not "a hostile PDF reaches a reader."
It is "a hostile PDF is parsed by our own rebuilder," which is a server-side
problem with server-side controls: a sandbox that can reach nothing, and a
pipeline that refuses rather than passes through when it fails.

## The model

A resource is two things, kept apart on purpose.

**The document** is content, with a schema in `sw5e-database` beside every
other type: key, name, blurb, kind, order, attribution, review state, and a
reference to a file by its hash. It goes through drafts, publication,
append-only revision history and revert. The authoring engine that already
exists, unchanged.

**The file** is bytes in a content-addressed store, keyed by SHA-256. The hash
is the name, so the same file uploaded twice is stored once, an address is
immutable and therefore cacheable forever, and a takedown is one deletion that
cannot miss a copy.

Keeping them apart is what lets the four official sheets be committed to the
content repository and reviewed as pull requests, while an uploaded sheet
creates the identical document through the CMS. One model, two doors. That is
what "all content is managed content" has to mean if it is to mean anything.

## Ingest

In order, and each step exists because of a specific failure:

1. **Authorize.** `Sw5ePolicies.Contribute`, which already requires strong
   authentication: a passkey or authenticator app, never an emailed code. A
   mailbox-code session cannot upload.
2. **Cap the request body before buffering it.** A size limit enforced after
   the bytes are in memory is not a limit.
3. **Sniff the magic bytes.** `%PDF-`. The extension and the client's
   `Content-Type` are claims by the uploader, not evidence.
4. **Cap complexity before parsing**. Byte size, page count, and object
   count. A decompression bomb is refused by arithmetic, before anything reads
   its contents.
5. **Scan it.** ClamAV, out of process. This is a tripwire, not a control:
   it catches files somebody has already seen and catalogued, and nothing
   about a PDF written last night. It earns its place by being cheap and by
   flagging the uploader, not by being what makes the file safe.
6. **Resolve and rebuild it.** The document is resolved to the single final
   state a viewer would see (last revision wins, incremental updates applied)
   and a fresh one-revision file is written from it. Everything after this
   point examines the rebuilt document, never the submission. See *Sanitization*
   below. The output of this step, not the upload, is what the site stores and
   serves.
7. **Store it** by hash: the rebuilt file, and the original quarantined.
8. **Create the document** in the state the uploader's role earns.

## Sanitization: rebuild, do not inspect

An earlier draft of this document scanned uploads for `/JavaScript`,
`/OpenAction`, `/Launch`, `/EmbeddedFile` and XFA, and refused anything that
carried them. That is a blocklist, and it loses.

It loses for a reason worth naming, because it is not obvious: **parser
differentials.** Our scanner and the reader's Acrobat do not have to agree
about what a file contains. Object streams, compressed cross-reference tables,
incremental updates and shadow attacks all allow a document to present one
structure to a simple parser and another to a full viewer. An attacker does not
need to defeat the blocklist. Only to disagree with it. And a blocklist can
only refuse what somebody thought to list.

So the site does not decide whether an upload is safe. **It extracts what it
wants and writes a new document, and anything it did not deliberately carry
across does not survive.** That is an allowlist enforced by construction rather
than by inspection: the question changes from "did we spot everything bad?",
which cannot be answered, to "did we copy anything but what we meant to?",
which can.

There are two strengths of rebuild, and which one a file gets depends on where
it came from.

### Trusted uploads (normalize)

For administrators and contributors, and for the official sheets: parse into an
object model, carry across an allowlist of page content, fonts, images and form
structure, and serialise a fresh document. Active content has no entry in the
allowlist and therefore no way through.

**`/AcroForm` field structure is carried across deliberately.** Three of the
four sheets are form-fillable and that is their entire purpose. An interactive
form is not an executable one, and a pipeline that could not tell them apart
would destroy the content this feature exists to serve.

### Known-safe JavaScript

Dropping all script would be simpler, and it would be wrong. The evidence is in
the official sheets themselves.

The form-fillable character sheet carries **33 JavaScript entries.** Thirty-two
of them are two strings:

    AFNumber_Format(0, 0, 0, 0, "", true)
    AFNumber_Keystroke(0, 0, 0, 0, "", true)

These are not anybody's code. They are Adobe's own field-formatting API. The
calls that make a number field behave like a number field. They take literal
arguments, they have no capability, and stripping them does not make the sheet
safer, only worse: the fields stop formatting.

The thirty-third is attached to `/OpenAction`:

    this.print({bUI:false, bSilent:false, bShrinkToFit:true})

The document tries to print itself when opened. Not malicious, not asked for,
and exactly the thing that should not survive ingest.

So a rebuilt document may carry script **only** when it matches a closed
allowlist of Adobe's `AF*` formatting and calculation functions:
`AFNumber_Format`, `AFNumber_Keystroke`, `AFDate_*`, `AFPercent_*`,
`AFSpecial_*`, `AFSimple_Calculate` and their siblings. `AFSimple_Calculate`
matters as much as the rest: auto-summing is what a character sheet is for.

Four rules make that allowlist safe rather than decorative, and every one of
them is the difference between a control and a hole:

1. **It matches the parsed object graph, never raw bytes.** Substring matching
   is trivially defeated: `AFNumber_Format(0,0,0,0,"",true); app.launchURL(...)`
   satisfies any check that asks whether the script *contains* an approved
   call.
2. **One call, literal arguments, nothing else.** The entry must parse as a
   single invocation of one allowlisted function whose arguments are all
   literals. No statement sequencing, no member access, no expressions, no
   concatenation. Anything else is unknown by definition.
3. **It runs after reconstruction, never before.** This is the lesson of the
   incremental-update attack: a document can present a benign first revision
   and a hostile later one, so a filter reading the file as submitted is
   examining a document the reader will never see. The rebuild resolves to the
   single final document state a viewer would resolve to, writes one fresh
   revision, and only then is the allowlist applied to what survived.
4. **There is no manual override.** A reviewer cannot approve unknown script
   for a specific file. The bypass is the allowlist (mechanical, closed and
   reviewable) because a human "allow this one" button is a
   social-engineering target, and the person best placed to argue convincingly
   for an exception is an attacker.

Everything not on the allowlist is removed. `/OpenAction`, `/Launch`,
`/EmbeddedFile`, `/RichMedia` and XFA have no allowlisted form at all and never
survive, whoever uploaded them.

### Unknown script raises an alert

When a rebuild strips script that did not match the allowlist, that is a
**security event**, recorded and surfaced to administrators, rather than a
silent success.

It is deliberately not a refusal. The official character sheet would be refused
by that rule, and so would most real-world forms, which routinely carry a
stray action nobody remembers adding. Refusing on unknown script would make the
feature unusable and would teach whoever operates it to reach for an override,
which is the thing rule 4 exists to prevent.

So the file publishes, cleaned, and the event says what was removed and from
where. The uploader is told the same thing. A pattern of alerts on one account
is a signal about that account; a single alert on a twelve-year-old character
sheet is a stray print action.

### Untrusted uploads: rasterize and rebuild

For community and homebrew submissions the default is stronger and simpler:
**render every page to an image and construct a new PDF from those images.**
The output is inert by construction: pictures in a PDF wrapper, with no
object surviving from the upload at all. There is no allowlist to get wrong
because nothing is carried across.

This destroys text selection, searchable text, screen-reader access and form
fillability, and that cost is the reason it is not the universal default. But
it is the right default *here*, because of what community submissions actually
are: somebody's custom class, an adventure, a supplement. Documents to read.
They are not fillable forms. The destructive option costs untrusted content
almost nothing real, which is exactly why untrusted content should pay it.

A submission that genuinely needs to stay fillable is not refused; it goes to a
human reviewer, who may approve the normalize path for it. That is a decision a
person makes about a specific file, with the quarantined original available to
look at, and never a flag the uploader sets.

Losing searchable text is a real accessibility regression and should not be
waved past. The mitigation is that the resource *document* carries the name,
blurb and attribution as real text on the page, so the thing is findable and
describable even when its contents are pictures. Running OCR over the rendered
pages to restore a text layer is a sensible later addition and is deliberately
not in this design: OCR output is generated text presented as the author's
words, and that is its own decision.

### Rules that apply to both

**It runs in the sandbox.** Reconstruction parses hostile input, so it runs in
the same locked-down sidecar as the preview renderer. No network egress,
read-only root filesystem, non-root, tmpfs scratch, hard caps on memory, CPU
and wall clock. This is the residual risk of the whole design, and it is
deliberately concentrated in one container that can reach nothing.

**It fails closed.** If reconstruction fails, times out, or produces nothing,
the upload is **refused**. It is never passed through in its original form
because the rebuild did not work. That fallback is the classic bypass (the
attacker's goal becomes crashing the sanitiser rather than evading it) and it
must not exist.

**The original is never served.** It is retained, quarantined and unreachable
from the web, so that a refusal can be appealed and a reviewer can see what was
actually submitted. Only the rebuilt file has a public address.

**The rebuild is disclosed, not silent.** The uploader is told the file was
reconstructed and what was removed, and the resource page says so too. The
principle the earlier draft had right (that an uploader whose file was quietly
altered does not know what they published) survives; it is satisfied by
telling them rather than by declining to alter it.

**The preview is the same work.** Page one of a rasterised rebuild *is* the
preview image, so for untrusted uploads the two steps share their output rather
than parsing the file twice.

### We do not write a PDF parser

Everything above depends on parsing PDF correctly, and PDF is a format where
"correctly" means "the same way the reader's viewer does". A bar that decades
of CVEs say is hard to clear. Writing a parser for this project would be
building the most dangerous component from scratch, in order to save taking a
dependency.

So the rebuild is implemented on a mature, actively maintained PDF library, and
the code here is the policy (the allowlist, the ordering, the fail-closed
behaviour) not the parsing. The library is pinned, tracked for advisories, and
runs in the sandbox precisely *because* it will eventually have one.

This is also why the rasterize path is attractive beyond its strength: it leans
on a rendering engine doing the thing rendering engines are built and fuzzed
for, rather than on a structural rewrite being exhaustively right.

### What this does not do

It makes the served file inert. It does not make the pipeline invulnerable.
Something still parses hostile bytes, which is why that something is sandboxed
and why it fails closed. And it is a safety control, not a moderation one: it
says nothing about whether a submission is somebody else's copyrighted work or
content nobody wants on the site. That is what the review queue is for.

## The sandbox

One container does both jobs that touch an uploaded file, the rebuild and the
page-one preview, because they are the same risk and concentrating them is the
point. Everything in the system that parses bytes a stranger sent lives here,
and nothing else does.

Rasterising or reconstructing a PDF means running a large C library on hostile
input, historically one of the richest sources of memory-safety
vulnerabilities there is. So it does not run in the API process.

A **separate sidecar service**: no network egress, read-only root filesystem,
non-root user, tmpfs scratch, and hard caps on memory, CPU and wall clock. It
receives bytes, returns a result or an error, and holds no state between calls.
It is treated as already compromised, and the blast radius of compromising it
is a container that can reach nothing: not the database, not the object store,
not the network.

**Rebuild failure and preview failure are not the same event**, and the
difference is deliberate:

- If the **rebuild** fails, the upload is refused. Nothing is published. See
  fail-closed, above.
- If the **preview** fails after a successful rebuild, the resource still
  publishes and the shelf draws a monogram plate. The same fallback the books
  already use when they have no cover art, already built and already tested.

A feature that broke when a picture was missing would make the renderer's
availability a publishing dependency, which is exactly the coupling that turns
a hardened sandbox into a thing people are tempted to relax. A feature that
published when the *sanitiser* failed would be the whole design undone. Those
pull in opposite directions, which is why they are written down separately
rather than left to whoever implements the error handling.

## Review states

`Draft → Pending → Published`, with `Rejected` and `Withdrawn`.

An administrator or contributor publishes directly; their upload is
`Published` on ingest. A community submission lands `Pending` and a reviewer
moves it. That queue is most of what the eventual homebrew path needs, which is
why it is built now although nothing uses it yet. The alternative is
retrofitting a review state onto a table of already-published rows.

`Withdrawn` is separate from `Rejected` because a takedown and a refusal are
different events, and collapsing them loses which one happened.

**The uploader's role also chooses how hard the file is sanitised**. Normalize
for trusted uploads, rasterize for untrusted ones. That coupling is worth
stating plainly rather than leaving implicit in two sections, because it has a
consequence: granting somebody `Contributor` does not only let them publish
without review, it also means their files keep their text layer and their form
fields. Both halves of that should be in mind when the role is handed out.

## Abuse and takedown

Per-account upload quota, a global storage ceiling, and a rate limit on the
upload endpoint. Content addressing makes removal total: delete one blob and
every document referencing it loses its bytes at once, with no second copy
surviving under another name.

That totality is why **permanent deletion requires recent authentication**. A
fresh passkey assertion or authenticator code at the moment of the request, not
merely a session that proved one earlier. It is the only operation in this
pipeline with no undo, which puts it in the same set as granting a role and
repointing a community channel. See *Recent authentication* in the community
channels design, where the requirement is specified.

`Withdrawn` is the reversible alternative and should be the reflex: it takes a
resource off the site without destroying it, and needs no such ceremony.

## Attribution

Resources carry attribution and appear in the credits, through the
`asset-credit` machinery that already exists. The four sheets are somebody's
artwork, re-hosted with permission, and the site should say so in the place it
already says so about every other picture it shows.

## Out of scope

- Formats other than PDF.
- The community submission **interface**. The states, roles and queue are built;
  the public-facing submission flow is its own piece of work.
- Any inline PDF viewer, now or later. See the trust boundary.
- **OCR to restore a text layer** to rasterised submissions. Worth doing and
  deliberately separate: OCR presents generated text as the author's words,
  which is a decision about honesty rather than about safety.

## Testing

A committed corpus of hostile PDFs. Carrying document-level JavaScript, a
launch action, an embedded file, an additional-action trigger, an HTML
polyglot, a decompression bomb, and a truncated trailer. The assertion is not
that each is *refused*; under reconstruction most are accepted and come out
harmless. The assertion is that **the rebuilt output contains none of it**,
checked by parsing the output rather than by trusting the pipeline that wrote
it.

That distinction matters, because it is the difference between testing the
blocklist we replaced and testing the thing we built. A test that asserted
"upload refused" would pass just as happily against the weaker design.

Specifically:

- For each hostile fixture, the rebuilt file parses clean: no `/JavaScript`,
  `/Launch`, `/EmbeddedFile`, `/AA`, `/OpenAction`, `/RichMedia` or XFA
  anywhere in the output object graph.
- A form-fillable fixture survives the normalize path **with its fields intact**
  and **its field actions gone**. Both halves, since a rebuild that quietly
  flattened the forms would pass a test that only checked for JavaScript.
- The rasterize path emits a document whose objects are images and nothing else.
- A fixture engineered to crash or hang the parser produces a **refusal**, not
  a pass-through. This is the fail-closed property and it is the one most
  likely to be undone by a later well-meaning change.
- A parser-differential fixture (benign under a naive read, hostile under a
  full one) comes out inert, which is the case the whole approach exists for.

On the known-safe allowlist specifically, since it is the one place script is
permitted to survive and therefore the one place a mistake is worth the most:

- **The real character sheet is the fixture.** Its 32 `AFNumber_*` calls
  survive, its `/OpenAction` print does not, and its fields still format. Using
  the actual file rather than a synthetic one is the point: it is the document
  this feature exists to serve, and it is the document a regression would
  break.
- **A smuggling fixture** (`AFNumber_Format(0,0,0,0,"",true); app.launchURL("http://example.invalid")`)
  is **not** treated as known-safe. This is the substring-match failure, and it
  is the single most likely way for this allowlist to be quietly weakened by a
  later change.
- **An incremental-update fixture**, whose first revision is clean and whose
  appended revision adds a hostile `/OpenAction`, comes out clean. Proving the
  allowlist ran against the resolved final document rather than the first one.
- **Stripping unknown script raises exactly one recorded alert**, naming what
  was removed. An alert that fires on the AF calls too would be noise, and
  noise is how a security signal gets muted.

Plus a test that the serving origin sets `attachment` and `nosniff`, and a test
that no cookie is ever issued with a `Domain`, since the whole origin-isolation
argument rests on that and nothing else would notice it changing.

## Open questions

- **Storage backend:** a filesystem volume on the host, or an S3-compatible
  service. Behind an interface either way; the volume is enough for the
  foreseeable size and adds no container.
- **The four official sheets' bytes:** committed to the content repository
  (reviewed like everything else, about 2 MB total) or uploaded once through
  the CMS. The first is more consistent with how this project treats content;
  the second is less to carry in git.
