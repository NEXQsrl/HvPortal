# Contributing

The repository is currently private; external contributions open when the project is published.

## Ground rules

1. **No fake success.** Never present a simulated, cached or assumed result as a real outcome. If an operation's
   result is unknown, say so (`Unverified`, `Unknown`, `stale`, `partial`).
2. **Server-side authorization for everything.** Every endpoint, job execution and hub message is authorized on the
   backend. Hiding a button is not a control. Add a negative authorization test for every new endpoint.
3. **No arbitrary execution.** The agent exposes typed operations only. Never add an endpoint/command that runs
   caller-supplied scripts, WQL, PowerShell or paths. PowerShell, when needed, must be parameterized and allow-listed.
4. **Verify APIs in official documentation** and record the source, supported versions and required privileges in
   [docs/capability-matrix.md](docs/capability-matrix.md).
5. **Secrets never in logs, audit details, URLs, exceptions or test snapshots.**
6. A feature is complete only with frontend, backend, authorization, audit, error handling and tests.

## Development

```powershell
dotnet build HvPortal.slnx
dotnet test HvPortal.slnx
cd web; npm ci; npx tsc -b; npx vitest run; npm run build
.\scripts\dev\Start-DevGateway.ps1 -Reset
.\scripts\dev\Invoke-SmokeTest.ps1
```

Database changes: `dotnet ef migrations add <Name> --project src/HvPortal.Gateway --output-dir Data/Migrations/Sqlite`
(provider-specific migration assemblies for PostgreSQL / SQL Server arrive in Milestone 2).

## Style

- C#: nullable enabled, analyzers on, file-scoped namespaces, `TimeProvider` for time, async all the way.
- TypeScript: `strict`, no `any` in new code, all user-visible text in `web/src/i18n.tsx` (IT + EN, same keys — tested).
- Commits: imperative subject, explain *why* in the body.

## Dependencies

Prefer the .NET base class library and Windows built-in facilities. A new dependency needs: a license that allows
redistribution in HvPortal under the PolyForm Shield License (permissive licenses are fine; copyleft or non-commercial
licenses need a review by NEXQ, see [docs/licensing.md](docs/licensing.md#rules-for-new-dependencies)), active
maintenance, and a reason the platform cannot cover. Record it in [docs/licensing.md](docs/licensing.md) and in
[NOTICE](NOTICE).

## License of contributions

HvPortal is licensed under the PolyForm Shield License 1.0.0 ([LICENSE](LICENSE)). The terms under which NEXQ accepts
external contributions (for example a contributor license agreement) are not defined yet: contact NEXQ before
submitting code.
