---
Name: OWASP ZAP Scan — Every Release
Category: Architecture
Description: Every release is scanned with OWASP ZAP against the deployment serving the candidate build — a public active scan and an authenticated passive baseline — before its tag is pushed. What is run, how, what the verdict is, what the scanner cannot see, and the findings each release carried.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M12 22s8-4 8-10V5l-8-3-8 3v7c0 6 8 10 8 10z"/><circle cx="12" cy="11" r="3"/><path d="M14.2 13.2 17 16"/></svg>
---

# OWASP ZAP Scan — Every Release

**A release is tagged only after the deployment serving the candidate build has been scanned with
OWASP ZAP (Zed Attack Proxy), and the release notes page says what the scan found.** The scan is a
precondition in [Release Process & Versioning](/Doc/Architecture/ReleaseProcess), beside the sealed
image set and the notes page; the `release` skill carries the same commands for the operator. Raw
reports never enter a repository — they are a site map with response bodies — so the record that
ships is the verdict line and the finding table below.

## The two runs

The portal has two surfaces, and one run cannot cover both: everything a signed-in user loads —
the code editor, the settings tabs, the layout areas — is invisible to an anonymous spider, and an
**active** scan must never run with a real session, because it fires its payloads as that user at
every write endpoint it finds.

| run | who | mode | what it reaches |
|---|---|---|---|
| **Public** | anonymous | **active** (`zap-full-scan.py`) — spiders, then attacks every input it found | the public pages, sign-in, the assets they load |
| **Authenticated** | a real browser session | **passive** (`zap-baseline.py -j`) — spiders with the AJAX spider, inspects every response, sends no payload | the signed-in portal and the bundles only its pages load |

```bash
# Both runs use the pinned scanner image; $OUT is a PLAIN directory (a Docker bind mount —
# an agent scratchpad is not mountable), one sub-folder per run.
OUT=~/.cache/zap-scan-$(date -u +%F); mkdir -p "$OUT/public" "$OUT/auth"

# 1. Public, ACTIVE — anonymous, so safe against production.
docker run --rm -v "$OUT/public":/zap/wrk/:rw -t ghcr.io/zaproxy/zaproxy:stable \
  zap-full-scan.py -t https://memex.meshweaver.cloud \
  -r public-full.html -J public-full.json -w public-full.md

# 2. Authenticated, PASSIVE — $COOKIE is the full `Cookie` header of a real browser session
#    (the OIDC `.AspNetCore.Cookies` session; an API token does not authenticate the SPA).
#    -j turns on the AJAX spider, which is what reaches the assets a signed-in page loads.
docker run --rm -v "$OUT/auth":/zap/wrk/:rw -t ghcr.io/zaproxy/zaproxy:stable \
  zap-baseline.py -t https://memex.meshweaver.cloud -j \
  -r auth-report.html -J auth-report.json -w auth-report.md \
  -z "-config replacer.full_list(0).description=sess -config replacer.full_list(0).enabled=true \
      -config replacer.full_list(0).matchtype=REQ_HEADER -config replacer.full_list(0).matchstr=Cookie \
      -config replacer.full_list(0).regex=false -config replacer.full_list(0).replacement=$COOKIE"
```

The cookie is a live session: it stays in the shell that runs the scan, never in a file under a
repository, and the session is signed out when the run ends.

## The verdict

The last line of each run's log is the verdict:

```
FAIL-NEW: 0	FAIL-INPROG: 0	WARN-NEW: 9	WARN-INPROG: 0	INFO: 0	IGNORE: 0	PASS: 58
```

- **`FAIL-NEW` must be 0** on both runs. A FAIL is a release blocker, full stop.
- **Every `WARN-NEW` is triaged before the tag**: fixed on the candidate, or carried with a
  disposition — the rule id, why it is accepted or deferred, and the issue that tracks it — written
  on the release notes page. A WARN without a disposition is an untriaged finding, not a pass.
- **`Vulnerable JS Library [10003]` is a blocker whatever its level.** It names a shipped library
  version with published advisories, and its evidence is exact: in `auth-report.json`,
  `site[].alerts[]` where `name` starts with *Vulnerable JS Library* carries the library and version
  retire.js matched in `otherinfo` and the bundle in `instances[].uri`. Trust that, never a version
  string read off a package manifest — the shipped bundle is what the browser runs.

## What the scanner cannot see

- **The public run does not reach the signed-in surface.** The 3.0.0 DOMPurify finding below is
  visible only to the authenticated run; a public scan alone reports `PASS` on rule 10003.
- **Retire.js reads banners and signatures.** A bundle that strips its `@license` banner passes the
  rule while still shipping the vulnerable code. That is why the fix for MeshWeaver#3378 keeps the
  banner and a guard test (`MonacoBundleGuard`, MeshWeaver.Plugins) asserts the version behind it.
- **The authenticated run is passive.** Nothing is fired at the signed-in write surface; a
  vulnerability that needs a payload to show is out of its reach by design.
- **Nothing here scans dependencies at rest.** Dependabot covers declared packages; a library
  inlined into a NuGet package's static assets (the Monaco bundle) is visible to neither Dependabot
  nor a source build — only to a scan of what is served.

## Findings by release

### 3.0.0 — scanned 2026-09-06 against memex.meshweaver.cloud

| run | verdict | endpoints |
|---|---|---|
| public, active | `FAIL-NEW: 0 · WARN-NEW: 5 · PASS: 136` | 296 |
| authenticated, passive | `FAIL-NEW: 0 · WARN-NEW: 9 · PASS: 58` | 179 |

| rule | level | run | instances | disposition |
|---|---|---|---|---|
| Vulnerable JS Library [10003] — DOMPurify 3.2.7 inside BlazorMonaco's Monaco bundle | Medium | authenticated | 1 | **Fixed**: MeshWeaver#3378 — the portal builds its own Monaco with DOMPurify 3.4.14 (MeshWeaver.Plugins, `tools/monaco-editor`), guarded by `MonacoBundleGuard` |
| Backup File Disclosure [10095] | Medium | public | 21 | open — triage before the tag |
| Proxy Disclosure [40025] | Medium | public | systemic | open — triage before the tag |
| CSP: Failure to Define Directive with No Fallback [10055] | Medium | both | 15 / 10 | open — triage before the tag |
| CSP: Wildcard Directive · script-src unsafe-eval · script-src unsafe-inline · style-src unsafe-inline | Medium | both | 3 each / 2 each | open — triage before the tag |
| Cross-Origin-Resource-Policy header missing [90004] · Cross-Origin-Embedder-Policy header missing | Low | both | systemic / 7 | open — triage before the tag |
| Dangerous JS Functions [10110] | Low | both | 1 | open — triage before the tag |
| Timestamp Disclosure — Unix [10096] | Low | authenticated | 3 | open — triage before the tag |
| Re-examine Cache-control Directives [10015] · Non-Storable Content [10049] · Suspicious Comments [10027] · Modern Web Application [10109] | Informational | authenticated | 5 / 11 / 15 / 5 | informational — no action |

An earlier scan of the same portal on 2026-08-23 had already reported rule 10003 on the Monaco
bundle; the advisory list had grown by 2026-09-06, which is what turned it into MeshWeaver#3378.

## See also

- [Release Process & Versioning](/Doc/Architecture/ReleaseProcess) — where the scan sits among the release preconditions.
- [Release & Self-Update Strategy](/Doc/Architecture/ReleaseStrategy) — the end-to-end release model.
- [MeshWeaver 3.0.0](/Doc/ReleaseNotes/3_0_0) — the first notes page carrying a scan record.
