# Testing

## What has been executed (Milestone 1, development machine: Windows 11 Business 26200, non-administrator)

| Suite | Count | Result | What it proves |
|---|---|---|---|
| `tests/HvPortal.Agent.Tests` | 39 | pass | WMI state mapping, operation availability, WMI return codes, RASD InstanceID parsing, command de-duplication (same id executes once), malformed/undefined command rejection, shutdown-reason sanitization, pinned-CA validation, fingerprint comparison |
| `tests/HvPortal.Gateway.Tests` (unit) | ~50 | pass | PBKDF2 (no truncation, NFKC, rehash), password policy (length 15–256, blocklist incl. SHA-1 file, repetition, sequences), RFC 6238 vectors, drift ±1, replay rejection, key ring AEAD + purpose binding + export passphrase, recovery code HMAC, internal CA (subject chosen by gateway, foreign/self-signed/server certs rejected, weak CSR rejected), RBAC (default deny, inheritance, host/VM/group scopes, deny precedence, platform permissions global-only, disabled users), audit chain (modification and deletion detected) |
| `tests/HvPortal.Gateway.Tests` (integration, real pipeline on TestServer + SQLite) | ~33 | pass | unauthenticated access, CSRF, VM isolation (404), force permission + denied audit, typed confirmation, **revocation enforced at job execution**, sessions revoked after admin reset, re-authentication window, **escalation prevention**, last admin protection, user-name injection, hostile query strings, idempotency keys, **TOTP replay**, MFA challenge brute-force bound, progressive delay without account lockout, **restart before/after dispatch**, reconciliation without re-send, **channel loss**, unreachable host retry/fail, cancellation rules, per-VM lock, OpenAPI generation and protection |
| `scripts/dev/Invoke-SmokeTest.ps1` (real Kestrel, real TLS, real agent process) | 35 | pass | setup token + ACL, CSRF/Origin, common password, setup once, MFA restriction, TOTP enrollment (local QR), replay, recovery codes single use, generic login errors, port separation, enrollment with wrong pin refused, token single use, **mTLS agent channel**, real inventory reporting `AccessDenied` truthfully, revocation disconnects and blocks reconnection, audit chain verification, denied actions audited |
| `web` Vitest | 1 | pass | IT/EN dictionaries have identical keys |
| `web/e2e/ui-smoke.mjs` (Edge headless via playwright-core) | 10 | pass | setup → forced MFA → recovery codes → dashboard; host creation and enrollment dialog; real agent enrollment; **live update via SignalR**; host detail shows real access status; audit verification from UI; language/theme switch; no horizontal scroll at 390 px; **no CSP violations** |
| `installer/Build-Release.ps1 -SkipTests` | — | pass | self-contained publish of gateway and agent, SPA included, dependency inventory, SHA-256 checksums; unsigned (no certificate) |

## Not executed yet (requires real hosts or elevation)

- Any Hyper-V operation on real VMs (inventory with VMs, power operations, restart heuristic).
- Installer scripts (need administrator rights), service accounts, firewall rules, upgrade/rollback, uninstall.
- WS2025 Standard/Datacenter and WS2022 compatibility.
- Load/performance with many hosts/VMs; SignalR scale.

→ See [verification/real-host-test-plan.md](verification/real-host-test-plan.md).

## How to run

```powershell
dotnet test HvPortal.slnx
.\scripts\dev\Start-DevGateway.ps1 -Reset          # in a second terminal
.\scripts\dev\Invoke-SmokeTest.ps1
# UI e2e (fresh gateway): 
cd web; node e2e/ui-smoke.mjs --url https://localhost:18443 --data $env:LOCALAPPDATA\HvPortal\dev-gateway `
  --agent-data $env:LOCALAPPDATA\HvPortal\dev-agent --agent-exe ..\src\HvPortal.Agent\bin\Debug\net10.0-windows10.0.17763.0\HvPortal.Agent.exe --out e2e-out
```

The test double `FakeAgentDispatcher` exists only in the test project; production code never simulates host results.
