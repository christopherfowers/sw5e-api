"""Drive the whole stack the way a person would, and check what it actually did.

The test suites run against a test host and a Testcontainers database. This runs
against the three images that ship, wired together the way the deployment wires
them, and asks the two questions the suites cannot:

  does a change published through the API reach a reader with nothing rebuilt
  does anybody find out it happened

Both have been wrong in production while every test was green. A change
published through the content tools once sat unseen for a week because the site
was rendered at build time; its author published it three times, reasonably
concluding the tool was broken.

## Reading the second answer carefully

With one administrator, "no notifications were written" is the correct result,
because the actor is never told what they just did. It is also exactly what a
feature that does nothing looks like. So this makes a second administrator and
checks that they were told, which is the only version of the check worth having.

## Running it

    docker build -t sw5e-database:local        ../sw5e-database
    docker tag sw5e-database:local ghcr.io/christopherfowers/sw5e-database:local
    docker build -t sw5e-api:local             .
    docker build -t sw5e-web:local --build-arg SW5E_CONTENT_TAG=local ../sw5e-web

    docker compose -f compose.local.yml up -d
    python tools/verify-stack.py

It talks only to localhost and sends mail only to a catcher on the compose
network. Every address it uses ends in @sw5e.test.
"""

import base64
import hashlib
import hmac
import json
import sys
import time
import urllib.error
import pathlib
import subprocess
import urllib.parse
import urllib.request

API = "http://localhost:8098"
MAIL = "http://localhost:8025"
SITE = "http://localhost:8099"
ADMIN = "admin@sw5e.test"          # never a real address, by project rule

session = {"cookies": {}}


def call(path, method="GET", body=None, headers=None, base=API):
    data = json.dumps(body).encode() if body is not None else None
    request = urllib.request.Request(base + path, data=data, method=method)
    request.add_header("accept", "application/json")
    # Every unsafe request is refused without one, and the check fails closed:
    # a request with neither Origin nor Sec-Fetch-Site is refused outright,
    # because every browser has sent Origin on unsafe requests for years. A
    # script standing in for a browser has to say the same thing.
    if base == API:
        request.add_header("origin", SITE)
    if data:
        request.add_header("content-type", "application/json")
    for key, value in (headers or {}).items():
        request.add_header(key, value)
    if session["cookies"]:
        request.add_header(
            "cookie",
            "; ".join(f"{k}={v}" for k, v in session["cookies"].items()),
        )

    try:
        with urllib.request.urlopen(request) as response:
            for header in response.headers.get_all("set-cookie") or []:
                name, _, rest = header.partition("=")
                session["cookies"][name] = rest.split(";")[0]
            raw = response.read().decode()
            return response.status, (json.loads(raw) if raw.strip() else None)
    except urllib.error.HTTPError as error:
        raw = error.read().decode()
        try:
            return error.code, json.loads(raw) if raw.strip() else None
        except json.JSONDecodeError:
            return error.code, raw


def solve():
    """A challenge, and the headers that prove it was paid for."""
    status, challenge = call("/api/auth/challenge")
    assert status == 200, (status, challenge)

    salt = challenge["salt"]
    difficulty = challenge["difficulty"]
    need = difficulty // 8
    remainder = difficulty % 8

    counter = 0
    while True:
        digest = hashlib.sha256(f"{salt}:{counter}".encode()).digest()
        if digest[:need] == b"\x00" * need and (
            remainder == 0 or digest[need] >> (8 - remainder) == 0
        ):
            break
        counter += 1

    return {
        "X-Sw5e-Challenge-Salt": salt,
        "X-Sw5e-Challenge-Difficulty": str(difficulty),
        "X-Sw5e-Challenge-Expires": challenge["expiresAt"],
        "X-Sw5e-Challenge-Signature": challenge["signature"],
        "X-Sw5e-Challenge-Counter": str(counter),
    }


def totp(shared_key, when=None):
    """A six digit code from a base32 shared key, as an authenticator makes it."""
    # Padding is computed after the spaces come out, not before: the key is
    # presented in readable groups and its displayed length is not its real one.
    raw = shared_key.upper().replace(" ", "")
    key = base64.b32decode(raw + "=" * (-len(raw) % 8))
    counter = int((when or time.time()) // 30)
    digest = hmac.new(key, counter.to_bytes(8, "big"), hashlib.sha1).digest()
    offset = digest[-1] & 0x0F
    value = int.from_bytes(digest[offset:offset + 4], "big") & 0x7FFFFFFF
    return f"{value % 1_000_000:06d}"


def clear_mail():
    """Empty the catcher, so the next read cannot pick up an older message."""
    urllib.request.urlopen(
        urllib.request.Request(MAIL + "/api/v1/messages", method="DELETE")
    ).read()


def latest_mail(to):
    """The newest message for an address, read out of the local catcher."""
    for _ in range(40):
        status, inbox = call("/api/v1/messages", base=MAIL)
        if status == 200 and inbox.get("messages"):
            for message in inbox["messages"]:
                if any(t["Address"] == to for t in message.get("To", [])):
                    _, full = call(f"/api/v1/message/{message['ID']}", base=MAIL)
                    return full
        time.sleep(0.5)
    return None


def step(name, ok, detail=""):
    print(f"  {'PASS' if ok else 'FAIL'}  {name}{(': ' + detail) if detail else ''}")
    if not ok:
        sys.exit(1)


clear_mail()

print("=== registering the bootstrap administrator ===")
status, body = call(
    "/api/auth/register",
    "POST",
    {"email": ADMIN, "displayName": "Local Admin"},
    solve(),
)
step("register", status in (200, 202, 204, 409), f"HTTP {status}")

print("=== reading the verification mail ===")
message = latest_mail(ADMIN)
step("verification mail arrived", message is not None)

text = (message.get("Text") or "") + (message.get("HTML") or "")
import re

token = None
for pattern in (r"token=([A-Za-z0-9%\-_\.]+)", r"\b(\d{6})\b"):
    found = re.search(pattern, text)
    if found:
        # The link carries it percent-encoded; the endpoint wants the token.
        token = urllib.parse.unquote(found.group(1))
        break
step("found a token in it", token is not None, (token or "")[:12] + "...")

status, body = call(
    "/api/auth/email/verify", "POST", {"email": ADMIN, "token": token}
)
step("verify email", status in (200, 204), f"HTTP {status}")

# The bootstrap administrator is promoted at startup, and this account did not
# exist when the API last started: the log says so plainly, "it will be
# promoted once that address has registered". A real deployment registers
# before it restarts; a local harness has to do the restart itself.
print("=== restarting the API so the bootstrap promotion runs ===")
ROOT = pathlib.Path(__file__).resolve().parent.parent
subprocess.run(
    ["docker", "compose", "-f", "compose.local.yml", "restart", "api"],
    cwd=str(ROOT),
    check=True,
    capture_output=True,
)
for _ in range(60):
    try:
        if call("/api/health/ready")[0] == 200:
            break
    except Exception:
        pass
    time.sleep(1)

print("=== signing in ===")
clear_mail()
status, _ = call("/api/auth/email/code", "POST", {"email": ADMIN}, solve())
step("request a sign-in code", status in (200, 202, 204), f"HTTP {status}")

message = latest_mail(ADMIN)
text = (message.get("Text") or "") + (message.get("HTML") or "")
code = re.search(r"\b(\d{6})\b", text)
step("code in the mail", code is not None)

status, body = call(
    "/api/auth/email/code/verify", "POST", {"email": ADMIN, "code": code.group(1)}
)
step("verify the code", status in (200, 204), f"HTTP {status}")

status, me = call("/api/auth/me")
step("signed in", status == 200 and me is not None, json.dumps(me)[:120] if me else "")
print(json.dumps(me, indent=2)[:400])

# ---------------------------------------------------------------- publishing

MARKER = "Locally verified at read time, not at build time."

# Publishing needs strong authentication, and an emailed code is not that. The
# account reports secondFactorRequired until an authenticator is enrolled, and
# the authoring endpoints refuse everything until it is. That is the design
# working: a code that arrives in a mailbox is not a second factor.
print()
print("=== enrolling an authenticator, because publishing demands one ===")
status, enrolment = call("/api/auth/mfa/totp/enroll", "POST", {})
step("enrol", status == 200, f"HTTP {status}")

status, enabled = call(
    "/api/auth/mfa/totp/verify", "POST", {"code": totp(enrolment["sharedKey"])}
)
step("verify the authenticator", status == 200, f"HTTP {status}")

# Enrolling an authenticator does not retroactively strengthen a session that
# was opened with an emailed code. The strength is a property of how this
# session was proved, so it has to be proved again.
time.sleep(31)  # a fresh window, or the code just used is refused as replay
status, _ = call(
    "/api/auth/reauthenticate/totp", "POST", {"code": totp(enrolment["sharedKey"])}
)
step("reauthenticate with the authenticator", status in (200, 204), f"HTTP {status}")

status, me = call("/api/auth/me")
step(
    "the session is now strong",
    me.get("strongAuthentication") is True,
    f"strong={me.get('strongAuthentication')}",
)

print()
print("=== what a reader sees before anything is published ===")
status, before = call("/api/content/class/sentinel/page")
step("page endpoint answers", status == 200, f"HTTP {status}")
step("marker is not there yet", MARKER not in json.dumps(before))

print("=== publishing a change ===")
status, published = call("/api/content/class/sentinel")
step("read the published document", status == 200, f"HTTP {status}")

document = published["data"]
document["description"] = (document.get("description") or "") + "\n\n" + MARKER

status, _ = call(
    "/api/authoring/drafts/class/sentinel", "PUT", {"document": document}
)
step("save a draft", status in (200, 204), f"HTTP {status}")

status, result = call(
    "/api/authoring/drafts/class/sentinel/publish",
    "POST",
    {"reason": "Local end to end check"},
)
step("publish it", status == 200, f"HTTP {status}")

print("=== and now, with nothing rebuilt ===")
status, after = call("/api/content/class/sentinel/page")
step("page endpoint still answers", status == 200, f"HTTP {status}")
step(
    "the change is there",
    MARKER in json.dumps(after),
    "a publish reached a reader without a rebuild",
)

print("=== notifications ===")
status, count = call("/api/notifications/count")
step("count endpoint answers", status == 200, f"HTTP {status}")
step(
    "the publisher was not told what they just did",
    count["unread"] == 0,
    f"unread={count['unread']}",
)

# ------------------------------------------------- and somebody else is told

# Zero notifications above is the right answer and a useless one: the only
# administrator was the person publishing, and actors are never told what they
# just did. That is indistinguishable from the feature not working, so this
# makes a second administrator and publishes again.
print()
print("=== a second administrator ===")
OTHER = "second@sw5e.test"

status, people = call("/api/auth/admin/users")
step("list accounts", status == 200, f"HTTP {status}")

clear_mail()
first_session = dict(session["cookies"])
session["cookies"] = {}

status, _ = call(
    "/api/auth/register", "POST", {"email": OTHER, "displayName": "Second Admin"}, solve()
)
step("register the second account", status in (200, 202), f"HTTP {status}")

message = latest_mail(OTHER)
step("their verification mail arrived", message is not None)
text = (message.get("Text") or "") + (message.get("HTML") or "")
token = urllib.parse.unquote(re.search(r"token=([A-Za-z0-9%\-_\.]+)", text).group(1))
status, _ = call("/api/auth/email/verify", "POST", {"email": OTHER, "token": token})
step("verify them", status in (200, 204), f"HTTP {status}")

# Back to the administrator, to promote them.
session["cookies"] = first_session

status, people = call("/api/auth/admin/users")
other = next(p for p in people["users"] if p["email"] == OTHER)

status, _ = call(
    f"/api/auth/admin/users/{other['id']}/roles", "PUT", {"roles": ["Administrator"]}
)
step("promote them", status in (200, 204), f"HTTP {status}")

print("=== publishing again, now that somebody else can hear about it ===")
status, published = call("/api/content/class/sentinel")
document = published["data"]
document["description"] = (document.get("description") or "") + "\nSecond change."

status, _ = call("/api/authoring/drafts/class/sentinel", "PUT", {"document": document})
step("save a draft", status in (200, 204), f"HTTP {status}")

status, _ = call(
    "/api/authoring/drafts/class/sentinel/publish", "POST", {"reason": "Second"}
)
step("publish", status == 200, f"HTTP {status}")

status, mine = call("/api/notifications/count")
step("the publisher still hears nothing", mine["unread"] == 0, f"unread={mine['unread']}")

print("=== and the other administrator? ===")
status, _ = call("/api/auth/logout", "POST", {})
session["cookies"] = {}

clear_mail()
status, _ = call("/api/auth/email/code", "POST", {"email": OTHER}, solve())
step("ask for their sign-in code", status in (200, 202), f"HTTP {status}")

message = latest_mail(OTHER)
code = re.search(r"\b(\d{6})\b", (message.get("Text") or "") + (message.get("HTML") or ""))
status, _ = call(
    "/api/auth/email/code/verify", "POST", {"email": OTHER, "code": code.group(1)}
)
step("sign them in", status in (200, 204), f"HTTP {status}")

status, theirs = call("/api/notifications/count")
step("count endpoint answers for them", status == 200, f"HTTP {status}")
step(
    "they were told about the publish",
    theirs["unread"] >= 1,
    f"unread={theirs['unread']}",
)

status, feed = call("/api/notifications/?limit=5")
step("their feed answers", status == 200, f"HTTP {status}")
print("  what it says:", json.dumps(feed["items"][0], indent=2)[:420] if feed["items"] else "(empty)")
