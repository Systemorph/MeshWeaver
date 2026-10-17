---
Name: Content-Security-Policy — No Inline Script
Category: Architecture
Description: The portal's script-src is moving from 'unsafe-inline' + 'unsafe-eval' to a per-request nonce. What the strict policy allows, how the shell's own inline scripts carry the nonce, where violation reports go, the declared ReportOnly/Enforce switch, the inventory of what had to change, and what still has to before enforcement.
Icon: /static/NodeTypeIcons/key.svg
---

# Content-Security-Policy — No Inline Script

**The portal's `script-src` is moving from `'self' 'unsafe-inline' 'unsafe-eval' blob:` to
`'self' 'nonce-<per response>' blob:`.** `'unsafe-inline'` is what let injected markup run at all:
an inline `<script>` or an `on*=` handler smuggled into rendered content executed with the viewer's
session. Fixing each injection point (at render time and at write time) removes that injection;
dropping `'unsafe-inline'` removes the class — markup the server did not vouch for does not run,
whoever put it there.

The policy is set in MeshWeaver.Plugins, `src/Memex.Portal.Gui/Security/ContentSecurityPolicy.cs`,
and composed by `MemexPortalComposition`. No other host in either repository sets one: the print and
slide export documents carry their own `default-src 'none'` meta policy (see
[Pixel-Faithful Export](../PixelFaithfulExport)), which is unrelated.

## The two policies

Every directive except `script-src` is the same in both, and unchanged from the policy enforced
since MeshWeaver.Plugins#1988: `default-src 'self'`, `object-src 'none'`, `base-uri 'self'`,
`frame-ancestors 'self'`, `form-action 'self' https:`, and the `img`/`media`/`font`/`connect`/`frame`/`worker`
sources.

| | `script-src` | reports |
|---|---|---|
| **Compatibility** (enforced since #1988) | `'self' 'unsafe-inline' 'unsafe-eval' blob:` | none |
| **Strict** | `'self' 'nonce-<n>' blob:` | `report-uri /api/csp-report` |

**`style-src` keeps `'unsafe-inline'` in both, deliberately.** Fluent UI and Blazor write inline
styles everywhere, and an injected style cannot execute script. Tightening it is a separate pass and
out of scope here.

## The nonce

One per request: 128 random bits, base64, minted by the CSP middleware **before** the response
renders and kept on `HttpContext.Items` (`HttpContext.CspNonce()`). `App.razor` stamps it on every
inline `<script>` it emits and on Blazor's `<ImportMap>` (an inline import map is subject to
`script-src` too). The header names the same value. Content cannot learn it: it is new on every
response, and browsers hide the `nonce` attribute from the DOM.

Pages rendered **outside** the Blazor shell carry no nonce and need none: the step-up pages
(`memex/Memex.Portal.Shared/Authentication/StepUpPages.cs`, core) put their per-page values in a
JSON data block (`<script type="application/json">`, which the browser never executes) and their
behaviour in a same-origin file, `/auth/step-up/page.js`. The first-run setup page has no script.

## The switch — declared, default ReportOnly

`Security:ContentSecurityPolicy:Mode` (environment: `Security__ContentSecurityPolicy__Mode`), declared
in both portal hosts' `appsettings.json` and overridable per deployment record:

| Mode | `Content-Security-Policy` | `Content-Security-Policy-Report-Only` |
|---|---|---|
| `ReportOnly` (default) | compatibility | strict |
| `Enforce` | strict | — |

In `ReportOnly` nothing a browser blocks changes; every page the strict policy *would* break is
reported. Any other value refuses to start, naming the value — an unknown mode never falls back to
one.

## Reports

`POST /api/csp-report` — anonymous (a browser sends a report on its own), excluded from the guest
virtual-user flow (`VirtualUserMiddleware.ExcludedExactPaths`, core), bounded at 16 KB (413
otherwise, unread), answered 204. Each violation is ONE warning line on the
`Memex.Portal.Gui.Security.ContentSecurityPolicy` category:

```
CSP violation (report): script-src-elem blocked inline on https://<host>/<path> at https://<host>/<file>:<line> sample=<first chars>
```

Every URL is cut at its query and fragment (a page URL can carry a token), every field at 256
characters with control characters dropped. Both report bodies are read: the `report-uri` shape
(`application/csp-report`) and the Reporting API's (`application/reports+json`). The lines ship to
Loki like any other warning, so a `Logs` instance action over that category is the read.

## Inventory — what emitted inline script, and what became of it

| Site | Was | Now |
|---|---|---|
| `App.razor` (Plugins): theme seed, Monaco loader, instance title, prerender hook, reconnect reload, `Blazor.start` | 6 inline `<script>` | each carries the nonce |
| `App.razor`: `<ImportMap />` | inline import map | carries the nonce |
| Step-up pages (core) | one inline `<script>` per page | data block + `/auth/step-up/page.js` |
| `PortalLayoutBase.razor.cs` (Plugins) | `eval("window.dispatchEvent(...)")` | module export `dispatchResize` |
| `SimpleDropdown.razor` (Plugins) | three `eval` calls | collocated module `SimpleDropdown.razor.js` |
| `DevLoginConfirm.razor` (Plugins) | `eval("…submit()")` | collocated module `DevLoginConfirm.razor.js` |
| `NotificationCenterPanel.razor` (Plugins) | anchor on `javascript:void(0)` | `FluentButton` |
| `FileBrowser.razor` (Plugins) | anchor on `javascript:void(0)` | `href="#"` + `@onclick:preventDefault` |
| Content pages under `/api/content` (Google and AppleMusic `authorize.html`, two Essentials design previews) | one inline `<script>` each | sibling `.js` file |
| React client `clients/portal/index.html` | module script with `src` | unchanged — no inline script |
| Blazor framework, Fluent UI, BlazorMonaco interop, Monaco bundle | external files | unchanged — `'self'` |

Pinned by tests: MeshWeaver.Plugins `ContentSecurityPolicyTest` (headers per mode, a new nonce per
request, the nonce in the header is the one the render read, unknown mode refused, report logged
with the query cut, oversized report refused) and `StaticPublicPageRenderingTest` (the real shell,
static and interactive, signed in and anonymous: every inline script carries the response's nonce,
no `on*=` and no `javascript:` — read against a denominator of inline scripts, with a foreign-nonce
negative control); core `StepUpPagesCarryNoInlineScriptTest`.

## What remains before `Enforce`

These are expected to show up as reports in `ReportOnly`, and each must be resolved — or consciously
accepted in this list — before a deployment declares `Enforce`:

- **`'unsafe-eval'` consumers among the vendored libraries.** The bundled Monaco carries no `eval`
  and no `new Function`. `mammoth.browser.min.js` and MathJax's `tex-svg.js` contain `new Function`/`eval`
  call sites; whether any is reached at runtime is what the reports will say.
- **Third-party script origins.** Google Analytics (`CookiePolicy.razor`, from
  `www.googletagmanager.com`) and Apple MusicKit (`AppleMusic/content/authorize.html`, from
  `js-cdn.music.apple.com`) are refused by BOTH policies today — the compatibility policy has never
  allowed an `https:` script source. Either is a decision to add an origin, not a nonce.
- **Scripts rendered inside the interactive circuit** (`NamedAreaView.razor`'s catalog-loaded marker,
  the analytics snippet) never executed: Blazor does not run a `<script>` it inserts into the DOM.
  They are dead code, not violations, and are listed so they are not mistaken for either.
- **Authored content.** A node body or `Controls.Html` fragment carrying an inline handler stops
  working under `Enforce` — that is the point; the sanitizers already strip them on the way in.

## Rolling out

1. Ship with `ReportOnly` everywhere (this change).
2. Read the report lines per instance for at least one full working week (`Logs` instance action on
   the category above). Each distinct `(directive, blocked, source)` is either fixed at its source or
   added to the list above with its reason.
3. Declare `Enforce` on the control instance's own record first, then the public instance, then the
   fleet — one record change each, reversible by the same change.
4. Once every record enforces, the compatibility policy and the mode are retired.
