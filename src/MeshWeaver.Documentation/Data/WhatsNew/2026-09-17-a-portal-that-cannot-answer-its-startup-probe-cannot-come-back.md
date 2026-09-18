---
Name: A portal whose health check grew slow can no longer be left unable to restart
Category: Fix
Description: The startup probe's endpoint had grown slower than the five seconds the probe waits, so a restarted portal could never finish starting — the chart now states the rule, two guards hold it, and the check that spent the time reads its answer instead of re-measuring it.
Icon: Sparkle
Order: -20260917
---

# A portal whose health check grew slow can no longer be left unable to restart

Kubernetes asks a starting container one question — *is everything you need up yet?* — and waits five
seconds for the answer. Until it gets one, it holds back the other two probes, so a container that
cannot answer in time never finishes starting, is killed when its budget runs out, and begins again.
It is the one probe failure a pod cannot recover from by itself.

On 2026-09-17 the endpoint that answers that question had grown to **eight to thirteen seconds** on one
instance, and nobody could see it: it is a census of seventeen checks, the framework logs a healthy
check's duration at a level this fleet filters out, and the endpoint printed only checks that were
*not* healthy. An expensive check that was perfectly healthy appeared nowhere at all. The pods that
were serving had passed startup hours earlier, when it was faster — so everything looked fine until
something restarted them, and then the portal could not come back.

Three things changed, and together they make that state impossible to arrive at silently:

- **The endpoint publishes its own cost.** Line two of the body now reads
  `timing: 10068ms total over 17 check(s), slowest first — required_modules 10068ms; …` — the total
  the probe has to cover, then every check at or above 10 ms by name, then a count of the rest, so
  "the cost is spread" and "I stopped listing" stay different sentences.
- **The check that was spending it now reads its answer instead of re-measuring it.** It asked the
  shared module volume twice for every module the deployment declares, on every single probe. It now
  takes one snapshot that is refreshed when the volume actually changes, which is the same fix the
  reader beside it already had.
- **The rule is written into the chart and held by two guards**, rather than living in a comment: the
  probe's endpoint is a setting, its default is the one that keeps the rollout gate working, and a
  deployment that moves it while claiming that gate now fails before it ships.

Nothing about the timeout was raised. A bigger number would have moved the cliff without removing it —
the endpoint's cost was still climbing while this was being written.
