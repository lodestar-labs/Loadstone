# Contributing to Loadstone

Thanks for taking the time — contributions of every size are welcome, from typo fixes to
new database providers.

## Getting set up

1. Install the [.NET 10 SDK](https://dotnet.microsoft.com/download).
2. `dotnet build` and `dotnet test` from the repository root should both be green before
   and after your change. The build treats warnings as errors in CI.
3. For end-to-end testing against a real database, `docker compose up` gives you SQL
   Server plus a running instance with the sample dataset.

## Understanding the codebase

Start with [docs/developer-guide.html](docs/developer-guide.html) — a component-by-component
walkthrough of the architecture and the key parts of each major class, written for people new
to the project. Pair it with [docs/architecture-overview.html](docs/architecture-overview.html)
for design rationale and [docs/manifest-reference.md](docs/manifest-reference.md) for the
manifest spec.

## Making changes

- Open an issue first for anything beyond a small fix, so we can agree on the approach
  before you invest time in it.
- Keep the layering intact: `Core` has no dependencies and defines contracts; format
  readers, providers, and transports implement them. New integrations (PostgreSQL, Blob
  Storage, Service Bus, new file formats) should be new implementations of existing
  interfaces, in their own project if they bring dependencies.
- Add or update tests for the behavior you change. The suite is plain NUnit and runs
  without a database.
- Match the existing code style; `.editorconfig` does most of the enforcing.

## Licensing of contributions

Loadstone uses the Business Source License 1.1 with a commercial tier (see
[COMMERCIAL-LICENSE.md](COMMERCIAL-LICENSE.md)). By submitting a contribution you agree
it may be distributed under the project's current license and under commercial
licenses, and that the project may be relicensed in the future. You keep the copyright
to your work.

## Pull requests

- One logical change per PR.
- Describe what the change does and why; link the issue if there is one.
- CI (build with warnings-as-errors + tests) must pass.

## Reporting bugs

Include the dataset manifest (or a trimmed version), a minimal input file that reproduces
the problem, and the job's rejection rows or event timeline if the import ran. That's
usually everything needed to reproduce an issue quickly.
