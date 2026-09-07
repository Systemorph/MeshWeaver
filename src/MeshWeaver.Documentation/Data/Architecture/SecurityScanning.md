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
# The scanner is pinned by VERSION, and that version goes on the notes page beside the verdict;
# bumping it is a deliberate change (a new release of the scanner brings new rules). $OUT is a
# PLAIN directory (a Docker bind mount — an agent scratchpad is not mountable), one sub-folder
# per run, and each run's console output IS its log: the verdict is that log's last line, so it
# is captured — the scripts exit 0 on PASS, 2 on WARN, 1 on FAIL, 3 on a scanner error.
ZAP=ghcr.io/zaproxy/zaproxy:2.17.0
OUT=~/.cache/zap-scan-$(date -u +%F); mkdir -p "$OUT/public" "$OUT/auth"

# 1. Public, ACTIVE — anonymous, so safe against production.
docker run --rm -v "$OUT/public":/zap/wrk/:rw -t "$ZAP" \
  zap-full-scan.py -t https://memex.meshweaver.cloud \
  -r public-full.html -J public-full.json -w public-full.md \
  > "$OUT/public/public-full.log" 2>&1; echo "public scan exit=$?"

# 2. Authenticated, PASSIVE — $COOKIE is the full `Cookie` header of a real browser session
#    (the OIDC `.AspNetCore.Cookies` session; an API token does not authenticate the SPA).
#    -j turns on the AJAX spider, which is what reaches the assets a signed-in page loads.
docker run --rm -v "$OUT/auth":/zap/wrk/:rw -t "$ZAP" \
  zap-baseline.py -t https://memex.meshweaver.cloud -j \
  -r auth-report.html -J auth-report.json -w auth-report.md \
  -z "-config replacer.full_list(0).description=sess -config replacer.full_list(0).enabled=true \
      -config replacer.full_list(0).matchtype=REQ_HEADER -config replacer.full_list(0).matchstr=Cookie \
      -config replacer.full_list(0).regex=false -config replacer.full_list(0).replacement=$COOKIE" \
  > "$OUT/auth/auth-baseline.log" 2>&1; echo "authenticated scan exit=$?"
```

The cookie is a live session: it stays in the shell that runs the scan, never in a file under a
repository, and the session is signed out when the run ends.

🚨 **Neither run is automatable, and there is no CI lane — measured 2026-09-07: no workflow in
`Systemorph/MeshWeaver` or `MeshWeaver.Plugins` invokes ZAP.** The authenticated run needs a HUMAN:
the session cookie can only come from a real interactive sign-in (the generic `authenticated-scan`
skill opens a Playwright-driven Chrome and waits for the person to log in). So an agent can prepare
a release, but it cannot produce this precondition — plan for an operator step. 🚨 And when the
operator uses that skill's `zap-auth-scan.sh`, **its invocation is not this one**: it runs
`zaproxy:stable` rather than the pinned version, and it omits `-j`, so it neither names a scanner
version for the notes page nor runs the AJAX spider that reaches a signed-in SPA's assets. Capture
the cookie with the skill if that is convenient, then run the **command above** with it.

## The verdict

The last line of each run's log (the console output captured above) is the verdict:

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
- 🚨 **Dropping a `<script>` removes the LOAD, not the ASSET.** Measured on both portals
  2026-09-07, after MeshWeaver.Plugins#1393 stopped `App.razor` loading BlazorMonaco's `min/vs`
  tree, a plain `GET` of
  `/_content/BlazorMonaco/lib/monaco-editor/min/vs/editor.api-CalNCsUg.js` still answers
  **200 with 3,669,759 bytes**: the package is still referenced for its `jsInterop.js`, so
  `MapStaticAssets` still publishes every file it ships, and .NET's fingerprint **import map**
  re-advertises 240 of them in the anonymously served app shell — an 81 KB
  `<script type="importmap">` on `/`, the retired `editor.api-CalNCsUg.js` among them. **What that
  does NOT do is put the file back in the scan**, and that half is measured rather than assumed:
  in the 2026-09-07 anonymous baseline below, ZAP 2.17.0 crawled 1330 URLs and rule 10003 read
  PASS, so its spider does not turn import-map entries into requests. The residue is a hardening
  gap — a published file with known advisories that nothing loads — not a live finding; closing it
  means removing the file from the published output (an MSBuild `StaticWebAsset` exclusion, or
  dropping the package reference once `jsInterop.js` is no longer needed). Read the general rule
  the other way round too: **a bundle a page stops loading does not leave the origin**, so an
  inventory taken from `App.razor` is not an inventory of what is served.

## Findings by release

### 3.0.0 — scanned 2026-09-06 against memex.meshweaver.cloud, ZAP 2.17.0

The full report of this scan — coverage, attack classes exercised, the delta against 23 August, live header verification, CORS posture and limitations — is [OWASP ZAP Scan — 3.0.0 (6 September 2026)](/Doc/Architecture/SecurityScan_3_0_0). The table below carries the CURRENT disposition of each rule, which may be newer than the report's.

| run | verdict | endpoints |
|---|---|---|
| public, active | `FAIL-NEW: 0 · WARN-NEW: 5 · PASS: 136` | 296 |
| authenticated, passive | `FAIL-NEW: 0 · WARN-NEW: 9 · PASS: 58` | 179 |

| rule | level | run | instances | disposition |
|---|---|---|---|---|
| Vulnerable JS Library [10003] — DOMPurify 3.2.7 inside BlazorMonaco's Monaco bundle | Medium | authenticated | 1 | **Fixed and DELIVERED, re-scan still owed**: MeshWeaver#3378 — the portal builds its own Monaco with DOMPurify 3.4.14 (MeshWeaver.Plugins#1393, `tools/monaco-editor`), guarded by `MonacoBundleGuard`. Delivery measured 2026-09-07 on the served bytes, not on the merge: `GET /_content/MeshWeaver.Blazor/lib/monaco-editor/monaco.js` answers 200 / 4,483,269 bytes / `sha256:8e991296e5e49dca83a02afa00a0eca20128a5c530b9996ce00830e6b039e846` on **both** memex.meshweaver.cloud and memex.systemorph.com — byte-identical to the committed bundle on MeshWeaver.Plugins `main` — carrying `/*! @license DOMPurify 3.4.14` (`versions.json`: monaco-editor 0.56.0, dompurify 3.4.14). Corroborated by an anonymous re-scan on 2026-09-07 that provably reached the bundle (10003 PASS over 1330 URLs, 10096 on `monaco.js`); the issue still closes on the AUTHENTICATED re-scan. Residue: the retired `min/vs` tree is still published, unloaded and unscanned (see *What the scanner cannot see*). |
| Backup File Disclosure [10095] | Medium | public | 21 | **False positive, measured**: every instance is `/static/NodeTypeIcons/Copy (n) of <icon>.svg`, and that route synthesises an icon for ANY name — a nonsense name answers 200 with a 547-byte SVG of its own, while `bot.svg.bak` is 404 — so no file is disclosed; the rule keys on "a variant of the URL also answers 200". Carried: the fallback icon is the feature. |
| Proxy Disclosure [40025] | Medium | public | systemic | **False positive, measured**: `TRACE` and `OPTIONS` answer 405 (`allow: GET, POST`) with no `Server`/`Via` header; the "Unknown proxy" is ZAP's inference from the refusal. Carried. |
| CSP: Failure to Define Directive with No Fallback [10055] | Medium | both | 15 / 10 | **Carried by design** — see the row below; `form-action 'self' https:` is declared on every response measured (`/`, `/login`), so the missing directive the rule names is to be re-read on the next scan. |
| CSP: script-src unsafe-inline · script-src unsafe-eval · style-src unsafe-inline · Wildcard Directive [10055] | Medium | both | 3 each / 2 each | **Carried by design**: the policy is set and explained in `MemexPortalComposition.cs` (MeshWeaver.Plugins; enforced since #1988 after a Report-Only run over the live pages with zero violations) — `'unsafe-inline'`/`'unsafe-eval'`, `blob:`/`data:` and `https:`/`wss:` are what the Blazor Server circuit, the editor and embedded https content need; per-response nonces and dropping `'unsafe-inline'` are a separate hardening pass. Follow-up: the bundled Monaco (MeshWeaver.Plugins#1393) carries no `eval`/`new Function`, so `'unsafe-eval'` — kept for the editor — can be re-measured. |
| Cross-Origin-Resource-Policy header missing [90004] · Cross-Origin-Embedder-Policy header missing | Low | both | systemic / 7 | **Accepted**: the portal embeds cross-origin resources by design (sign-in assets from the Microsoft CDNs, fonts, user-embedded media); `COEP: require-corp` would break them, and `CORP: same-site` on the portal's own assets is the intended scope. |
| Dangerous JS Functions [10110] — `eval(` | Low | both | 1 | **Fixed by MeshWeaver.Plugins#1393**: the `eval(` is in BlazorMonaco's AMD `loader.js`, which the page no longer loads; the bundled Monaco has no `eval` and no `new Function`. Confirmed on the rolled portal — `PASS: Dangerous JS Functions [10110]` in the 2026-09-07 anonymous baseline, the same run that reached `monaco.js`. |
| Timestamp Disclosure — Unix [10096] | Low | authenticated | 3 | **False positive**: 1732584193, 1518500249, 1859775393 are 0x67452301, 0x5A827999, 0x6ED9EBA1 — SHA-1 round constants in Monaco's hashing code, not timestamps. The same constants sit in the new bundle and will be flagged again. |
| Re-examine Cache-control Directives [10015] · Non-Storable Content [10049] · Suspicious Comments [10027] · Modern Web Application [10109] | Informational | authenticated | 5 / 11 / 15 / 5 | informational — no action |

An earlier scan of the same portal on 2026-08-23 had already reported rule 10003 on the Monaco
bundle; the advisory list had grown by 2026-09-06, which is what turned it into MeshWeaver#3378.

#### 2026-09-07 re-scan of rule 10003 — anonymous, passive, and NOT the acceptance run

After the fix rolled, an **anonymous** `zap-baseline.py -j -m 5` at the same pinned ZAP 2.17.0
against memex.meshweaver.cloud read
`FAIL-NEW: 0 · WARN-NEW: 9 · PASS: 58` over **1330 URLs**, with
**`PASS: Vulnerable JS Library (Powered by Retire.js) [10003]`**.

🚨 **That PASS is not vacuous the way an anonymous PASS on this rule used to be, and the reason is
worth keeping.** The bullet above says a public run cannot see the signed-in surface — true, and
until 2026-09-06 it was why only the authenticated run could report 10003: BlazorMonaco's **AMD**
loader fetched `editor.api` lazily, when an editor was created, which needs a signed-in page. The
new shell loads `monaco.js` **eagerly on every page**, `/login` included, so the editor bundle is
now on the anonymous surface. The proof that the run actually retrieved and scanned it is in the
same report: rule 10096 fires three times on
`/_content/MeshWeaver.Blazor/lib/monaco-editor/monaco.js` with evidence `1732584193`,
`1518500249`, `1859775393` — the SHA-1 round constants this page predicted would follow the new
bundle. Retire.js read that file and passed it.

**It is still not the acceptance measurement.** The release precondition is the AUTHENTICATED
baseline (it is the run that reaches libraries only a signed-in page loads), and it needs a human
sign-in. What the anonymous run settles is this one rule against this one bundle; what it cannot
settle is any library the signed-in portal loads and the anonymous shell does not.

## See also

- [Release Process & Versioning](/Doc/Architecture/ReleaseProcess) — where the scan sits among the release preconditions.
- [Release & Self-Update Strategy](/Doc/Architecture/ReleaseStrategy) — the end-to-end release model.
- [MeshWeaver 3.0.0](/Doc/ReleaseNotes/3_0_0) — the first notes page carrying a scan record.
