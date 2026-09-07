---
Name: OWASP ZAP Scan — 3.0.0 (6 September 2026)
Category: Architecture
Description: The full OWASP ZAP report for the 3.0.0 candidate, scanned on 6 September 2026 against the production portal — a full active scan of the public surface (333 URLs, 0 FAIL, 136 rules passed) and an authenticated passive crawl of the signed-in application (228 URLs, 0 FAIL, 58 passed) — with the delta against the 23 August scan, live header verification, every finding assessed, and the limitations stated.
Icon: Shield
---

# OWASP ZAP Security Scan — MeshWeaver Production Portal (6 September 2026)

> **What this is — and is not.** An internal, automated OWASP ZAP scan of the production portal,
> run by the MeshWeaver team on 6 September 2026 for the 3.0.0 release — a re-run of the
> 23 August 2026 scan of the same portal, so the two can be compared. It is **not** a third-party
> penetration test and is not presented as one; for contractual assurance Systemorph will
> commission, or support, an independent test on request. The dispositions below are as of the
> scan date; the current disposition of every rule is kept on
> [OWASP ZAP Scan — Every Release](/Doc/Architecture/SecurityScanning) under *Findings by release*.

| | |
|---|---|
| **Target** | `https://memex.meshweaver.cloud` (production portal) |
| **Tool** | **OWASP ZAP 2.17.0** (`ghcr.io/zaproxy/zaproxy:stable`) — `zap-full-scan.py` (full active) and `zap-baseline.py -j` (passive rules + AJAX spider) |
| **Scan type** | **Two passes: the public surface and the signed-in application.** A full active attack scan of the public surface; an authenticated crawl performed as a real signed-in user (a genuine browser sign-in was captured and its session replayed on every request) |
| **Date** | 2026-09-06 · active scan 05:40–06:20 UTC (40 min, 10-minute spider cap, 30-minute scan cap) · authenticated pass 05:40–05:52 UTC |
| **Coverage** | Public active: **333 URLs**, **136 rules passed, 0 failed, 5 warnings**. Authenticated: **228 URLs**, **58 rules passed, 0 failed, 9 warnings** — up from 105 / 183 URLs in August |

## Executive summary

**No exploitable vulnerability was found, anonymously or signed in.** Every active-attack rule passed — 48 injection, scripting, execution, traversal, request-forgery and disclosure rules, listed below. The hardening headers fixed on 23 August are confirmed live on every route (verification below). The signed-in pass reached the pages a session unlocks (a user's Export page, the global settings) and surfaced **no new vulnerability class**: the same nine hygiene items as the anonymous run.

**What is different from August is one sentence of honesty.** The August report filed *Vulnerable JS Library [10003]* as "inside Monaco's own library". This run names it: the Monaco editor bundle carries **DOMPurify 3.2.7**, against which 2026 advisories exist (XSS via RAWTEXT elements in `SAFE_FOR_XML`, et al.). Third-party, editor-only, narrow — and not fixable by a package bump, because the newest BlazorMonaco release (3.5.0, the one pinned) still ships that bundle. It is tracked as [MeshWeaver #3378](https://github.com/Systemorph/MeshWeaver/issues/3378) with the fix path (serve a current monaco-editor build in place of the bundled one). We would rather show a named, tracked item than a clean page with a footnote.

## Delta against 23 August 2026

| | 23 August | 6 September | Reads as |
|---|---|---|---|
| Public surface, active scan | 105 URLs · 0 FAIL · 133 rules passed | **333 URLs · 0 FAIL · 136 rules passed** | three times the surface, same result |
| Signed-in application, passive | 183 URLs · 0 FAIL · 9 WARN · 58 PASS | **228 URLs · 0 FAIL · 9 WARN · 58 PASS** | the same nine hygiene rules, no new class |
| Hardening headers (CSP, nosniff, CORP, COOP, Permissions-Policy, HSTS) | fixed and verified | **still live on every route** | no regression |
| CORS posture | no grant to a foreign origin | **unchanged** (preflight now answers 404 rather than 405 — still no grant) | no regression |
| Vulnerable JS Library [10003] | noted as "Monaco's own library" | **DOMPurify 3.2.7 in the Monaco bundle, named; MeshWeaver #3378 open** | the one item with a real, narrow exposure |
| Backup File Disclosure [10095] | false positive (SVG icon filenames) | **same false positive, 21 icons** — housekeeping, see below | not a defect |

## Attack classes tested — all passed

The active scan exercised the full OWASP injection and execution rule set against 333 URLs. Every one returned no alert:

- **SQL Injection** — generic, and MySQL / PostgreSQL / Oracle / MS-SQL / Hypersonic time-based; **NoSQL (MongoDB)**, plain and time-based
- **Cross-Site Scripting** — reflected, persistent (prime and spider), DOM-based
- **Remote Code Execution** — Shellshock, CVE-2012-1823, React2Shell; **Remote OS Command Injection**, plain and time-based; **Server-Side Code Injection**; **Server-Side Template Injection**, plain and blind
- **Log4Shell**, **Spring4Shell**, **Text4Shell (CVE-2022-42889)**
- **Path Traversal**, **Remote File Inclusion**, **Server-Side Request Forgery**
- **XPath**, **XSLT**, **Expression Language**, **SOAP XML** and **CRLF** injection
- **Source-code disclosure** — Git, SVN, `/WEB-INF`, file inclusion, CVE-2012-1823; **cross-domain script inclusion**
- Buffer overflow, format string, integer overflow; parameter tampering, pollution and override; external, off-site and big redirects

*(136 distinct passive and active rules passed on the public surface; 58 passive rules on the signed-in application.)*

## Findings — every warning, and what it is

| Finding (ZAP rule) | Where | Severity | Assessment (as of the scan) |
|---|---|---|---|
| **Vulnerable JS Library** (10003) — DOMPurify 3.2.7 inside `editor.api-*.js` of the Monaco editor | signed-in pass, 1 asset | Medium | **Real, narrow, tracked.** Monaco uses DOMPurify to sanitise the markdown it renders inside the editor (hovers, suggestions); the exploit needs attacker-controlled content reaching that renderer on a page where a victim has the editor open. BlazorMonaco 3.5.0 — the newest release, the one pinned — still bundles monaco-editor 0.42.0-dev (2023) with DOMPurify 3.2.7; current monaco 0.56.0 carries DOMPurify 3.4.8. Fix: serve a current editor build in place of the bundled one — [MeshWeaver #3378](https://github.com/Systemorph/MeshWeaver/issues/3378). Dependabot cannot see this one (static assets inside a NuGet package), which is exactly why a scan is run as well. |
| **CSP: permissive directives** (10055) — `unsafe-inline` / `unsafe-eval` on `script-src`, `unsafe-inline` on `style-src`, `https:` sources, `form-action` without fallback | both passes, `/` and `/login` | Medium | **Deliberate and documented, unchanged since August.** Blazor Server emits inline script; the Monaco editor evaluates code at runtime; the product embeds customer content, media and third-party sign-in flows. Tightening means per-response nonces and enumerated origins — a separate, testable hardening pass, not a header edit. A complete policy IS returned on every route (10038 passes). |
| **Backup File Disclosure** (10095) | 21 files under `/static/NodeTypeIcons/` | Medium | **False positive.** Every hit is an SVG icon whose filename starts with `Copy of` / `Copy (2) of` — ZAP's rule matches the naming pattern of backup copies. They are stray duplicate icons, not backups of anything; the only follow-up is housekeeping (delete the duplicates). |
| **Proxy Disclosure** (40025) | 305 responses | Medium | The managed ingress in front of the application announcing itself, working as intended. |
| **Cross-Origin-Resource-Policy missing** (90004) | a handful of static assets: `styles.css`, `fonts.css`, `favicon.ico`, `robots.txt`, `sitemap.xml` | Low | HTML and API routes carry `same-site` (verified below); the static-files path does not add it. Low value, low risk — a hardening follow-up. |
| **Cross-Origin-Embedder-Policy missing** (90004) | `/`, `/login`, `/sitemap.xml` | Low | Deferred by decision, as in August: `require-corp` breaks cross-origin embeds and worker loading. |
| **Dangerous JS Functions** (10110) — `eval(` | Monaco `loader.js` | Low | Inside the Monaco editor's own loader (third-party). |
| Timestamp disclosure (10096), suspicious comments (10027), Modern Web Application (10109), cache-control (10015 / 10049), cookie slack (90027), session-management response (10112), user-agent fuzzer (10104), information in localStorage (120000) | various | Informational | Hygiene and fingerprinting notes; nothing exploitable. The localStorage item is the signed-in UI remembering its own state on two pages. |

## Live header verification (6 September 2026, `curl`)

Every route returns the complete hardening set — the state the 23 August fixes left the portal in:

| Route | CSP (complete policy) | `X-Content-Type-Options: nosniff` | `Cross-Origin-Resource-Policy: same-site` | `Cross-Origin-Opener-Policy: same-origin` | `Permissions-Policy` | HSTS (1 year, subdomains) |
|---|---|---|---|---|---|---|
| `/` | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ |
| `/Store` | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ |
| `/app` | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ |
| `/login` | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ |
| `/api/version` | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ |

```text
content-security-policy: default-src 'self'; base-uri 'self'; object-src 'none'; frame-ancestors 'self';
    img-src 'self' data: blob: https:; media-src 'self' data: blob: https:; font-src 'self' data: https:;
    style-src 'self' 'unsafe-inline' https:; script-src 'self' 'unsafe-inline' 'unsafe-eval' blob:;
    worker-src 'self' blob:; connect-src 'self' https: wss:; frame-src 'self' https:; form-action 'self' https:
x-content-type-options: nosniff
cross-origin-resource-policy: same-site
cross-origin-opener-policy: same-origin
permissions-policy: accelerometer=(), camera=(), geolocation=(), gyroscope=(), magnetometer=(),
    microphone=(), payment=(), usb=()
strict-transport-security: max-age=31536000; includeSubDomains
```

## Cross-site / CORS posture — unchanged

1. **Same origin, no CORS surface**: the SPA, the portal and `/api` share one origin.
2. **No CORS grant to a foreign origin** (re-verified 6 September): a request carrying `Origin: https://evil.example` receives **no** `Access-Control-Allow-Origin` header, and a cross-origin preflight `OPTIONS` is refused (404) — a malicious page's JavaScript cannot read any API response.
3. **`SameSite=Lax` session + Bearer-only writes**: the session cookie is not sent on a cross-site POST, and every mutating API/MCP verb requires a Bearer token — CSRF surface zero by construction.

## Limitations (stated plainly)

- **Attack payloads were fired at the public surface only — deliberately.** The signed-in application was crawled and inspected passively (228 URLs); no payloads were sent while authenticated, because on a production system that would exercise real write endpoints as a real user and mutate live data. Active testing of the authenticated surface belongs on a staging instance, and we would run it there on request.
- **Blazor Server** — most of the application runs over a SignalR WebSocket circuit that ZAP cannot fuzz, so a clean automated result is **necessary but not sufficient**.
- **The real risk surface is authorization logic** (entitlement and access isolation), which no black-box scanner infers and which is tested separately, in the platform's own test suites.
- **This is an internal scan, not an independent assessment.** For contractual assurance we recommend an independent third-party penetration test, which Systemorph will commission or support.

## Appendix — raw scan output (OWASP ZAP 2.17.0, 6 September 2026)

```text
== Public surface — zap-full-scan.py (active) — https://memex.meshweaver.cloud — 2026-09-06 05:40–06:20 UTC
Total of 333 URLs
WARN-NEW: CSP: Failure to Define Directive with No Fallback [10055] x 15
WARN-NEW: Backup File Disclosure [10095] x 21                    (21 SVG icons named "Copy of …" — false positive)
WARN-NEW: Dangerous JS Functions [10110] x 1                     (Monaco loader.js)
WARN-NEW: Proxy Disclosure [40025] x 305                         (managed ingress — expected)
WARN-NEW: Cross-Origin-Resource-Policy Header Missing or Invalid [90004] x 14   (static assets only)
FAIL-NEW: 0  FAIL-INPROG: 0  WARN-NEW: 5  WARN-INPROG: 0  INFO: 0  IGNORE: 0  PASS: 136

== Signed-in application — zap-baseline.py -j (passive + AJAX spider, captured session replayed) — 2026-09-06 05:40–05:52 UTC
Total of 228 URLs
WARN-NEW: Vulnerable JS Library [10003] x 1                      (DOMPurify 3.2.7 inside the Monaco editor bundle — MeshWeaver #3378)
WARN-NEW: Re-examine Cache-control Directives [10015] x 5
WARN-NEW: Information Disclosure - Suspicious Comments [10027] x 15
WARN-NEW: Non-Storable Content [10049] x 11
WARN-NEW: CSP: Failure to Define Directive with No Fallback [10055] x 10
WARN-NEW: Timestamp Disclosure - Unix [10096] x 3
WARN-NEW: Modern Web Application [10109] x 5
WARN-NEW: Dangerous JS Functions [10110] x 1
WARN-NEW: Cross-Origin-Embedder-Policy Header Missing or Invalid [90004] x 7
FAIL-NEW: 0  FAIL-INPROG: 0  WARN-NEW: 9  WARN-INPROG: 0  INFO: 0  IGNORE: 0  PASS: 58

== Active-attack rules exercised on the public surface — every one PASS (no alert)
  Cross-Domain JavaScript Source File Inclusion [10017]
  HTTP Parameter Override [10026]
  Off-site Redirect [10028]
  Big Redirect Detected [10044]
  Source Code Disclosure - /WEB-INF Folder [10045]
  Remote Code Execution - Shell Shock [10048]
  Source Code Disclosure [10099]
  HTTP Parameter Pollution [20014]
  Source Code Disclosure - CVE-2012-1823 [20017]
  Remote Code Execution - CVE-2012-1823 [20018]
  External Redirect [20019]
  Buffer Overflow [30001]
  Format String Error [30002]
  Integer Overflow Error [30003]
  CRLF Injection [40003]
  Parameter Tampering [40008]
  Cross Site Scripting (Reflected) [40012]
  Cross Site Scripting (Persistent) [40014]
  Cross Site Scripting (Persistent) - Prime [40016]
  Cross Site Scripting (Persistent) - Spider [40017]
  SQL Injection [40018]
  SQL Injection - MySQL (Time Based) [40019]
  SQL Injection - Hypersonic SQL (Time Based) [40020]
  SQL Injection - Oracle (Time Based) [40021]
  SQL Injection - PostgreSQL (Time Based) [40022]
  Cross Site Scripting (DOM Based) [40026]
  SQL Injection - MsSQL (Time Based) [40027]
  NoSQL Injection - MongoDB [40033]
  Log4Shell [40043]
  Spring4Shell [40045]
  Server Side Request Forgery [40046]
  Text4shell (CVE-2022-42889) [40047]
  Remote Code Execution (React2Shell) [40048]
  Source Code Disclosure - Git [41]
  Source Code Disclosure - SVN [42]
  Source Code Disclosure - File Inclusion [43]
  Path Traversal [6]
  Remote File Inclusion [7]
  XSLT Injection [90017]
  Server Side Code Injection [90019]
  Remote OS Command Injection [90020]
  XPath Injection [90021]
  Expression Language Injection [90025]
  SOAP XML Injection [90029]
  Server Side Template Injection [90035]
  Server Side Template Injection (Blind) [90036]
  Remote OS Command Injection (Time Based) [90037]
  NoSQL Injection - MongoDB (Time Based) [90039]
```

The full HTML, Markdown and JSON reports of both passes (about 3 MB) are kept by Systemorph — they are a site map with response bodies and never enter a repository — and are available to a customer's security team on request.

*Run and written by Systemorph, 6 September 2026 · the captured session used for the authenticated pass was discarded after the run.*
