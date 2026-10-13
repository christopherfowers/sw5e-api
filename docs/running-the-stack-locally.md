# Running the whole stack locally

Three repositories build three images that only ever meet in a deployment. The
test suites run against a test host and a Testcontainers database, which is the
right thing for them to do and leaves a gap: nothing exercises the images that
actually ship, wired together the way the deployment wires them.

`compose.local.yml` closes that gap, and `tools/verify-stack.py` drives it.

## Why it is worth having

Everything in the "What this turned up" section below was found the first time
the stack was assembled locally. None of it is a defect in any repository and
none of it could have been caught by a unit or integration test, because every
one of them is a property of the assembly rather than of a part.

There is also a standing risk this reduces. The deployed stack is described by
a compose file and a start-up script that live on the host and in no
repository: no history, no review, and no copy if that disk goes. This file is
not that file and does not try to be, but it is the first thing in version
control that says how the three images fit together.

## Building the images

```bash
docker build -t sw5e-database:local ../sw5e-database
docker tag sw5e-database:local ghcr.io/christopherfowers/sw5e-database:local
docker build -t sw5e-api:local .
docker build -t sw5e-web:local --build-arg SW5E_CONTENT_TAG=local ../sw5e-web
```

The tag in the middle is not redundant. The site's Dockerfile pulls its content
from `ghcr.io/christopherfowers/sw5e-database:${SW5E_CONTENT_TAG}`, so a locally
built content image has to be reachable under that name.

Building the site is also the only way to run its `typecheck` and production
build on the Node version the project pins, if the machine has an older one.

## Running it

```bash
docker compose -f compose.local.yml up -d
python tools/verify-stack.py
```

Everything is on <http://localhost:8099>, behind one reverse proxy, with the
mail catcher on <http://localhost:8025>. Both ports are bound to the loopback
address and the mail provider delivers nowhere, so nothing reaches the network.

The one origin is not a convenience. The site addresses the API as `/api` on
its own origin, which keeps the cookie same-site, keeps CORS out of it, and
gives the browser's origin check one answer. A stack that served the two on
different ports would be one where every request the site makes 404s against a
static file server, and where none of that is exercised. That is exactly what
the first version of this file did, and the API-level checks all passed while
the site itself was broken in a browser.

To start again from nothing:

```bash
docker compose -f compose.local.yml down -v
```

The harness is not idempotent against a database that already has accounts in
it, which is deliberate: making it so would mean teaching it to reason about
half-finished enrolments, and starting clean is one command.

## How it differs from the deployment, on purpose

| | Deployment | Here |
|---|---|---|
| Ports | behind a reverse proxy | the same, bound to 127.0.0.1 |
| Mail | a real provider | a catcher, delivering nowhere |
| `UseStartTls` | on, and a fatal error off | off, for a credential-free relay on the compose network |
| Identity schema | migrated as a deliberate step | migrated at API startup |

The last one is worth saying out loud. Production does not migrate at startup
because every replica runs startup, so N replicas race to apply the same
migration, and a web process that can change the schema holds rights at runtime
it has no business holding. One container on a laptop has neither problem.

## What this turned up

**The content volume has to be made writable before anything publishes into
it.** Docker seeds a named volume's ownership from the image of whichever
container mounts it first, and that is not reliably the one that writes to it:
the API mounts the same volume read-only. Get the order wrong and the publisher
cannot create its own directory. The deployment does this inside `deploy.sh`,
which is the script no one can review.

**`migrate all` does not migrate identity.** It brings up content, moderation
and notifications. Identity is a separate path, and a stack whose identity
schema has never been migrated answers every account request with a 500 that
names a missing table.

**The bootstrap administrator is promoted at startup.** An account that
registers afterwards stays a `Community` account until the API restarts, and
the log says so: *"it will be promoted once that address has registered"*.
Whoever onboards an administrator needs to know a restart is part of it.

**Publishing demands strong authentication.** An emailed sign-in code is not a
second factor, so the authoring endpoints refuse everything until an
authenticator is enrolled *and* the session has been proved again with it.
Enrolling alone does not upgrade a session that was opened with a code: the
strength is a property of how that session was proved.

## What the harness checks

Three things, in the order they have to be true:

1. The marker it is about to publish is **not** already on the page. A check
   that cannot fail proves nothing.
2. After publishing, the page endpoint carries it, with nothing rebuilt.
3. A second administrator is told, and the publisher is not.

Point three is the one worth defending. With a single administrator, "nothing
was written" is the correct outcome, because an actor is never told what they
just did, and it is indistinguishable from a feature that does nothing at all.
So the harness makes a second administrator rather than reading zero and calling
it a pass.
