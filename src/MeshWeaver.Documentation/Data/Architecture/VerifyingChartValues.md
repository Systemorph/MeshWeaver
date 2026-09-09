# A values key can be set, rendered, and still not read

Three different things can be true of a key in a third-party chart's values file, and they look
identical from the outside — helm reports success for all three:

| | the key reaches the render | the component reads it |
|---|---|---|
| **Read** | yes | yes |
| **Dropped by the chart** | no | no |
| **Passed through but not a field** | **yes** | **no** |

Only the first is what you meant. The other two are inert configuration that reads as coverage, and
the third is the one that defeats the obvious check.

## What it cost

On 2026-09-09, `loki-0` was drained with its node at 01:09Z and **every namespace's log history in
the cluster restarted from that moment** ([#3773]). A query at 01:08Z returned both portal pods'
`Application is shutting down...` lines at 01:05:01Z; the same query over the same window at 01:11Z
returned nothing at all, and the pods no longer appeared in
`sum by (pod) (count_over_time({namespace="memex"}[70m]))`. The lines lost were the ones the
incident under investigation ([#3772]) actually needed.

`persistence.enabled: true` was already set, and it was doing its job — it preserves **flushed**
chunks and the index. What dies with the process is whatever the ingester still holds in memory, and
that is covered by a different knob: the ingester **write-ahead log**, plus `flush_on_shutdown`,
which turns a graceful drain into a flush rather than a loss.

## Why "we set the key" was not good enough

The same values file already documented, in prose, a key that is set-able and inert:

> Rendering with `--set promtail.config.wal.enabled=true` yields ZERO occurrences of "wal" in the
> output. Setting it anyway would be a silent no-op that reads like coverage.

That was measured by hand, once, and written down. Nothing re-checked it, and nothing was checking
the Loki side either — so when the ingester WAL keys were added, "they arrive" was a belief. They do
arrive; that was rendered and confirmed. But a belief that happens to be true is not a control, and
the whole point of the incident is that the evidence went missing without anyone being told.

## The trap: the obvious check is vacuous for the component that matters

The natural gate is *render the chart and assert every `<component>.config.<KEY>` the values set
appears in the component's rendered configuration*. That is the right check for **Promtail**, whose
chart builds its config from named fields and silently discards the rest — it catches
`promtail.config.wal` exactly.

For **Loki** it cannot fail. loki-stack merges `loki.config` into its defaults **verbatim**, so
anything you write arrives in the render intact — including a key that is not a field at all.
Measured, with `ingester:` deliberately misspelled as `ingestor:`:

```
render check     Verified: 5 config leaf/leaves … all reach the rendered configuration   exit 0
loki -verify-config   line 18: field ingestor not found in type loki.ConfigWrapper       exit 1
```

The first version of the gate shipped that render check alone. It passed the typo — it could not
fail for the one component [#3773] was about. **A gate that cannot fail for its own subject is worse
than no gate**, because it reports a tick nobody re-examines.

What closes it is handing the rendered configuration to the component's own binary. Loki validates
its config strictly and rejects unknown fields and unparseable values, so
`loki -verify-config` answers the question the render never could.

## The shape that works

`deploy/aks/scripts/check-observability-values.sh` runs both halves, on every pull request
(`Chart Gate` → *Observability values (rendered + binary-validated)*):

```
values.observability.yaml ──▶ helm template (PINNED version) ──┬──▶ every config leaf present
                                                               │    and unaltered?      ← promtail
                                                               └──▶ loki -verify-config  ← loki
```

Three properties are load-bearing:

- **The chart version is pinned.** Until [#3773] `install-observability.sh` ran
  `helm upgrade --install loki grafana/loki-stack` with no `--version`, so every run installed
  whatever upstream had published most recently. Against an unpinned third-party chart a render
  proves nothing about tomorrow: an upstream restructure of `loki.config` could stop templating the
  WAL, and the first symptom would be the next drain losing the logs again. The pin turns an
  invisible upgrade into a deliberate one. `CHART_VERSION` appears in the installer and in the
  checker, and they move together.
- **The validating binary comes from the pin, never a literal.** The image tag is read from the
  pinned chart's `appVersion`, so bumping the chart moves the validator with the thing it validates.
  A hard-coded tag would eventually validate against a binary the cluster does not run.
- **Every failure mode is red, and none is a skip.** helm missing, PyYAML missing, docker missing or
  not running, the chart unfetchable, the render empty, a component's document absent, or fewer
  leaves examined than the file is known to set — each names itself and fails. The check needs the
  network, which is why it is its own job: `Chart invariants` states that everything it reads is in
  this repository, and that property is worth keeping true rather than quietly falsifying.

`deploy/aks/scripts/test-observability-values.py` is the checker's own control — nine cases with
helm and docker stubbed, so it runs offline in the `Chart invariants` job. Its positive case exists
so that a checker which failed everything could not pass it, and deleting either half of the real
checker turns it red.

## What is still not covered

- **Promtail has no WAL.** When Loki is unreachable, Promtail buffers in memory and eventually
  discards. The binary supports a WAL, but this deprecated chart does not pass
  `promtail.config.wal` through at all — that is the inert key above, and the gate now *fails* if
  anyone sets it, pointing at this. Getting it needs a full `promtail.config.file` override or a
  migration to the maintained `grafana/loki` + `grafana/promtail` charts.
- **A drain of `loki-0` is still a read-it-now window.** The WAL narrows the loss; it does not
  remove the gap. `loki-0` sits on the system pool and is drained with its node like anything else,
  and Promtail is buffering with no WAL of its own while it restarts. If you are reading an incident
  and see `loki-0` restart, pull what you need first.
- **This says nothing about the cluster.** It compares the values against the chart, not against
  what is installed. That is [Chart Drift](../ChartDriftSemantics).

## The general rule

> When a values key matters, the question is never "is it set" and rarely "does it reach the
> render". It is **does the component read it** — and for a chart that passes configuration through
> verbatim, only the component itself can answer.

Related: [Chart Ownership and the Runner Pool](../ChartOwnershipAndRunnerPool) ·
[Chart Drift](../ChartDriftSemantics) · [Red-Log Watching & Ticketing](../LogWatchTriage) ·
[Measuring a Live Portal Read-Only](../MeasuringALivePortalReadOnly)

[#3772]: https://github.com/Systemorph/MeshWeaver/issues/3772
[#3773]: https://github.com/Systemorph/MeshWeaver/issues/3773
