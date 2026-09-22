---
Name: The Plugin Registry Index's Per-Request Cost
Category: Architecture
Description: >-
  The authenticated bundle index evaluates entitlement per package and runs mesh queries on every
  request, measured at 12-19 s to first byte for an 8.7 KB document. That one cost surfaces as two
  unrelated-looking incidents because it meets two different ceilings — a 10 s auth budget that
  answers 503, and an index-assembly stage with no bound at all that answers nothing. Why widening
  a budget moved the symptom rather than fixing it, and the hypothesis that was refuted on the way.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="10"/><polyline points="12 6 12 12 16 14"/></svg>
---

# The Plugin Registry Index's Per-Request Cost

**`GET /api/plugins/bundles/index.json` evaluates entitlement per package and runs mesh queries on
every request.** For an 8.7 KB document that was measured, from inside two production portals, at
~0.03 s to connect and then **12-19 s to first byte** — and the cost grows with the number of
published packages. The measurement is recorded in the code that had to work around it,
`memex/aspire/Memex.Portal.ServiceDefaults/ServiceDefaults.cs`.

This page exists because that single cost produced **two incidents that look unrelated**, and
because the obvious remedy for each is the one thing that must not be done.

## One cost, two ceilings, two symptoms

The handler is `memex/Memex.Portal.Shared/Api/PluginBundleEndpoints.cs` — `/index.json` mapped at
`:149`, `Index(...)` at `:1009`. Note the tree: it is **core, but under `memex/` rather than
`src/`**, which is why searching `src/` for it finds nothing.

It runs two sequential stages of live mesh reads:

| stage | work | ceiling | what a stall looks like |
|---|---|---|---|
| **auth filter** | `InstanceRegistryAuthenticator.Resolve` — a children listing plus two per-node mirrors | **10 s per leg** (`ReadBudget`, `src/MeshWeaver.PluginCatalog/InstanceRegistryAuthenticator.cs:54`) | a real **503 + `Retry-After`** (`InstanceAuthResponses.cs:41`), at ~10,000 ms |
| **index assembly**, only after auth succeeds | `Servable()` `:1192` → `InstalledPackages` `:2149` and `HeldPartitions` `:1325`, both `IMeshService.Query<MeshNode>` awaiting `QueryChangeType.Initial`, plus per-package entitlement `Decide(...)` | **none** | **nothing at all** — no status line, no headers — until the *caller's* budget tears the connection down |

`grep -c '\.Timeout(' PluginBundleEndpoints.cs` returns **`0`**. Not one bound in 2,194 lines.

So the same slow dependency reads as either *"the registry returned 503, retries exhausted"* or
*"operation timed out after 180001 ms with **0 bytes received**"*, depending purely on which stage it
happened to stall in. Filed as two issues, triaged separately, with different-looking causes.

🚨 **The `~10,000 ms` in the first symptom is not a coincidence and not a client timeout.** It is
`ReadBudget` elapsing server-side. A client-side timeout would surface as a cancelled attempt, not as
a recorded `Result: '503'` — a resilience pipeline records a status only for a response that actually
carried one.

## The fast path is fast because it never touches the mesh

`AuthenticateToken` returns `Observable.Return(InstanceAuthResult.Resolved(null))` **synchronously**
for a missing or malformed key. That is why an unauthenticated probe answers 401 in ~0.13 s while the
authenticated request hangs for three minutes — and why "the host is up, so this is not the server"
is the wrong inference. **An unauthenticated probe of this endpoint measures nothing about the
authenticated path.** Both share a hostname and almost no code.

The corollary for diagnosis: `/api/version` answering in 0.12 s, and the 401 answering in 0.13 s, are
both consistent with the authenticated handler being completely stuck.

## 🚨 Why this is a CQRS violation, not just a slow endpoint

`Query` / `ObserveQuery` are eventually consistent, and
[CQRS and Content Access](../CqrsAndContentAccess) limits them to listing children, searching by
predicate and autocomplete — *anywhere a stale negative is harmless*. Assembling a per-request HTTP
response is none of those: the answer must be correct, it must be prompt, and the caller has no way
to tolerate a slow negative. Two `IMeshService.Query<MeshNode>` calls awaiting their `Initial` frame
on the hot path is the defect, and the absence of a timeout only decides which symptom it produces.

## The remedy, and the two that are forbidden

**Take the work off the per-request path.** Assemble the servable index once per publication /
entitlement change and serve that, rather than evaluating entitlement per package and querying the
mesh per request. Then **give every live read that remains a declared bound**, so a slow dependency
fails fast and visibly instead of holding a connection open with nothing written.

Forbidden, and one of them has already been tried:

- **Widening a client budget.** The attempt budgets were **already** raised to 30 s
  (`plugin-registry`) and 120 s (`plugin-registry-bundles`) at `ServiceDefaults.cs:140,150`,
  precisely so the default 10 s client timeout would stop firing on a 12-19 s TTFB. That widening
  worked, in the sense that the client stopped cancelling — and the result was that the *server's*
  503 became visible instead, at scale, for weeks. **The bound that was widened did not fix
  anything; it changed which symptom got filed.** That is the strongest available argument against
  widening the next one.
- **Raising a retry count.** The server answers a genuine 503 after its own budget. Three more
  attempts meet the same ceiling.
- A watchdog re-polling a frozen catalog, or a `catch` that continues, would each bury it further.

## The second defect: exhaustion leaves no readable mark

When the attempts do exhaust, nothing an operator can see records it:

- `InstanceAutoRegistrationService.AdoptPrebuilt` → `AbsorbUnlessPrebuiltRequired` catches everything
  except `PrebuiltRequiredException`, logs at **`LogInformation`** — *"no prebuilt assemblies adopted
  — compiling instead"* — and returns 0;
- `RegistryUpdateReconciler.ReconcileModules` is failure-tolerant per package and leaves the module
  set at its current version.

So the catalog is left **frozen at its last-known-good state with no incident, no node and no
readable status** — an installation quietly serving a stale catalogue, visible only as an
Information line in a pod log. This is separable from the cost and survives fixing it: a registry
that cannot serve should leave a mark somewhere an operator reads.

## A hypothesis that was refuted, recorded so it is not re-run

A drift check measured `PluginCatalog__RegistryUrl` as **live-empty** on the same namespace as the
503s, which looked like a strong candidate cause. **It cannot be**, and the argument is worth keeping
because it generalises:

- no `BaseAddress` is configured for the named clients, so an empty base yields a **relative URI**;
- `HttpClient` throws `InvalidOperationException` **synchronously**, before any network I/O;
- a resilience pipeline records a **status** only for a real HTTP response. An exception is a
  different outcome and is not the same telemetry field.

So an empty registry URL **cannot** produce a recorded `503`. It is nevertheless a real, separate
configuration defect — and note the shape: **a key present with an empty value is invisible to any
"is it configured?" guard that tests for null or absence.**

The generalisation: *before accepting a cause, check that it can produce the exact observation you
have* — not merely a failure. "Something is misconfigured and something is failing" is not a
mechanism.

## Reading an incident record of this, with care

The 503 log lines carry **no exception**, so per
[Red-Log Watching & Ticketing](../LogWatchTriage) the folding identity falls back to the masked
**log** message — and masking replaces the resilience-pipeline source name and the execution time
with placeholders. Every distinct shape therefore folds into **one** fingerprint: two named pipelines
at ~10,000 ms and an unnamed one at ~1.4 ms became a single incident with a single count.

🚨 So a large occurrence count on this fingerprint is **not** a count of one defect, and a fix
verified against one shape can leave another firing with the counter still climbing. Read the
samples, not the total.

## Related

- [CQRS and Content Access](../CqrsAndContentAccess) — what `Query` is and is not for
- [Plugin Registry](../PluginRegistry) — the endpoints, the credential model, and who may read the catalog
- [Red-Log Watching & Ticketing](../LogWatchTriage) — the folding identity, and why masking merges these shapes
