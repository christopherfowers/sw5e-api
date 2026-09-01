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

### This repository has a submodule

`external/sw5e-database` pins a commit of the content repository. Clone with it,
or the build will stop at a missing project reference:

```sh
git clone --recurse-submodules https://github.com/christopherfowers/sw5e-api.git
```

If you already cloned without it:

```sh
git submodule update --init
```

It is there for one reason. The write path validates every authored document
against the JSON Schemas in that repository, and its CI validates the whole
corpus against the same ones. If those two checks were separate implementations
they would eventually disagree, and the way you would find out is a document
this API accepted and that repository's CI later rejected — by which point it is
already in the corpus. Referencing the one validator makes that impossible
rather than unlikely.

Only `src/Sw5e.Database.Schemas` and `schemas/` are used. The submodule's
`content/` directory is not built, not tested against, and excluded from the
Docker build context.

Bumping the pinned commit is a deliberate, reviewed change: it can alter what
documents the API accepts.

## Reviewing content changes

Changes to canonical game content are reviewed like code. A content pull request
should state its source — the book and page it comes from — so a reviewer can
verify it against the original text.
