---
Name: A record-driven deploy no longer stops before Helm
Category: Fix
Description: A Reconcile or Provision run by the hosting operator stopped at its first step — "could not ensure namespace" — because the operator's own narration was handed to kubectl as part of the namespace manifest. The narration now goes where a log line belongs, and the manifest reaches kubectl clean.
Icon: Wrench
Order: -20260908
---

Rolling an instance from its `Deployments/<name>` record — a `Reconcile`, and the `Provision` of a
new instance — starts with the operator making sure the namespace exists. On 2026-09-08 that step
failed on the first record-driven roll of memex:

```
hosting-deploy: ERROR: could not ensure namespace memex
error parsing STDIN: yaml: line 2: mapping values are not allowed in this context
```

## What was happening

The operator narrates every mutation it runs (`  + kubectl create namespace …`) so the run's log
tells you what it did. That narration was written to the same stream as the command's output — and
this particular command's output is a YAML manifest that is piped straight into `kubectl apply`.
kubectl therefore received the narration as line 1 of the manifest and refused line 2.

A `Restart` was unaffected (its steps pipe nothing), which is why a restart could roll the pods
earlier the same day while a reconcile of the same instance could not.

## What it does now

Narration is written to the log's error stream, so the data a wrapped command produces is exactly
what the next command receives. The operator's test suite now asserts this byte-for-byte: a
manifest piped through the wrapper comes out identical, a dry run writes nothing to the data
channel, and the narration is still there — on the stream a log reader sees.

The operator image carrying this fix has to be named on each instance's record
(`operator.image`); until it is, a `Reconcile` or `Provision` on that instance stops at the same
line.
