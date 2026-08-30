---
Name: The cluster can no longer hide configuration
Category: Feature
Description: The hosting operator gains hosting-audit — one read-only command that diffs a namespace against its helm release's manifest and reports, by name and never by value, everything that lives only on the cluster: env patches, live-edited ConfigMap keys, sidecars, unmanaged objects, secrets nothing renders, and secret-shaped names carried as plain values.
Icon: ArrowSyncCheckmark
Order: -20260830
---

# The cluster can no longer hide configuration

A `kubectl set env`, a live-edited ConfigMap key, a hand-applied CronJob — none of it fails a
deploy, because `helm upgrade` preserves a live-edited key and never touches an object it does not
own. It fails months later instead: the memex portal crashed at boot because its Email section
existed twice — the chart's ConfigMap defaults and a cluster-only secret patched onto the
Deployment as env — and .NET picks the winner at random per pod start.

The operator image now carries `hosting-audit`. It takes the truth from `helm get manifest` +
`helm get hooks` and reports what the namespace runs that no repository renders: the portal
Deployment's live-only env, envFrom, volumes, mounts, containers and pod-spec patches; the chart
ConfigMaps' live-only and value-differing keys; objects the chart does not own; secrets the pod
reads that nothing renders; and entries whose name looks like a secret carried as a plain value.
Names only — a value is never printed, because some of them are the secrets being hunted.

The verdict is `clean` or `drift`, emitted with the full report for the Hosting plugin's new
`Audit` instance action to record on the mesh. The command refuses rather than guessing: no
release, no live portal, or a kind it is not permitted to list each fail the run — an audit that
could not read a kind must not report it as clean.
