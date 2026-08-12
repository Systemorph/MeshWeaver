---
Name: Moved-Node Redirects — Keeping Links Alive After a Move
Category: Architecture
Description: The Redirect node type — a declarative, subtree-applying redirect left at a retired path, which surfaces follow it and which deliberately do not, how loops and chains terminate, and why it can never widen access.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M10 13a5 5 0 0 0 7.54.54l3-3a5 5 0 0 0-7.07-7.07l-1.72 1.71"/><path d="M14 11a5 5 0 0 0-7.54-.54l-3 3a5 5 0 0 0 7.07 7.07l1.71-1.71"/></svg>
---

# Moved-Node Redirects

`MoveNodeRequest` moves a node and leaves **nothing behind**. That is correct for the node itself and disastrous for everything that pointed at it: bookmarks, markdown links, search hits, agent references, another repo's cross-links. Retiring one module in MeshWeaver.Reinsurance meant 465 files with 30 external references, and there was no way to keep any of those links working.

A **`Redirect` node** is what you leave behind. It is an ordinary mesh node at the old path whose `NodeType` is `Redirect` and whose `Content` is a `NodeRedirect` naming the new home:

```json
{
  "id": "UWDeepfield",
  "namespace": "Reinsurance",
  "nodeType": "Redirect",
  "name": "UW Deepfield",
  "content": {
    "$type": "NodeRedirect",
    "targetPath": "Reinsurance/Underwriting",
    "scope": "Subtree",
    "reason": "Merged into Underwriting"
  }
}
```

Browsing to `Reinsurance/UWDeepfield/Pricing/Rates` now lands on `Reinsurance/Underwriting/Pricing/Rates`, with the browser URL rewritten to the new address and a notice telling the reader they were moved.

It is **pure data**. A repo declares one by committing a node file — no framework change, no configuration lambda, and re-importing the same declaration is idempotent. Deleting the node removes the redirect.

## The declaration

| Field | Meaning |
|---|---|
| `targetPath` | Where this content now lives. Leading `/` optional. Required — a declaration with no target is inert and says so. |
| `scope` | `Subtree` (default) covers the declaring path **and everything under it**. `Exact` covers only the declaring path. |
| `reason` | Optional free text shown on the redirect's own page ("Merged into Underwriting"). Author-supplied, displayed verbatim, not translated. |

`Subtree` is the default because deep links are the reason the mechanism exists. One declaration at the retired root moves the whole tree; a root-only redirect would leave every bookmark below it dead, which is the situation you were trying to fix.

The target does **not** have to be a node. `Underwriting/Overview` is a perfectly good destination when `Overview` is a layout **area** of `Underwriting` — the resolver rewrites the path and lets the normal page machinery decide what renders.

## Which surfaces follow a redirect

This is the design decision that matters most, and it is deliberately **not** uniform.

| Surface | Follows? | Why |
|---|---|---|
| GUI navigation (browser URL → page) | **Yes** | The whole point. Implemented in `IPathResolver.ResolveNavigationPath`, alongside the legacy `/User/{id}` home rewrite that already lives there. |
| Agent navigation (`navigate_to`) | **Yes** | It goes through the same `ResolveNavigationPath`, so it inherits the behaviour rather than reimplementing it. |
| Markdown links, search-result clicks, menus | **Yes, transitively** | They all produce a URL, and the URL is what navigates. No search-specific or link-specific code exists — nor should it. |
| Message routing (`ResolvePath` / `ResolveRoute`) | **No** | See below. |
| Node reads (`GetMeshNodeStream`, `IMeshService.Query`) | **No** | See below. |
| Writes, `MoveNodeRequest`, `DeleteNodeRequest` | **No** | A write must land where the caller addressed it, full stop. |
| The search index | **No** | It indexes what is actually stored. A `Redirect` node appears in search as itself — a small tombstone naming the new location — and clicking it navigates, which redirects. |

**Why routing and reads stay literal.** `RoutingServiceBase.RouteMessage` carries a large banner forbidding any fallback from a requested address to an ancestor, because every "small" exception has caused silent data corruption: copy operations that skip writes thinking the target exists, reads that hand back ancestor data as if it were the requested node. A redirect that fired on reads would be exactly that bug with a nicer name — `GetMeshNodeStream("Old/X")` would answer with `New/X`, and a caller that then wrote through the same handle would write to a node it never named. Browsing is the one surface where "take me where this went" is unambiguously what the caller meant, so it is the one surface that follows.

The practical consequence is worth stating plainly: an **agent** or an **MCP client** that `get`s the old path gets an honest "not found" plus the tombstone node itself, whose content names the destination. It does not silently receive different content than it asked for.

## Chains, loops and the hop cap

A chain is followed: `A → B → C` collapses to `C` in a single resolution, so the browser performs **one** navigation rather than bouncing through each hop.

Termination is proven two ways, because each is the other's backstop:

- **A visited-set** catches every cycle — `A → B → A`, the degenerate `A → A`, and the `A → A/child` descent that would otherwise re-enter the same declaration forever.
- **`NodeRedirectRules.MaxHops`** (8) bounds the acyclic-but-long chain, which the visited-set alone walks quite happily. Every hop is a live resolution query, so an unbounded walk is unbounded work on a navigation — the shape that wedges a hub.

A chain that stops short **fails loudly and lands somewhere useful**. The resolver logs at `Error` naming the full chain, and returns the last `Redirect` node's own resolution tagged with a `RedirectDiagnostic`:

| `RedirectDiagnostic` | Cause |
|---|---|
| `Loop` | The chain revisited a path it had already been through. |
| `DepthExceeded` | Still redirecting after `MaxHops` hops — collapse the chain by pointing the first declaration at the final target. |
| `TargetMissing` | The chain had nowhere to go — the declaration carries no `targetPath` (or a blank one), **or** it names one that resolves to nothing at all. |

`TargetMissing` deliberately does **not** cover a target that resolves to an ancestor with an unmatched remainder: that is followed, because a destination may legitimately name a layout *area* rather than a node (`Underwriting/Overview`) and the resolver cannot tell that apart from a dead deep path. The fallback below handles the dead case at the point where the answer is actually known.

Because the failure is a **value** on `AddressResolution`, the GUI reads it and the tests assert on it — nobody has to grep a log to find out what happened. The viewer lands on the redirect node's own page, which names the intended destination and links to it: a dead end with a signpost beats a dead end.

## 🚨 A redirect is not an access-control bypass

A `Redirect` node is discoverable by design — it sits where people browse and its job is to name a path somewhere else. If following one were treated as authorisation to read what it names, then anybody who could see the tombstone could read the destination, and every retirement would quietly become a privilege escalation.

It cannot, because **a redirect rewrites a path and confers nothing**. Path resolution has always run under a system bypass — that is how every existing mesh page reaches the gate at all — and the enforcement points are all downstream of it: the anonymous gate in `NavigationService.ProcessResolvedPath` and row-level security on the content read. Both evaluate on the **final** path, for the **arriving** user. Arriving via a redirect therefore gives you exactly what typing the destination URL gives you, no more.

`NodeRedirectAccessTest` pins this as an A/B on the same destination reached the same way: `carol` may read it and must; `bob` may not and must not — including through a subtree declaration, where a leak would leak the whole tree rather than one node.

## Nearest-existing-ancestor fallback

Separately from any declaration, a URL that names something which is **not there** now lands on the nearest ancestor that **is**, instead of a dead end. `Underwriting/X/Y` where `Underwriting/X` exists but `Y` does not shows `Underwriting/X`, with a notice saying the requested path does not exist.

The resolver already computes both halves — `AddressResolution.Prefix` **is** the closest existing ancestor and `Remainder` is what did not match, which is why the old failure text could say *"No node found at 'Admin/Menu/Default'. Closest ancestor is 'Admin' (remainder='Menu/Default')"*. The fallback consumes that rather than re-deriving it.

Three limits keep it from becoming a mask, because a fallback that fires too widely is worse than none. They live in `AncestorFallbackRule` as a **pure predicate**, so they are unit-asserted rather than asserted in a comment:

1. **Only on the typed absence outcomes** — `ErrorType.NotFound` (routing found no node at the address) and `ErrorType.Ignored` (the target hub has no handler, i.e. the area does not exist). This is exactly the pair the navigation layer *already* collapsed into its page-not-found branch, so the fallback does not widen the class of failures treated as absence; it only changes what the viewer is shown for a class that already read as "page not found".

   Everything else keeps failing **with its own reason**: `Unauthorized` / `Forbidden` (a denial), `Unavailable` (**no verdict was reached** — presenting that as absence is a fabricated negative, issue #974), `Unknown` (the enum's default, and the value an unclassified `d.Failed(reason)` refusal carries), timeouts, and any other exception. Turning "you may not see this" into "here is something else" would be strictly worse than the dead end, because it *looks* like an answer.

   This is also why the fallback is **not** hooked onto the post-load `node is null` branch: that branch still cannot tell a refusal from an absence. Issue #1253 / PR #1279 fixed one source of that ambiguity — the compile path's `@@` include reads, where a refused `GetDataRequest` arrived as `null` — but the ambiguity in the branch itself remains, so a fallback there could mask denials.
2. **Only when a remainder was left over** — i.e. the URL genuinely named something deeper than any existing node. A bare existing path that fails is a real failure and stays one.
3. **Only one hop, to a different path.** The ancestor's own load has no remainder, so limit 2 stops it falling back again: this cannot walk the tree and cannot loop.

The original diagnostic is **not swallowed** — the real failure is logged at `Warning`, naming the path that was missing and the ancestor chosen, before the viewer is moved. The `NamedAreaView` "target is gone" placeholder is untouched.

**Scope: browser navigation only, and there is no opt-out to remember.** The fallback lives in `NavigationService` — not in `IPathResolver`, not in routing, not in queries, not in `GetMeshNodeStream`. Existence checks (a sanctioned query use) and every probe-style caller that legitimately treats `NotFound` as an answer are therefore unaffected **by construction**, rather than by passing a flag at each call site. That is the reason to scope it by surface instead of adding a `followAncestors: false` parameter: a flag is something a new caller can forget, and the failure mode of forgetting it is a probe that silently reports the parent as if it were the thing it was looking for. Where absence is an answer, absence is still the answer; where absence is a dead page, the user gets somewhere useful.

## Telling the user

Serving a different page than the address bar promised, without saying so, is how people conclude the product is broken. So both kinds of redirect:

- **rewrite the browser URL** to the destination (`replace: true`, so Back does not bounce off the redirect). This is not cosmetic: a page rendered under a foreign URL resolves its own **relative** links against that foreign path, so every `../Sibling` in a moved subtree would break — and the stale bookmark would never heal.
- **show a dismissible notice** naming where the reader came from: *"Moved here from `Reinsurance/UWDeepfield/Pricing`"* or *"`…` does not exist. Showing the closest place that does."* Both strings are in the `redirect.*` keys of the `en`/`de` catalogs.

`NavigationContext.RedirectedFrom` and `RedirectKind` carry this to the page; `NavigationService` holds the pending notice as circuit-scoped instance state across the one navigation that delivers it — never a `?redirectedFrom=` query parameter, which on a single-segment target would be read as a Blazor **page** route and drop the navigation entirely.

## Retiring a module — the recipe

1. Move or merge the content (`MoveNodeRequest`, or a repo-level restructure).
2. Commit **one** `Redirect` node at the retired root, `scope: Subtree`, pointing at the new root.
3. Do **not** add per-page redirects. The subtree declaration covers them, and a page-per-page pile is a maintenance burden that drifts.
4. Where a former child genuinely went somewhere else, add a deeper `Redirect` for that one path — a more specific declaration wins, because resolution matches the deepest existing node first.
5. Update the in-repo links you own anyway. The redirect is for the links you **don't** own.

## See also

- [Data Access Patterns](/Doc/Architecture/DataAccessPatterns) — the literal read/write surfaces that deliberately do not follow redirects.
- [CQRS and Content Access](/Doc/Architecture/CqrsAndContentAccess) — why a single-node read must be authoritative and literal.
- [Access Control](/Doc/Architecture/AccessControl) — the gate a redirect hands the viewer to.
- [Adding a New Node Type](/Doc/Architecture/AddingANewNodeType) — how `Redirect` is registered.
- [Localization](/Doc/Architecture/Localization) — the `redirect.*` catalog keys.
