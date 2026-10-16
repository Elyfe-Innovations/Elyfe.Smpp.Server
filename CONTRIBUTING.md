# Contributing to Elyfe.Smpp.Server

Thanks for your interest in contributing! This project is licensed under
**GPL-3.0-or-later**, and all contributions are accepted under the same license.

## Getting started

1. Fork the repository and create a feature branch from `main`.
2. Make sure you have the **.NET 10 SDK** installed.
3. Build and test:

   ```bash
   dotnet build Elyfe.Smpp.Server.slnx
   dotnet test  Elyfe.Smpp.Server.slnx
   ```

## Development guidelines

- Target framework is **net10.0**; nullable reference types and implicit usings are enabled.
- Keep public APIs documented with XML doc comments.
- Add or update tests for any behavioral change. Both unit (PDU round-trip,
  authenticators) and integration (full bind → submit → unbind) tests live under
  `test/Elyfe.Smpp.Server.Tests`.
- Preserve the original JamaaTech copyright headers on any reused PDU-codec files.
- Follow the existing code style; run `dotnet format` before submitting.

## Commit messages

Use [Conventional Commits](https://www.conventionalcommits.org/), e.g.
`feat(server): ...`, `fix(handlers): ...`, `test(...): ...`, `docs(...): ...`.

## Pull requests

- Keep PRs focused and reasonably small.
- Describe the motivation and the change.
- Ensure CI (build + tests) passes.
- By submitting a PR you agree to license your contribution under GPL-3.0-or-later.

## Reporting bugs / requesting features

Open a GitHub issue with a clear description, reproduction steps, and the SMPP
version involved where relevant.
