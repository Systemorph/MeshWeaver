---
Name: Every HttpClient Names Its Pipeline
Category: Architecture
Description: >-
  Source: '-standard//…' in a Polly line was read as "some caller uses an unnamed HttpClient". It
  was the shared defaults pipeline, which every client that does not re-register itself rides under
  one empty instance and one circuit breaker. What it really named, how the instance became the
  client's own name, and why the timeouts behind it are the control instance's restarts.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M20.59 13.41l-7.17 7.17a2 2 0 0 1-2.83 0L2 12V2h10l8.59 8.59a2 2 0 0 1 0 2.82z"/><line x1="7" y1="7" x2="7.01" y2="7"/></svg>
---

# Every HttpClient Names Its Pipeline

**A Polly line that reads `Source: '-standard//Standard-AttemptTimeout'` does not mean "a caller
resolved an unnamed `HttpClient`". It means "a client that rides the portal's shared defaults
pipeline" — which, until [#4528](https://github.com/Systemorph/MeshWeaver/issues/4528), was every
client except the two plugin-registry ones, and all of them shared ONE pipeline instance.**

## Why the slot was empty

`ServiceDefaults.AddServiceDefaults` turns resilience on for every client through
`ConfigureHttpClientDefaults(http => http.AddStandardResilienceHandler(…))`. Two facts of
`Microsoft.Extensions.Http.Resilience` (read from the 10.10.0 assembly the portal ships) make the
rest follow:

| Fact | Consequence |
|---|---|
| `AddStandardResilienceHandler` names its pipeline `PipelineNameHelper.GetName(builder.Name, "standard")` = `{builder.Name}-standard` | the builder `ConfigureHttpClientDefaults` hands out has **no name**, so the pipeline is `-standard` for every client it configures — named or not |
| the pipeline registry keys a pipeline by `HttpKey(name, instance)`, and without a selector the instance is `""` | every such client, and every host they call, got the **same** pipeline — one retry budget, one circuit breaker — and every line read `-standard//…` |

So `self-update-handover`, `deployment-report`, `module-published-broadcast`, `OpenGraphPreview`,
the OCI tag listers, and every Plugins client that registers by name (`WebSearchPlugin`,
`TeamsClient`, the Apple/Google/Chess clients…) all logged as the same nobody.
[#1133/#1137](../LogWatchTriage) had fixed the symptom for the registry clients by re-registering them
with `RemoveAllResilienceHandlers()` — which is why `plugin-registry-standard//…` always named
itself, and why the fix below leaves those two alone.

## What it actually was (measured)

Read-only `Logs` InstanceActions on memex.meshweaver.cloud, 2026-09-24:

- **Every stack under the timeouts ends in `Memex.Portal.Shared.SelfUpdate.SelfUpdateHandover.Post`.**
  `Ops/logs-memexcloud-20260924-timeout-stack2` (pod `…589ff8f895-vn49b`, 100 min): each
  `Polly.Timeout.TimeoutRejectedException` is followed by `ResilienceHandler.SendAsync` frames and a
  `SelfUpdateHandover.<>c__DisplayClass37_0.<<Post…` frame — 12:33:06Z, 12:34:44Z, 12:52:31Z,
  13:43:15Z, 13:43:32Z. `Ops/logs-memexcloud-20260924-2x9rq-stack` found the same caller behind the
  06:24–06:30Z samples the recurrence bot quoted (pod `…76cf776847-2x9rq`, a memex-cloud replica).
  The client is the NAMED `self-update-handover` client, posting a signed release hand-over to the
  control instance's inbox `https://memex.systemorph.com/api/hooks/Hosting/PlatformBuilds`.
- **The bursts line up with the control instance's replicas starting.** 24 h of
  `-standard//Standard-TotalRequestTimeout` + hand-over-FAILED lines on memex-cloud
  (`Ops/logs-memexcloud-20260924-totaltimeouts-1440`, 157 lines) against the control instance's own
  `Now listening on` / `Application is shutting down` lines (`Ops/logs-memex-20260924-boots-600`):

  | memex-cloud hand-over failures (UTC) | control instance (memex) replicas |
  |---|---|
  | 05:50–06:30 | two new replicas listening 06:29:58 / 06:30:32, no graceful shutdown logged before them |
  | 07:10–07:20 | shutdown 07:06:42, listening 07:07:40; shutdown 07:20:50, listening 07:22:13 |
  | 08:20 | shutdown 08:21:52, listening 08:22:27 |
  | 09:20–09:30 | shutdown 09:20:49, listening 09:21:30 and 09:32:16 |
  | 10:10 | listening 10:03:11 → 10:08:07 (three replicas) |
  | 12:20–12:50 | shutdown 12:21:01, listening 12:23:27 and 12:47:51 |
  | 13:40 | listening 13:43:32 / 13:43:53 — these attempts answered **503 in 1.3 ms**: an ingress with no ready backend |

  Not every bucket has a start beside it: 07:50, 08:40–08:50 and 13:10 (two to six lines each) do
  not, and the boot read sees only graceful shutdowns and `Now listening` lines — a replica killed
  without a shutdown line, or one alive but stalled, leaves nothing in it. So the target that "does
  not answer" is, in every large burst, a control instance that is restarting — roughly hourly on
  that day — and unresponsive for minutes after it begins listening; the small ones are not
  explained here. The hand-over itself behaves as
  designed: it fails, logs that the next check announces again, and the next check does.

## The fix: the instance is the client's name

`ServiceDefaults.AddHttpClientResilienceDefaults` (called by `AddServiceDefaults`, and by the test
that pins it) now:

1. puts an outermost `DelegatingHandler` on **every** client that writes
   `HttpMessageHandlerBuilder.Name` into `request.Options` (`ClientNameKey`) — registered through
   `ConfigureAll<HttpClientFactoryOptions>`, where the name is known, and inserted at index 0 so it
   runs before any pipeline is selected;
2. adds `.SelectPipelineBy(_ => ClientNameOf)` to the defaults' standard handler, so the pipeline
   **instance** is that name (`(unnamed)` for the factory's default client).

Every line now reads `-standard/self-update-handover/Standard-AttemptTimeout`, and each client
trips its own breaker instead of one shared by all of them.

🚨 **Keyed by client NAME, never by `SelectPipelineByAuthority`.** The defaults also serve arbitrary
URLs taken from user data — an agent's web fetch, an Open Graph preview — and the pipeline registry
keeps every instance it ever built, so a per-host key would grow with whatever hosts users paste.
Client names are fixed in code; the set is bounded.

`EveryClientNamesItsOwnResiliencePipelineTest` (Memex.Portal.Shared.Test) builds exactly what
production builds and reads the identity off the Polly log line itself. Its negative control —
dropping the `SelectPipelineBy` call — reproduces the production form, `'-standard//Standard-Retry'`.

## What this does NOT fix

- **Why the control instance restarts and stalls after boot.** That is the receiving side, and no
  client budget can change it; the correlation above is the evidence to take there. Raising the
  hand-over's attempt timeout would only make each failure slower.
- **The incident identity.** The log watcher's normalizer masked every quoted literal, `Source:`
  included, so every Polly timeout in the fleet folded onto the ONE incident #4528 was linked to.
  Keeping the pipeline and instance in the identity is a change to `LogLineParser.Normalize` in
  MeshWeaver.Plugins (the portal re-derives identities from raw samples, so it needs no watcher roll).
- **The bundle pipeline #4528 was filed for** is almost quiet: over the same six hours the control
  instance logged ONE `Bundle for OgCard@1.1.6 over HTTP … did NOT complete after 119999 ms` whose
  cause is the registry not beginning a response within 120 s — the registry-side time-to-first-byte
  tracked as [#4963](https://github.com/Systemorph/MeshWeaver/issues/4963).

## Related

- [A Name That Does Not Resolve Is Not Transient](../ANameThatDoesNotResolveIsNotTransient) — the other
  carve-out on the same defaults pipeline.
- [Bundle Transfer Budget](../BundleTransferBudget) — the `plugin-registry-bundles` pipeline and its
  silence budget.
- [Log Watch Triage](../LogWatchTriage) — repointing an incident that outlived its issue.
