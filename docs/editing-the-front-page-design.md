# Editing the front page

An administrator who signs in today cannot change anything on the front page,
and there are three separate reasons rather than one missing button. This is
what they are and what closes them.

## What the front page is made of

| Section | Where the data comes from | Where the words come from |
|---|---|---|
| Hero: title, lede, counts, buttons | computed at build time | hard-coded markup |
| The rulebooks | `source` documents, already editable | hard-coded markup |
| Sheets and downloads | `resource` documents, invisible to the CMS | hard-coded markup |
| Getting in touch | `channel` documents, invisible to the CMS | hard-coded markup |

Three problems, in the order they bite:

1. `resource` and `channel` are real content types with schemas and documents
   on disk, and neither is in `ContentTypeRegistry`. That list is closed on
   purpose, and it is what the API serves and what the authoring screens
   enumerate, so the two shelves an administrator most wants to rearrange are
   not reachable at all.
2. The prose was never content. The hero lede, and the heading and sentence
   above each of the three sections, are markup in `home.tsx`. There is nothing
   for a CMS to edit.
3. Nothing published reaches a reader. The site is prerendered in full and has
   no runtime server, and the build reads content from `sw5e-database/content`,
   which is files on disk. So an edit goes to PostgreSQL and stops there until
   the exporter, a pull request, a review and a rebuild have all happened.

The third is the one that decides the design. The first two are small once it
is settled.

## The spine that already exists

The authoring endpoints already split the work the way an editorial process
wants:

- **Contributor** reads schemas, lists and opens drafts, saves and discards
  them, and reads the revision history.
- **Administrator** publishes a draft into the catalogue, and reverts.

So a contributor can draft anything and an administrator decides what becomes
true. Nothing here replaces that. What it adds is a third stage, because
publishing to the catalogue and putting something in front of readers stopped
being the same act the moment the site became a static build.

**Draft, publish, release.** A contributor drafts. An administrator publishes,
which changes what the catalogue holds and what the API serves. An
administrator releases, which rebuilds the site and puts it in front of
readers.

The third stage is separate rather than automatic because a full build is 5,129
routes and about six minutes. Nobody should spend that on a typo, and an
administrator fixing four things should ship them once. It also means the
person making the change chooses the moment readers see it.

## 1. Let the CMS see the two shelves

Add `resource` and `channel` to `ContentTypeRegistry`.

Both already have a published schema, documents in the content repository, and
an `order` property, so renaming, rewriting a blurb and reordering a shelf all
work the moment the type is visible. No new concepts.

Permissions need no special case: the existing split already says a contributor
drafts and an administrator publishes, which is the right answer for both. A
contributor proposing a new character sheet or a new Discord link is exactly
the flow the site wants, and an administrator deciding whether it goes up is
exactly the check it needs. `channel` keeps the host allowlist described in
`community-channels-design.md`; nothing here relaxes it.

## 2. A content type for the page's own words

A `page` type. One document per page, keyed by the route it belongs to, so the
front page is `home` and the same mechanism covers `/about` and `/credits`
later without inventing anything.

Every slot is an optional named property rather than a free-form map. Three
reasons, and they all matter:

- The schema stays closed, which is the house rule everywhere else and is what
  keeps a typo from becoming a silently ignored field.
- The authoring form builds each field's help text from that property's
  `description`, so a named property is what gives an editor a sentence
  explaining what the slot is for.
- Adding a slot becomes a schema change, which is reviewed. A free-form map
  would let the front page grow editable regions nobody agreed to.

The slots on `home`:

| Property | What it replaces |
|---|---|
| `heroLede` | the paragraph under the title |
| `booksHeading`, `booksLede` | the rulebooks section's heading and sentence |
| `resourcesHeading`, `resourcesLede` | the sheets section's heading and sentence |
| `touchHeading`, `touchLede` | the contact section's heading and sentence |

**Every one is optional, and absence means the text that is there today.** The
page carries its current wording as the fallback, so a deployment with no
`page` document renders exactly as it does now. This is the same property the
book shelf has and for the same reason: a section should not need a document to
exist before it can be drawn.

### What stays out of the CMS, and why

- **The site's name.** "Star Wars 5e" appears in the header, the page title,
  the meta description and the manifest. Making one of the five editable is how
  the five stop agreeing.
- **The counts line.** "7,877 entries across 31 categories" is measured from
  what the build actually holds. Frozen as text it becomes wrong the first time
  somebody publishes anything, and quietly.
- **The button labels.** "Start with the Player's Handbook" names whichever
  book the corpus says teaches the game. Editing the label without editing the
  link is how the two stop matching.

Each of these is computed because computing it is what keeps it true. An
editable copy of a true thing is a stale copy waiting to happen.

## 3. A rebuild has to read the catalogue

`scripts/build-content-fixture.mjs` already takes its content from one of two
sources, `--content` for the reviewed corpus in `sw5e-database` and `--archive`
for the legacy dump. Add a third, `--api`, which reads the published catalogue
over HTTP.

This is the piece that closes the loop. Without it, "release" would rebuild the
site from whatever the content repository last received, which is the state the
front page is stuck in now.

`sw5e-database` does not stop being the seed or the reviewed record. The
exporter keeps refreshing it from PostgreSQL on its own schedule, and the pull
request it produces is still where the community's edits get read by a person.
What changes is that the site no longer waits for that round trip to show a
correction.

**The request is explicit about scope.** It asks for published, official
content, and says so in the query rather than relying on that being the only
thing the catalogue holds. See the homebrew section below for why this sentence
is here.

**A failed fetch fails the build.** An `--api` run that cannot reach the API,
or that reads a type and gets nothing, exits non-zero rather than writing a
smaller dataset. The importer already refuses to treat a failed read as a
deletion, and this is the same rule one step further out: a build that quietly
published an empty catalogue would pass every health check.

## 4. Release

One control, in the authoring screens. It rebuilds the web image from the
current catalogue and deploys it.

**Administrator only, behind the recent-authentication requirement.** It
changes what every reader sees, which is the bar that gate was built for. The
same one that covers role grants, suspensions and deletions.

**The UI has to say that published is not live.** This is the part most likely
to be got wrong. Once release is a separate step, an administrator can publish
a change, look at the site, see nothing, and reasonably conclude the CMS is
broken. The authoring screens carry a standing line saying how many documents
have been published since the site was last released, and the release control
sits next to it. When the number is zero the control says so rather than
offering a six-minute rebuild of something that has not changed.

**What it does not do.** It does not deploy the API and it does not deploy the
database. Those follow their own pipelines, for the same reason they always
have: a content release should not be able to move code.

## Getting to it from the page

Every content item page carries a quiet "Edit this page" line at the foot for
an account that can use it. The front page gets the same thing, pointing at its
`page` document.

Quiet is deliberate and is already the established register here: a line of
text at the foot, not a toolbar, and nothing at all until the session has
resolved, which is the state the prerendered HTML is frozen in. The counterpart
to being quiet is being present, on the page itself, with the type and key
filled in, so nobody has to remember a URL.

The shelves are their own documents and appear in the worklist under their own
types. The `home` editor links to them rather than embedding them, because a
form that edited four documents at once would be a form that could half-save.

## Where homebrew fits

Homebrew is being scoped next, and the point of this section is that nothing
above forecloses it.

What homebrew adds that official content does not have: an owner, a visibility
that is not "everybody", numbers that could run to thousands of documents, and
a moderation path. What it cannot use is the prerender. The site builds 5,129
routes in six minutes; it cannot build a route per document per account, and
nobody would want it to.

So homebrew will need its own delivery, which is the API at runtime and the
browser rendering it. That is a real architectural fork and it is not this
document's to make. What this document does is avoid three ways of making it
harder:

- **The build asks for official content by name.** Not "everything published".
  When homebrew exists, a build that asked for everything would start
  prerendering strangers' documents, and the failure would look like a slow
  build rather than a mistake.
- **`page` is site chrome and is official by construction.** A homebrew author
  never writes one. The type carries no owner and needs none.
- **Release rebuilds the official site.** It is not a general "publish
  everything" verb, so it does not have to grow a meaning for content it was
  never about.

The one thing worth deciding early, and not here, is whether homebrew is a
visibility on the existing content types or a parallel set of types. This
design works either way.

## The theme

The authoring screens already draw from the site's own tokens: the stylesheet
uses them throughout, and the radius tokens are zero everywhere, so the square
corners and solid fills apply to the CMS as much as to the reference. New
controls use the same tokens and the same classes. There is no separate
administrative look to build and none to maintain.

## What could go wrong

- **A release that renders a broken page.** The build runs the site's own
  checks before the image is published, which is what already stands between a
  bad merge and QA. A release runs the same pipeline rather than a shortcut
  around it.
- **A contributor drafts something and nobody publishes it.** The worklist
  already lists drafts. What it gains is being worth looking at, which is a
  reason to show the count where an administrator sees it rather than only
  inside the authoring screens.
- **Two administrators release at once.** The deploy script already takes an
  `flock` before touching the compose project, for exactly this, because three
  repositories drive one stack.
- **The API is down when somebody releases.** The build fails and nothing is
  deployed. The running site is untouched, which is the correct outcome and is
  why the fetch failing has to fail the build rather than produce a thinner
  dataset.

## Order of work

1. Register `resource` and `channel`. Smallest, and it makes both shelves
   editable on its own.
2. The `page` type, its schema, and the front page reading it with fallbacks.
3. The "Edit this page" line on the front page.
4. The `--api` source for the dataset builder.
5. Release: the control, the published-since-release count, and the pipeline
   behind it.

The first three are useful before the last two exist: they make the front page
editable in the CMS, which is visible in a preview and in the next ordinary
deploy. Four and five are what make it self-service.
