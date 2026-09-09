---
NodeType: Markdown
Name: CI Emit Reference Capture
Category: Architecture
Abstract: "Opt-in capture of a failed emit's reference files, without changing compiler scheduling or production settings."
---

# CI emit reference capture

Plugins issue #890's failing run `34402293577` did not preserve the ordered references
used by its compiler process. A native standalone control (core run `34414443069`)
then emitted 4,000/4,000 successfully both with default settings and with tiered PGO
disabled. Those controls did not reproduce the failure and do not establish its cause.
Reconstructing a reference set from a portal image would not recover the test runner's
selected TPA, module and NuGet references.

## Explicit opt-in

The NodeType disk-emit path requests a capture only when **both** named variables are
present: `GITHUB_ACTIONS=true` and `MW_CAPTURE_EMIT_REFERENCES=1`. `RUNNER_TEMP` must be
an absolute path. Set the latter flag only on the affected CI test job when requesting
that evidence; it is not enabled by this change. No production configuration is changed.
Ordinary C# diagnostics do not trigger it: Roslyn must throw during emit.

Output is fixed beneath
`RUNNER_TEMP/straggler-logs/emit-reference-capture/process-<pid>/`. The Plugins moved-suite
job already uploads the `straggler-logs` directory recursively. A different runner must
preserve that directory through its existing artifact mechanism. No additional permissions
or credentials are required. The first successful atomic creation of `started.json`
claims that process directory; later emit failures never overwrite the first capture.
Before snapshotting references or enqueueing I/O, each mesh root admits only one request
through an injected atomic reservation shared by its compiler hubs. Cancellation or a
scheduling failure never resets that reservation. Multiple independent mesh roots in a
test process can each enqueue once, but the file sentinel still permits only one artifact
capture for the process. No mutable static registry or actor-side file I/O is introduced.
The request ID in the existing error summary must match the manifest's `captureId`;
a different failure's manifest is not evidence for the current request.

## Scheduling and lifetime

The existing emit signatures retain their default behavior. The NodeType service supplies
an optional failure callback which snapshots only the ordered references and exception type.
It submits a separate leaf through the existing registry's **FileSystem `IIoPool`**, with
the subscription registered to the hub before work starts. Completion releases it; hub
disposal cancels it, and the pool participates in the existing drain. No new pool, untracked
fallback, blocking actor wait or compiler scheduling change is introduced.

The compile does not await the capture or extend its deadline. The original exception
and canary result continue through the existing reporting funnel. A scheduling failure is
reported as incomplete diagnostic evidence and cannot replace the original exception.
If the scope has no registered file pool, capture is explicitly unavailable. Missing or
invalid opt-in inputs leave the callback disabled.

## Evidence and limits

`manifest.json` records each reference's ordinal, kind, aliases, EmbedInteropTypes,
file path, retained-file SHA256 and size, and file versus held-metadata MVIDs. Referenced
PE bytes are stored under their hash. Named process evidence includes actual runtime,
compiler/CoreLib assembly and informational versions, MVIDs, architecture, processor count
and capture thread. Only six named JIT flags are read; values are limited to unset, `0`,
`1` or `other`. No general environment/configuration dump, source text, exception message,
credentials or mesh data is captured.

The writer limits one capture to 1,024 references, 64 MiB per file and 256 MiB of file
bytes read (including repeated references). It checks cancellation between reads/writes.
Unreadable, unsupported, missing, mismatched or over-budget references make the manifest
incomplete. `started.json` itself says incomplete; a missing final manifest remains
incomplete, including a cancelled or failed write. Only the final atomic manifest can
declare completion. A complete capture requires every entry to have been retained and
its file MVIDs to match the held metadata MVIDs.

**Completion is an evidence inventory, not a claim of identical mapped state.** Files are
reread after failure. Matching MVIDs alone cannot prove the bytes are the same ones Roslyn
previously mapped, and a new process cannot reproduce its reference-object caches, JIT
history, heap or interleavings. A changed/missing reference must not be silently substituted
in a replay. Full workload replay would also need generated trees and compiler options;
this minimal capture deliberately provides only what the unchanged canary comparison needs.

Regression tests cover opt-in/default behavior, real PE bytes/properties, incomplete and
cancelled captures, first-capture preservation, the original emit exception, ordinary
diagnostics, and execution on the real file pool rather than an actor's event-loop thread.
