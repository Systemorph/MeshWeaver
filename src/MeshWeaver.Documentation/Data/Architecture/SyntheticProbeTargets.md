---
Name: What a synthetic probe may assert
Category: Architecture
Description: A probe that names one deployment's installed content is broken the moment the fleet has two portals — it went red for eight days for a true statement. The three things a probe may assert (the platform floor, a negative control, the deployment's own declaration), why a probe without a control cannot tell "absent" from "down", and why an empty denominator has to be louder than a failure.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="3"/><circle cx="12" cy="12" r="9"/><path d="M12 3v3M12 18v3M3 12h3M18 12h3"/></svg>
---

# What a synthetic probe may assert

The `Prod synthetic probe` watches the one endpoint that crosses the cross-silo content hop —
`/api/content/{node}/content/{file}`, a collection-config `GetDataRequest` to the owning node's hub.
That is the transport that actually wedges, so the choice of endpoint was right. The choice of
**target** was not, and it cost eight days of red.

## The failure: red for a true statement

The probe's matrix paired each portal with an asset of an installed package:

| portal | target |
|---|---|
| `memex.meshweaver.cloud` | `/api/content/AgenticEngineering/content/og.png` |
| `memex.systemorph.com` | `/api/content/AgenticPrimer/content/og.png` |

`AgenticPrimer` is not installed on the staff portal and never was. So from 2026-08-31 the probe
failed every 15 minutes, for 59 of its last 60 runs, while the portal it was pointed at was
completely healthy — `/healthz` 200, every page rendering, the content route serving other assets
fine.

**A check that is red in an empty room is worse than no check.** It is the same defect as a gate
that skips: after a day nobody reads it, and every real failure it would have caught arrives wearing
the colour everyone has learned to ignore. The eight days were not the bug. They were the cost of the
bug.

## Why it was broken by construction, not by accident

A per-deployment install decision is **not a property of the platform**. The moment the fleet has two
portals with different content, an assertion naming one portal's packages is wrong on the other — and
this repository cannot even see which is which. The deployment inventory is deliberately private
(see `chart-drift.yml`: *"do NOT move the overlays into this public repo to dodge the credential —
that would publish the deployment inventory"*), so a public workflow that hard-codes what a named
portal serves is encoding a private fact it has no way to keep current.

So the target could only ever drift, and when it drifted the probe blamed the portal.

The first instinct — *install the content, or repoint the target* — is a repair of this instance and
nothing else. Installing course content on the staff portal to satisfy a monitor is the tail wagging
the dog; repointing puts a fresh guess in the same slot to drift again. What the probe needed was to
stop guessing.

## The three things a probe may assert

Everything the probe checks is now either **shipped by the platform image** or **read from the
deployment at probe time**. Nothing is hard-coded about what a named portal contains.

### 1. The platform floor — a constant that cannot drift

`/api/content/Doc/Architecture/content/platform-overview.svg`.

Present on every deployment by construction, and each link in that chain is load-bearing: the bytes
are an `EmbeddedResource` in `MeshWeaver.Documentation`, `AddDocumentation` gives every `Doc/*` child
a `content` collection with `isStatic: true` (which is what publishes a collection on this route at
all — the default is *not publishable, and the route answers 404*), and `Doc/_Policy` sets
`PublicRead` so an unauthenticated probe may read it. It resolves through `ContentFileResolver` with
the same longest-node-prefix match and the same collection-config request to the owning hub as any
package asset, so it crosses the hop being watched while depending on no install decision.

This is the **only** assertion that can answer *"is the route healthy"*, because it is the only one
whose expected value is known without asking the deployment anything.

### 2. A negative control — the assertion whose absence made the incident unreadable

A path under the same route that cannot exist, which must answer **404**.

The old probe sampled **one URL three times** and reported `content route unhealthy — 3/3 samples
failed`. Three samples of one URL cannot distinguish *"this asset is missing"* from *"the route is
down"* — they are the same evidence. The issue was filed on exactly that wrong inference, proposing
that the content-serving module had failed to load, and the next two readers each had to re-measure
from scratch to find that the route was fine.

With a control the two separate cleanly:

| platform asset | control | verdict |
|---|---|---|
| 200 | 404 | route healthy, target present — **green** |
| 404 | 404 | the route *discriminates*, so the asset is genuinely absent — a **content** red |
| 5xx / timeout | any | a **route** red |
| any | not 404 | the route answers the same thing for everything — a **route** red |

The route verdict outranks the content verdict, because a wedged route makes every content answer
meaningless. Only once the control has proved the route discriminates does a 404 mean what it says.

### 3. The deployment's own declaration — `/sitemap.xml`

The probe holds each portal to **its own claims** rather than to a guess made in this repository.

`/sitemap.xml` is anonymous and is the only unauthenticated surface that says what a deployment
serves: `SeoEndpoints` enumerates every top-level `Store/Plugin`, `Store/Catalog` and `Space` root
that passes `AnonymousGate`, applied per node and fail-closed. Measured 2026-09-08 it already encodes
the difference the matrix used to guess at — **79** roots on `memex.systemorph.com` (no
`AgenticPrimer`), **101** on `memex.meshweaver.cloud` (with it).

The probe reads that list, samples roots from it by index (first, middle, last — deterministic so a
red is reproducible, spread so the sample is not the same three alphabetically-first names on every
portal), and requires 200. A portal is therefore only ever failed for contradicting **itself**, and
content installed later is covered with no edit here.

## An empty denominator must be louder than a failure

A loop over an empty list exits 0 having asserted nothing, and GitHub paints that green. So the
count is asserted **before** the loop and printed either way — a zero has to be readable rather than
silent. Three distinct reds come out of reading the declaration, and they are deliberately not one
message:

- **the sitemap could not be fetched** — the declaration was not read, so nothing was checked;
- **the response was 200 but is not a sitemap** — see below;
- **a well-formed sitemap declaring zero roots** — the portal genuinely publishes nothing anonymously.

### 🚨 A 200 is not a sitemap

Found by falsifying the step rather than by reviewing it. The portal's SPA fallback answers **200
`text/html`** for any unrouted path, so *"the sitemap endpoint is gone"* arrives looking exactly like
*"the sitemap is empty"* — and the second reads as a portal that publishes nothing, which is a
statement about the portal rather than about the probe's own input. The step now asserts the
**shape** (`<urlset` present) before counting, so the two get different messages.

This generalises past this probe: on any host with a catch-all route, an HTTP 200 is not evidence
that the thing you asked for exists.

### 🚨 `code=$(curl … || echo 000)` concatenates

The idiom every step in this workflow used. On a timeout `curl` prints its own `000` **and** exits
non-zero, so the capture reads `000000` — a six-digit status nobody can look up, produced in exactly
the situation where the error message is all the reader has. `code=$(curl …) || code=000` assigns
once. Worth grepping for wherever a status code is captured.

## What this file may never contain again

The workflow's matrix names **hosts**. It may not name a package, a space, or a course. Anything
content-shaped appearing there is this defect returning — and it will not be visible as a defect,
because it will look exactly like a target that used to work.

## See also

- [Probe Semantics](../ProbeSemantics) — the *Kubernetes* probes, which ask a different question
  again: not "is this healthy" but "which remedy should Kubernetes apply".
- [Deployment Inventory](../DeploymentInventory) — why what each deployment serves is private, and
  where it does live.
- [Reading CI Signals](../ReadingCiSignals) — skipped and absent contexts count as satisfied; the
  broader family this red belongs to.
