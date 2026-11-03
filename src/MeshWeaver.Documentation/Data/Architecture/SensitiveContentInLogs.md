---
Name: Sensitive Content Stays Out of Logs
Category: Architecture
Description: Node content may be sensitive — health data, personal data — and a log travels further than the store that holds it, to Loki and from Loki into filed GitHub issues. So the platform logs a message's IDENTITY, never its body; a content property marked [SensitiveData] is redacted by ONE redaction point on every log and egress rendering; a deployment opts in to body rendering explicitly; and every path that moves content off the instance (GitSync export, incident filing, feedback hand-over) has a deployment key that turns it off.
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

## Marking a property: `[SensitiveData]`

```csharp
using MeshWeaver.Messaging;   // a standard using of every in-mesh compile

public record PatientRecord([SensitiveData] string? Diagnosis = null)   // a positional parameter
{
    public string? Ward { get; init; }
    [SensitiveData] public DateOnly? BirthDate { get; init; }           // a property, any type
    public ImmutableList<Visit> Visits { get; init; } = [];             // nested: marked members inside are found
}
```

`SensitiveDataAttribute` (namespace `MeshWeaver.Messaging`, assembly `MeshWeaver.Messaging.Contract`)
is the ONE attribute for sensitive content. It goes on a property, a field, or a positional record
parameter (no `property:` target needed). It changes nothing for an authorised reader: the value is
stored, read, rendered in an area and returned by an MCP `get` exactly as before. It changes every
rendering that leaves the access rules behind — there the value becomes `‹redacted›`
(`SensitiveDataAttribute.Placeholder`; null stays null) and the member stays visible, so a log
still shows that the field was there.

It is found by reflection on the CLR type, so it works on a content type compiled LIVE from a
NodeType's node source exactly as on a platform type: the in-mesh compile references
`MeshWeaver.Messaging.Contract`, and the mesh-wide content-type registry resolves a compiled type's
`$type` for untyped JSON.

`[PreventLogging]` is the older, different control: it OMITS a member from log renderings (built
for large payloads). `[SensitiveData]` replaces the value. Both are honoured by the same logging
serializer.

### The one redaction point

`MeshWeaver.Messaging.Serialization.SensitiveDataRedaction` is the only place that decides what a
rendering may show. Every log/egress path goes through it:

| API | For |
|---|---|
| `hub.GetLoggingSerializerOptions()` | the hub's write-only LOGGING options, built once per hub. `[PreventLogging]` members stripped, `[SensitiveData]` values redacted. |
| `hub.RenderForLog(value)` | any object into a log line or an exception text. Never throws. |
| `hub.RedactJson(json)` | JSON TEXT (a payload, a tool argument); text that does not parse is never echoed — only its length. |
| `hub.SensitiveValues(content)` + `SensitiveDataRedaction.ScrubText(text, values)` | FREE TEXT written about content (a gist an agent wrote): the content's marked values are removed from it. Values under 3 characters and booleans are not collected. |

How the redaction reaches every shape:

- **Typed objects** — a per-type modifier on the logging options' resolver replaces each marked
  member. Nested records, collections, dictionaries and `object`-typed (polymorphic) members are
  each resolved through the same options, so the modifier reaches them.
- **Untyped JSON** (`JsonElement` / `JsonNode` — a node's content before it is materialised) — the
  tree is walked; each object's `$type` is resolved through the hub's type registry and the
  mesh-wide content-type registry, and the members it marks are redacted.
- **A `$type` nothing in the process can resolve fails CLOSED**: every scalar of that object is
  redacted, because which members are sensitive cannot be known.
- 🚨 **The hub's own `JsonSerializerOptions` are never touched.** Persistence and the wire use them,
  so redacting there would blank the stored value. The logging options are a separate, write-only
  instance; reading with them throws.

## Covered paths

| Path | What it does now |
|---|---|
| Message logging, bodies opted in (`Messaging:LogMessageBodies=true`) | the body is rendered through the logging options: marked values redacted. Off (the default), only the summary is logged. |
| A packaged payload in a log or a failure report (`RawJson`, `MessageSizeGuard` refusals, the oversized-NACK omission) | identified by its byte count and `$type` only. The head of the payload used to be quoted, and the first fields of a payload are content. |
| Changed-data Debug logging (`WorkspaceStreams`) | rendered through the logging options (it used the hub's own options). |
| `MeshNode.ToString()` (any `{node}` in a log line or exception text) | prints path, NodeType, version, state and the content's TYPE — never the content, which printed every member through its own `ToString()`. |
| Data-binding conversion failures (`GetDataBoundValue`, `ConvertString`, integer range checks) | name the types and the length, never the bound value — and do not log the exception, whose message quotes its input (`The input string '…' was not in a correct format`). |
| Storage diagnostics (`FileSystemStorageAdapter` tiny-write trace) | the length only. |
| The error-signature digest, incident filing | template-only already (MeshWeaver.Plugins); kept and tested there. |
| MCP server logs, AI thread execution logs, feedback hand-over | MeshWeaver.Plugins — see its Observability and Feedback pages. |

## What is NOT covered

- **Authorised reads.** A node read, an area render, an MCP `get` to a caller allowed to read the
  node return the value. That is access, and access control decides it.
- **Free text.** A node's name, description, path or id, a markdown body, a search query, a chat
  message. The attribute protects PROPERTIES; keep sensitive values out of those.
- **Content without a `$type` and without a typed parent.** Untyped JSON that names no type cannot
  say which of its members are marked; it is rendered as is.
- **A record's own `ToString()`.** A content record printed through its compiler-generated
  `ToString()` (`$"{content}"`) prints every member; render it with `hub.RenderForLog(content)`.
  `MeshNode` overrides its printer; content types do not.
- **Exception messages written by a handler.** An exception a handler throws with a value in its
  text is logged in full wherever the exception is logged. .NET's own parse exceptions quote their
  input; the platform's binding paths no longer log them, but a handler that logs one does.

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
  strips only members marked `[PreventLogging]` (`MeshNode.Content` is deliberately not one), so every
  other field of the message (a request's own properties, an unmarked payload) reached the log.
- The no-handler failure appended `{handled.Message}` to its text. A C# record's `ToString()`
  prints **every** property, so the failure text — logged at Warning and sent back to the sender —
  held the whole message.

The exception is still logged in full: an exception's message is the handler's own text, not the
payload. What this page does **not** establish is that no handler ever puts content into its
exception message; a handler that does is a defect in that handler.

### The opt-in: `Messaging:LogMessageBodies`

| Key | Default | Effect |
|---|---|---|
| `Messaging:LogMessageBodies` | `false` | `true` renders the delivery body (through the logging serializer: `[PreventLogging]` members still stripped, `[SensitiveData]` values still redacted) on the handler-fault Error and the disposal-timeout Warning. Anything other than a literal `true` keeps bodies out. |

It is meant for a development mesh or a short, deliberate diagnosis on an instance that holds no
sensitive content. Never set it on an instance that stores personal or health data: the log lines
it produces reach Loki and, from there, any issue a red line is filed into.

## Egress switches

| Egress | Key | Default | Off means |
|---|---|---|---|
| GitSync export (mesh → repository) | `GitSync:Export:Enabled` | on (absent = `true`) | every export is refused with `gitsync.policy.exportDisabled`; imports are unaffected. Any value but `true` is OFF — a typo in an off switch never reads as on. |
| GitSync partitions | `GitSync:AllowedPartitions` | absent = every partition | set (an array, or one comma-separated value — which, when configured, is the whole list and replaces an array from an earlier configuration layer; an entry ending in `*` matches by prefix), a sync source (`{partition}/_GitSync`, `{partition}/_GitSync/{id}`) outside the list can be neither created nor updated, and no export, import or re-import runs for it (`gitsync.policy.partitionNotAllowed`). |
| Incident filing (log → GitHub issue), feedback hand-over to a control instance | MeshWeaver.Plugins keys | on | documented on the Observability and Feedback pages in MeshWeaver.Plugins, which own those paths. |

🚨 **The GitSync allow-list binds the platform as well.** A system-synced module Space provisioned
by module discovery is a partition like any other: an instance that sets
`GitSync:AllowedPartitions` lists those partitions too, or their sync sources are refused at
creation and every update of their last-sync state is refused with them. Deleting a sync source is
always allowed.

Both GitSync keys are read once, when the service is created; a change reaches a running instance
with its next restart.

## Where it is pinned

- `SensitiveContentNeverReachesALogTest` (`test/MeshWeaver.Hosting.Test`) — a real mesh, a NodeType
  whose content marks a positional parameter, a nested member in a list and a non-string member; a
  node is created, read, updated and rendered while a capture provider records EVERY line at Trace.
  No line, no logging rendering (typed, untyped, `RedactJson`, `ToString()`) carries a marked value,
  the unmarked ones survive, and the hub's own options still write the value. Also: a
  deserialisation failure echoes nothing; a gist is scrubbed of the content's marked values; an
  unresolvable `$type` fails closed.
- `ACompiledContentTypesSensitiveMembersAreRedactedTest` — a content type compiled live from node
  source by the real batch bake: its marked member is redacted typed (across the collectible load
  context) and untyped (through the registry's `$type`).
- `MessageBodiesOptInTest.The_opt_in_still_redacts_a_marked_member` — with bodies opted in, the
  Error line renders the body and still not the marked value.

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
