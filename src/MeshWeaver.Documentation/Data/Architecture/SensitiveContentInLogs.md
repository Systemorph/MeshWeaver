---
Name: Sensitive Content Stays Out of Logs
Category: Architecture
Description: Node content may be sensitive — health data, personal data — and a log travels further than the store that holds it, to Loki and from Loki into filed GitHub issues. So the platform logs a message's IDENTITY, never its body; a deployment opts in to body rendering explicitly; and every path that moves content off the instance (GitSync export, incident filing, feedback hand-over) has a deployment key that turns it off.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="3" y="11" width="18" height="11" rx="2"/><path d="M7 11V7a5 5 0 0 1 10 0v4"/></svg>
---

# Sensitive Content Stays Out of Logs

Node content is whatever an installation stores, and an enterprise client may store health or
personal data in it. The store guards that content with access control. A **log line has none**:
it is shipped to Loki, read by whoever reads the instance's logs, and — through
[Red-Log Watching & Ticketing](../LogWatchTriage) — pasted into GitHub issues. A body that reaches
a log has left every access rule the store applied to it.

So the rule is:

> **A log line names a message; it never carries one.** It carries the type, the id, sender →
> target and the delivery state — enough to find the message and the code that handled it — and
> not the body. Rendering a body is a deployment's explicit, documented opt-in, off by default.

And beside it: **every path that moves node content OFF the instance has a deployment key that
turns it off**, so an instance holding sensitive content can close the egress without a code
change.

## What a log line carries

`MessageService` writes one line about a delivery on each of these paths, and every one of them
renders the delivery through `LogSummary` — `SensitiveRequest (ID: …, client/1 -> host/1, Failed)`:

| Path | Level | Carries |
|---|---|---|
| A handler threw | Error | the summary and the exception |
| A handler outlived the disposal timeout | Warning | the summary and the duration |
| No handler matched the message | Warning (the delivery failure) | the message's type name and id |
| Start-processing / deserializing | Debug | the summary (unchanged — this was already the rule on the hot path) |

Two shapes that used to carry the body, and no longer do:

- The handler-fault Error rendered the whole delivery as JSON (`LogText`). The logging serializer
  strips only members marked `[PreventLogging]` — `MeshNode.Content` among them — so every other
  field of the message (a request's own properties, an unmarked payload) reached the log.
- The no-handler failure appended `{handled.Message}` to its text. A C# record's `ToString()`
  prints **every** property, so the failure text — logged at Warning and sent back to the sender —
  held the whole message.

The exception is still logged in full: an exception's message is the handler's own text, not the
payload. What this page does **not** establish is that no handler ever puts content into its
exception message; a handler that does is a defect in that handler.

### The opt-in: `Messaging:LogMessageBodies`

| Key | Default | Effect |
|---|---|---|
| `Messaging:LogMessageBodies` | `false` | `true` renders the delivery body (through the logging serializer, `[PreventLogging]` members still stripped) on the handler-fault Error and the disposal-timeout Warning. Anything other than a literal `true` keeps bodies out. |

It is meant for a development mesh or a short, deliberate diagnosis on an instance that holds no
sensitive content. Never set it on an instance that stores personal or health data: the log lines
it produces reach Loki and, from there, any issue a red line is filed into.

## Egress switches

| Egress | Key | Default | Off means |
|---|---|---|---|
| GitSync export (mesh → repository) | `GitSync:Export:Enabled` | on (absent = `true`) | every export is refused with `gitsync.policy.exportDisabled`; imports are unaffected. Any value but `true` is OFF — a typo in an off switch never reads as on. |
| GitSync partitions | `GitSync:AllowedPartitions` | absent = every partition | set (an array, or one comma-separated value; an entry ending in `*` matches by prefix), a sync source (`{partition}/_GitSync`, `{partition}/_GitSync/{id}`) outside the list can be neither created nor updated, and no export, import or re-import runs for it (`gitsync.policy.partitionNotAllowed`). |
| Incident filing (log → GitHub issue), feedback hand-over to a control instance | MeshWeaver.Plugins keys | on | documented on the Observability and Feedback pages in MeshWeaver.Plugins, which own those paths. |

🚨 **The GitSync allow-list binds the platform as well.** A system-synced module Space provisioned
by module discovery is a partition like any other: an instance that sets
`GitSync:AllowedPartitions` lists those partitions too, or their sync sources are refused at
creation and every update of their last-sync state is refused with them. Deleting a sync source is
always allowed.

Both GitSync keys are read once, when the service is created; a change reaches a running instance
with its next restart.

## Where it is pinned

- `MessageBodiesStayOutOfLogsTest` / `MessageBodiesOptInTest` (`test/MeshWeaver.Messaging.Hub.Test`)
  — a handler fault and a no-handler failure carrying a marker string in the message: no Warning or
  Error line contains it by default, and the opt-in renders it.
- `GitSyncPolicyReadingTest` / `GitSyncHonoursTheDeploymentPolicyTest` (`test/MeshWeaver.Hosting.Test`)
  — the key parsing, and on a live mesh: a sync source outside the list cannot be created, an
  import into such a partition is refused, and with exports off an allowed partition does not
  export.

## See also

- [Red-Log Watching & Ticketing](../LogWatchTriage) — how a red log line becomes a GitHub issue.
- [GitHub Sync](../GitHubSync) — the sync itself; section 7 for operator setup.
