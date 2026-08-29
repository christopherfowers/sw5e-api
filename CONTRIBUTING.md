# Contributing

Thanks for helping maintain this community resource.

## Ground rules

- Every change arrives as a pull request against `main`. Direct pushes are blocked.
- CI must pass. That means build, tests, linting, and the dependency audit.
- New behavior needs a test. Bug fixes need a test that fails before the fix.
- Commit subjects follow conventional commits: `feat:`, `fix:`, `chore:`,
  `docs:`, `test:`, `ci:`.
- Never commit secrets. Local configuration belongs in a gitignored `.env`;
  commit placeholders to `.env.example` instead.
- Every dependency must carry an OSI-approved license compatible with MIT
  redistribution. Check before adding it, not after.
- The project's assertion library is Shouldly. Never add FluentAssertions,
  even via a Dependabot upgrade: version 8+ ships under a paid Xceed
  commercial license, incompatible with this MIT-licensed project.

## Getting set up

See the "Getting started" section of the README for this repository.

## Reviewing content changes

Changes to canonical game content are reviewed like code. A content pull request
should state its source — the book and page it comes from — so a reviewer can
verify it against the original text.
