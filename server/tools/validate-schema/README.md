# validate-schema

A small, dependency-free .NET console app that validates every inventory instance
file against its kind JSON Schema (`schemas/kinds/{kind}.instance.schema.json`,
which composes the shared `schemas/common/instance.base.schema.json`).

## Why a separate console project

- **Reused by CI.** It is the schema gate in `validate-registry.yml`; keeping it a
  standalone console means the PR check runs it directly with `dotnet run` and a
  non-zero exit code fails the build — no web host, no database, no DI container.
- **No web dependencies.** It shares only the schema/inventory files, not the ASP.NET
  host, so it stays fast and has a tiny dependency surface.
- **Deterministic.** Same inputs → same exit code and output, which is what a CI gate
  needs.

## When you would modify it

- A new kind schema is added and needs to be discovered/validated.
- The schema-composition rules change (e.g. new shared base constraints).
- You want richer diagnostics in the CI output.

## Run

```powershell
dotnet run --project tools/validate-schema
```

Exit code `0` means all instance files are valid; non-zero means at least one failed.
