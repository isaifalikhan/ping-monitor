# NetWatch 2.0.0 — bug list (release QA, 2026-11-22)

Severity: **Critical** crash, data loss, security hole, install/upgrade fails, collection stops · **High** feature does
not work, wrong/missed alerts, wrong data · **Medium** works with errors or a bad workaround, UI problem that blocks a
task · **Low** cosmetic, wording, minor.

Every fix: regression test → full build (0 warnings / 0 errors) → full test suite → re-run of the affected manual or
black-box test → commit.

| ID | Severity | Title | Status | Regression test |
|---|---|---|---|---|
| BUG-001 | Medium | SMTP / syslog notification lost when the relay is briefly unavailable | Fixed | `QaNotificationTests.Dispatcher_retries_when_the_relay_is_briefly_unavailable` |
| BUG-002 | Medium | E-mail address without a domain accepted; the whole mail then fails for all recipients | Fixed | `QaNotificationTests.Smtp_sends_through_a_relay…`, `Smtp_configuration_errors_are_clear`; black-box `ALR-06` |
| BUG-003 | Medium | Notification script that does not read stdin reported as failed ("The pipe is being closed"), exit code lost | Fixed | `QaNotificationTests.Script_failures_and_timeouts_are_reported_and_names_are_validated` |
| BUG-004 | Low | SNMP connection test shows raw socket text for a closed UDP 161 | Fixed | `QaRegressionTests.Snmp_to_a_closed_udp_port_explains_that_the_agent_is_not_running` |
| BUG-005 | Low | Misleading log "Open alert already exists" when the device was deleted meanwhile | Fixed | `QaAlertLifecycleTests.Signals_for_a_device_deleted_meanwhile_are_dropped_and_the_engine_keeps_working` |
| BUG-006 | Medium | One poll overrun in 72,000 polls during the 2× soak; root cause not determined | **Open** (diagnostics added) | final soak (see TEST_RESULTS REL-01) |
| BUG-007 | Low | SMTP connection errors do not name the server or the cause ("No such host is known.") | Fixed | `QaNotificationTests.Smtp_connection_failures_name_the_server_and_the_cause`; black-box `PLT-01` |
| BUG-008 | High | `build.ps1 -Offline` fails at the test step (`dotnet test` has no `--source`) | Fixed | offline build re-run with empty NuGet cache and network blocked (TEST_RESULTS AIR-03) |

Totals: found 8 (Critical 0, High 1, Medium 4, Low 3) · fixed 7 · **open 1 Medium (BUG-006)**.

---

## BUG-001 — Notification lost when the SMTP relay or SIEM is briefly unavailable (Medium)

- **Steps:** enable SMTP notifications; stop the relay (or restart it) while an alert is raised; start it again.
- **Expected:** the notification is delivered once the relay answers again (a restart takes seconds).
- **Actual:** one attempt only; the e-mail (and a TCP syslog forward) was dropped and only counted as *Failed*.
- **Fix:** `NotificationDispatcher` retries SMTP and syslog deliveries after transient errors (socket/IO/timeout,
  SMTP 4xx) after 10 s and 60 s; configuration errors (no host, bad address) are not retried.
- **Regression test:** `Dispatcher_retries_when_the_relay_is_briefly_unavailable` (relay comes up after the first
  attempt; exactly one mail arrives, status *Sent = 1*).

## BUG-002 — Recipient or sender without a domain accepted (Medium)

- **Steps:** *Settings → Notifications*: recipients `noc@corp.local; noc` (typo), save, trigger a Critical alert.
- **Expected:** saving is refused with a clear message; valid recipients still get mail.
- **Actual:** MimeKit parses `noc` as an address; it was saved, the relay rejected `RCPT TO:<noc>`, and the whole
  message failed — nobody received the alert.
- **Fix:** `SmtpChannel.Parse` requires `local@domain`; the settings API validates sender and recipients
  (`MailSender.ValidAddress` / `ValidRecipients`).
- **Regression tests:** unit tests above; black-box `ALR-06` (API returns 400 for `noc` and for sender `netwatch`).

## BUG-003 — Script action fails when the script does not read standard input (Medium)

- **Steps:** notification script `notify.cmd` that only uses the `NETWATCH_*` environment variables and exits
  (e.g. `exit /b 3` or `exit /b 0`).
- **Expected:** success for exit 0; for exit 3 the error "Script exited with code 3: …".
- **Actual:** `IOException: The pipe is being closed` while writing the JSON to stdin — reported as a failure
  even for a successful script, exit code lost.
- **Fix:** stdout/stderr readers start before the stdin write; an `IOException` on stdin is ignored and the exit code
  decides.
- **Regression test:** `Script_failures_and_timeouts_are_reported_and_names_are_validated`.

## BUG-004 — SNMP connection test: unclear message for a closed UDP 161 (Low)

- **Steps:** *Pilot tools → SNMP* against a host without an SNMP agent (ICMP port unreachable).
- **Expected:** "UDP 161 is closed … the SNMP agent is not running or not enabled".
- **Actual:** "An existing connection was forcibly closed by the remote host."
- **Fix:** `ConnectionTestService.SnmpHint` maps connection-reset/refused to the explanation.
- **Regression test:** `Snmp_to_a_closed_udp_port_explains_that_the_agent_is_not_running` (real closed port).

## BUG-005 — Misleading log when an alert cannot be stored (Low)

- **Steps:** delete a device while its alert signals are queued.
- **Expected:** signal dropped, the log says why.
- **Actual:** behaviour correct, but the log said "Open alert already exists".
- **Fix:** log text names both causes (open alert exists, or device deleted).
- **Regression test:** `Signals_for_a_device_deleted_meanwhile_are_dropped_and_the_engine_keeps_working`
  (also proves the engine keeps working).

## BUG-006 — Single poll overrun during the 2× soak (Medium, open)

- **Steps:** simulator `NETWATCH_SIM_SCALE=2`, 4,000 DC events/min, 5 % slow and 2 % timeout devices, 4 h.
- **Expected:** 0 overruns.
- **Actual:** 1 overrun at minute 130 (of ≈ 72,000 polls). The decaying maximum poll duration stayed ≈ 5 s and no poll
  reached the 55 s poll timeout (no timeout warnings), so the device was most likely still *queued* or in result
  processing (database write) when it was due again. Impact: one skipped poll of one device (one missing sample).
- **Action taken:** `PollScheduler` now records when each poll was queued and started and logs at every overrun the
  device, stage (queued/running) and times. The final soak runs with this build.
- **Why not fixed:** the root cause is not proven; changing scheduling without it would be guesswork.
- **Status:** open → blocks GO (gate requires 0 open Medium).

## BUG-007 — SMTP connect errors without the server name (Low)

- **Steps:** SMTP host `relay.corp.local` that DNS does not know; *Pilot tools → SMTP*.
- **Expected:** "The SMTP server name 'relay.corp.local' cannot be resolved (DNS) …".
- **Actual:** "No such host is known."
- **Fix:** `SmtpChannel.ConnectError` (DNS, refused, unreachable) wrapped in an `IOException` (still retried).
- **Regression test:** `Smtp_connection_failures_name_the_server_and_the_cause`; black-box `PLT-01`.

## BUG-008 — Offline build fails (High)

- **Steps:** `.\build.ps1 -PrepareOffline` on a connected PC, then `.\build.ps1 -Offline`.
- **Expected:** UI, tests, single-file exe and installer are built from the offline kit.
- **Actual:** `MSBUILD : error MSB1001: Unknown switch` — the script passes `--source <kit>` to `dotnet test`, which
  does not accept it. The air-gapped build could not be completed (the online build was not affected).
- **Fix:** restore from the kit with `dotnet restore --source`, then `dotnet test --no-restore`.
- **Regression test:** the offline build itself, run with an empty `NUGET_PACKAGES` folder (only the kit can be
  used) and HTTP(S) forced to a dead proxy (`127.0.0.1:9`); see TEST_RESULTS AIR-03.

## Not bugs (investigated)

| Observation | Why it is correct |
|---|---|
| WinRM/DCOM connection test to `127.0.0.1` succeeds with wrong credentials | Windows ignores alternate credentials for local WMI connections; remote targets are authenticated |
| Hourly metric data older than 730 days still present | Retention drops whole shard periods (day / month / year), documented in the admin guide |
| Hung vCenter shows the device *Up* for the first minutes | Device status is ICMP; the "vCenter/ESXi API not reachable" rule has a 180 s duration and fired as designed |
| Report for a 26-year range refused (400) | Report period is limited to 366 days by design |
| Poll interval below 15 s accepted | It is clamped to 15 s (checked: stored value ≥ 15) |
