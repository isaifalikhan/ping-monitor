# Security review and performance test (2.0.0)

Review of NetWatch 2.0 against the hardening brief (WP11). Each finding lists what was found, what changed and
how it is verified. Automated checks run in every build (`dotnet test`).

## 1. Findings and fixes

| # | Area | Finding | Fix / state | Verified by |
|---|---|---|---|---|
| 1 | Authorization | Every endpoint needs explicit protection; a future endpoint could be added without it | All API endpoints require a policy; only `POST /api/auth/login`, `/logout`, `/kiosk` and `GET /api/auth/options` are anonymous. Writes need Operator or Admin, except a Viewer's own password and saved searches; users, credentials, settings, backups, diagnostics, kiosks, audit, discovery and import need Admin | `Wp11SecurityTests` enumerate the real endpoint table (> 120 endpoints) and fail on any exception; `ApiAuthIntegrationTests` check 401/403 over real HTTPS |
| 2 | CSRF | — | State-changing API calls require the custom header `X-NetWatch-Request` (browsers cannot add it cross-site); cookies are `SameSite=Strict` | `ApiAuthIntegrationTests` (403 without the header) |
| 3 | Login | — | Rate limit on sign-in and kiosk sign-in (10 per minute per client); account lockout after 5 failures for 15 min; password hashes PBKDF2-HMAC-SHA512 with 210,000 iterations (OWASP); forced change of the one-time admin password | existing tests; integration test (forced change) |
| 4 | Session cookie | — | `HttpOnly`, `Secure`, `SameSite=Strict`, server-side session table with idle (30 min) and absolute (12 h) timeouts; only kiosk displays get a persistent cookie without idle timeout, revocable at once | `Session_cookie_is_http_only_secure_and_strict`, `Wp9NocTests` |
| 5 | Security headers | — | Content-Security-Policy, `X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY`, `Referrer-Policy: no-referrer`, Permissions-Policy, Cross-Origin-Opener/Resource-Policy, HSTS, no `Server` header; HTTPS only (TLS 1.2/1.3) | integration test |
| 6 | Secrets at rest | — | Credentials, SMTP password, PFX passwords: DPAPI (machine scope). Portable export: AES-256-GCM, PBKDF2-SHA256 600k, password never stored | `Wp8BackupTests` |
| 7 | Secrets in logs | Reviewed every log statement | Only profile IDs, user names and file paths are logged — never a password, community, key or token. The one-time admin password goes to an ACL-protected file, not the log | code review (grep of all `Log*` calls) |
| 8 | Secrets in exports | Diagnostic bundle and CSV exports | Bundle redacts every property named like a secret at any depth and contains no credential value; CSV is formula-injection safe | `Wp10PilotToolsTests`, `Wp4DcUiTests` |
| 9 | Input validation | — | DTOs validated (lengths, ranges, address formats, file names by pattern, uploads size-limited), EF Core parameterised queries only (no raw SQL with input), XML parsing with DTDs prohibited, zip restore with allow-list and path checks | parser and backup tests |
| 10 | TLS to monitored systems | .NET / MailKit / Windows LDAP may try online CRL/OCSP and certificate downloads, which hang in an air gap | `TlsPolicy`: revocation **NoCheck** (default) or **Offline** (local CRL cache), `DisableCertificateDownloads`, 2 s retrieval timeout — applied to vCenter, Redfish, LDAPS (own verification incl. host name) and SMTP. Self-signed vCenter/iLO/iDRAC certificates are pinned (SHA-256) | `Wp11HardeningTests` |
| 11 | Console certificate | Self-signed by default | Import of an internal-CA PFX from the UI with checks (private key, validity, server-auth EKU, RSA ≥ 2048, host names) | `Wp11HardeningTests` |
| 12 | SNMP v2c | Community strings travel in clear text | Warning on the credential form; v3 authPriv recommended; read-only and ACL-restricted | UI |
| 13 | Supply chain | `nuget.config` used `signatureValidationMode=accept` | Kept `accept`, **but** every project now has `packages.lock.json` (exact versions **and content hashes** of all direct and transitive packages, win-x64 graph) and `build.ps1` restores in **locked mode** (tests and publish): a changed, substituted or tampered package fails the build. `require` was not enabled because it needs the exact nuget.org repository-signer fingerprints in `nuget.config`; a wrong or rotated fingerprint would block every offline build inside the air gap, while the lock-file hashes give the same tamper protection for the packages actually used. npm: `npm ci` against `package-lock.json` (integrity hashes) | `dotnet restore -p:NetWatchLockedRestore=true` |
| 14 | Code signing | Builds unsigned | `build.ps1 -SignCertificate <pfx> [-SignTimestampUrl <internal TSA>]` signs the service exe and the installer (password from `NETWATCH_SIGN_PASSWORD`). Unsigned builds: allow-list by path/hash in AppLocker/WDAC (installation guide §6); Smart App Control exists only on Windows 11 clients | build script |
| 15 | No internet at run time | — | UI build fails on any external URL (`check-offline.mjs`); no telemetry, update checks, CDNs or web fonts; no online revocation (10) | build |
| 16 | Bug found by load test | vCenter events were never stored (EF translation error on every poll with events) | Fixed; regression test fails on the old code | `Wp11RegressionTests` |

Residual risks (accepted, pilot to confirm): SNMP v2c where devices cannot do v3; LocalSystem service account
(needed for ports 162/514 and DPAPI machine scope); WinRM over HTTP 5985 relies on Kerberos/NTLM message
encryption (use HTTPS 5986 where certificates exist); Redfish uses basic authentication over TLS.

## 2. Performance test (simulator, 2× scale)

Setup: Release build, one process, 306 devices (**240 switches**, 20 servers, 40 VMs, 2 DCs, vCenter with 4
hosts), **~12,470 interfaces**, 60 s poll interval, 64 parallel polls; **5 %** of devices answer slowly
(2–2.8 s), **2 %** of switch polls time out (full 3 s SNMP timeout); DC Security events offered at about
1,000 per minute; 30 minutes per run.

| Measure | Run 1 | Run 2 (after the VMware fix) |
|---|---|---|
| Polls in 30 min | 9,734 (≈ 324/min) | 9,701 (≈ 323/min) |
| Average poll duration | 250–420 ms | 280–390 ms |
| Longest poll | 4.0–6.4 s (simulated slow devices and 3 s SNMP timeouts) | 4.2–6.0 s |
| Overruns, queued polls, dropped samples, dropped alert signals | 0 | 0 |
| Failed polls | 26 (simulated timeouts) | 25 |
| CPU | ≈ 37 s in 30 min (≈ 2 % of one core) | ≈ 35 s (≈ 2 %) |
| Working set | 290–311 MB | 262–327 MB |
| DC Security events stored | ≈ 600/min (≈ 1,000 offered; simulator under-generated) | ≈ 780/min of ≈ 1,000 generated (the rest are network logons dropped by the default 4624 filter) |
| metrics.db growth | +24 MB / 30 min | +25 MB / 30 min (≈ 1.2 GB/day before rollup retention) |
| events.db growth | +17 MB / 30 min | +19 MB / 30 min (≈ 0.9 GB/day at this DC rate; purged after the event retention period) |
| Warnings in the log | 6 × "Processing VMware data failed" (bug, fixed) | none (except ports 514/162 held by another process on the test PC) |

Sizing: at this scale plan about **50 GB** for metric history plus the events: the whole events.db grew ≈ 0.9 GB/day in this test, of which the DC Security events are ≈ 0.34 GB/day at 1,000 events/min with the default 4624 filter (≈ 0.45 GB/day storing all logon types; measured separately, about 310 bytes per event). With the default 365-day event retention plan **≈ 330 GB** at this event mix (≈ 80 GB with 90 days) for `C:ProgramDataNetWatch`, plus backups on
another disk. Real SNMP agents answer more slowly than the simulator; the 64 parallel polls leave ample headroom
(average poll ≈ 0.35 s against a 60 s interval).

One slow switch cannot delay the others: each device is polled by its own worker from a pool of 64, so slow and
timing-out devices only occupy their own worker; the queue stayed at 0 and no poll cycle overran.
