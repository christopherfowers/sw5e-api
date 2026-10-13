# Telling people what happened

Drake published a change to the Sentinel class on 6 October. Nobody was told,
nothing on the site changed, and he published the same document three times
over a fortnight trying to work out why. The content system recorded every one
of those publishes correctly. It just had no way to say so.

This is the design for notifications: a general one, for every account, with
content changes as the first thing it carries.

## What already works, so it is not redesigned here

Revisions are in good shape and nothing below changes them.

| Already built | Where |
|---|---|
| Every publish writes a whole-document revision | `content_revision` |
| Who did it, when, what action, and their stated reason | columns on that row |
| Per-document revision numbers from 1 | `Number` |
| Revert, which adds a revision rather than deleting one | `POST /content/{type}/{key}/revert` |
| A history screen that diffs two revisions properly | `authoring-history.tsx` |

The gap is not the record. It is that reading the record means already knowing
which document to open.

## The roles stay as they are

Three ranks, and Administrator is the top one.

| Rank | Role | What it can do |
|---|---|---|
| 0 | Community | Read the reference, keep an account |
| 1 | Contributor | Draft and correct content |
| 2 | Administrator | Publish, revert, manage accounts and roles |

Designating a leader is granting Administrator, which already works. No fourth
rank, and no separate "may be told" permission: who is notified follows from
what somebody can already do, and a list of watchers maintained by hand is a
list that goes stale the first time somebody is promoted.

## 1. A notification is a row per recipient

```
notification
  id              bigint
  user_id         uuid     -- who is being told
  kind            text     -- content.published, flag.raised, ...
  actor_user_id   uuid?    -- who did it; null when the system did
  subject_type    text?    -- content type, when it is about a document
  subject_key     text?    -- document key
  detail          jsonb    -- what the line needs to read, see below
  created_at      timestamptz
  read_at         timestamptz?
```

One row per recipient rather than one event row with per-user read markers.
Fanning out on write makes the unread count a single indexed count, which is
the query the bell runs on every page, and it lets each person mark their own
as read without a second table. The cost is duplication, and for a community
this size that is a few rows per publish.

`detail` carries what the line has to say rather than a join: the document's
display name at the time, the actor's display name at the time, and the
revision number. A notification is a statement about a moment. If a document is
renamed afterwards, the old notification should still read the way it read when
it was written, and a join would quietly rewrite history.

### What is never in it

A notification must not carry anything its recipient could not already read.
`detail` holds names and numbers, never document bodies and never the reason
text from a moderation flag, because a flag's reason is somebody's account of
another person's conduct and it belongs on the flag.

Everything in `detail` is text somebody typed. It is escaped where it is drawn,
like every other untrusted string on the site.

## 2. Who gets told is derived, not configured

A compiled table, in the same spirit as the content type registry, not rows in
a table somebody edits:

| Kind | Recipients |
|---|---|
| `content.published` | Every Administrator except the actor |
| `content.reverted` | Every Administrator except the actor |
| `flag.raised` | Every Administrator |
| `flag.resolved` | Every Administrator except the actor, and the reporter if they have an account |
| `account.role_granted` | The account whose role changed |
| `account.suspended` | The account suspended |

Two rules hold across all of them.

**Never tell somebody what they just did.** The actor is excluded everywhere.
A bell that lights up because you pressed Publish teaches people to ignore it.

**Adding a kind is a code change.** Who learns what is an editorial decision,
and it should go through review rather than being editable by whoever holds the
database. This is the same argument the content type registry makes.

## 3. The bell

In the site header, for signed-in accounts, with the count of unread.

It draws nothing at all until the session resolves. Every page on this site is
a static file served to everybody, and the prerendered HTML is frozen in the
signed-out state, so a bell baked into those files would appear for anonymous
readers and then change its mind. This is the rule `EditControl` already
follows.

Opening it shows the most recent few, each a line and a link. Opening a
notification marks it read and goes to the thing it is about: a content change
goes to that document's history, at the revision in question, where the diff
already exists.

**The count is polled, because there is nothing to push from.** There is no
runtime server and no socket. The count is fetched when the header mounts, on
navigation, and on an interval slow enough not to matter. Sixty seconds is
plenty for a reference site; this is not a chat application.

## 4. The feed

`/account/notifications`, the whole list, read and unread, oldest to newest with
paging. Mark one read, mark all read.

It belongs under the account rather than under authoring because every account
has one. A Community member who gets told their role changed should not have to
visit a contributor's workspace to read it.

## 5. The API

```
GET  /api/notifications?unread=true&limit=20&cursor=...
GET  /api/notifications/count
POST /api/notifications/read        { ids: [...] }
POST /api/notifications/read-all
```

A separate count endpoint because the header asks for it far more often than
anything asks for the list, and it should answer from an index without reading
rows.

Everything here is scoped to the calling account by the handler, not by a
parameter. There is no way to ask for somebody else's notifications, including
for an administrator, because the alternative is one missing predicate away
from a privacy breach.

## 6. Push, later

Not built now, but the shape should not have to change for it.

Web Push needs a VAPID key pair, a service worker, and a subscription per
device per account:

```
push_subscription
  id, user_id, endpoint, p256dh, auth, user_agent, created_at, last_seen_at
```

That sits beside the notification table rather than inside it. A notification is
the fact; a push is one way of delivering it, and email will be another. When
delivery becomes plural, each channel reads the same rows and records its own
outcome. Nothing above needs to move for that.

## What this depends on, and cannot paper over

**A feed that reports publishes readers cannot see is a feed that lies.**

Every content route is rendered to HTML at build time, from the content
repository, and served by nginx. The CMS writes to PostgreSQL. Nothing connects
them, so a publish updates what the API answers and never changes what a reader
is served. That is why Drake's edit is correct in the database, visible at
`/api/content/class/sentinel`, and absent from `/classes/sentinel` a week later.

Shipping notifications on top of that produces a bell that announces changes
nobody can read, which is worse than silence, because silence at least does not
claim anything.

So this design depends on the publishing path being closed first: content pages
reading from the API rather than being frozen at build time, with the
prerendered file kept as the first paint. That is its own piece of work and is
not specified here, but it comes first.

## What could go wrong

**Notifications pile up.** Nothing prunes them. Read notifications older than
thirty days are deleted on a schedule; unread ones are kept. A person who has
not signed in for a year should still find out their role changed.

**The bell becomes noise.** Six administrators all being told about every
correction to a typo is how a bell gets ignored. If that happens the answer is
grouping by document and by hour, not an opt-out, because an opt-out leaves the
record looking read when it was not.

**A notification outlives the thing it is about.** A document can be deleted
after a notification about it is written. The line is written from `detail` and
still reads; the link checks, and says the document is gone rather than
answering 404.

**Fan-out on a large recipient set.** Today the largest audience is every
Administrator, which is a handful. If a kind is ever added whose audience is
every account, the write stops being trivial and needs batching. That is a
reason to look twice at any new kind whose recipients are not a role.

## Order of work

1. The publishing path, so that what is announced is true
2. The table, the compiled recipient rules, and writing rows on publish
   and revert
3. `GET /api/notifications/count` and the bell
4. The list endpoints and `/account/notifications`
5. The remaining kinds: flags, role grants, suspensions
6. Push, when somebody asks for it
