---
Name: Notifications — Satellites, the Bell, and Routing
Category: Architecture
Description: How completion notifications work end-to-end — Notification satellite nodes, the reactive bell, mark-as-read via stream.Update, and rule-based routing to email/Teams.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M18 8A6 6 0 0 0 6 8c0 7-3 9-3 9h18s-3-2-3-9"/><path d="M13.73 21a2 2 0 0 1-3.46 0"/></svg>
---

# Notifications

A notification is **just a mesh node** — a satellite under the thing it notifies about. Nothing about the pipeline is special-cased: creation is a node create, the bell is a reactive query, mark-as-read is a `stream.Update`, and routing to external channels is an agent reading rule nodes. Everything composes from primitives you already know.

```mermaid
flowchart LR
    A[Agent round completes] -->|NotificationService.CreateNotification| B["{threadPath}/_Notification/{id}"]
    B -->|satellite routing| C[(notifications table)]
    B -->|reactive query| D[🔔 Bell]
    D -->|click → stream.Update IsRead| B
    B -->|NotificationTriage agent + rules| E[Email / Teams]
```

## 1. Emitting — a satellite create

When a thread round reaches a terminal state, `ThreadExecution.EmitCompletionNotification` creates a `Notification` node under the thread (the same surface is open to any feature):

```csharp
NotificationService.CreateNotification(
        meshService,
        mainNodePath: threadPath,                    // satellite of the thread
        title: $"\"{threadName}\" is ready",
        message: preview,                            // first 120 chars of the response
        type: NotificationType.General,
        targetNodePath: threadPath,                  // where clicking navigates
        createdBy: agentName,
        icon: "/static/NodeTypeIcons/chat.svg")
    .Subscribe(_ => { }, ex => logger.LogWarning(ex, "notification failed"));
```

The node lands at **`{mainNodePath}/_Notification/{id}`** with `MainNode = mainNodePath`, and the `_Notification` path segment routes persistence to the dedicated **`notifications`** satellite table. Creation is fire-and-forget: a failed notification never fails the round.

🚨 **Who can see it is decided by the PATH, not by `MainNode`.** No `SatelliteAccessRule` is registered for `Notification`, so `RlsNodeValidator` falls through to the ordinary path-based permission fold on the notification's own path — a notification is visible to whoever can read the node it was written under. That is why an "Update available" notification written under a plugin record reaches every viewer who can read the plugin catalog. The fix is not another rule but the data model: see [Addressed Notifications](/Doc/Architecture/AddressedNotifications).

## 2. The bell — a reactive query

The portal's notification center subscribes once and re-renders on every change — new notifications appear without polling, and the unread badge is just a count over the same emission:

```csharp
MeshQuery.Query<MeshNode>(
        MeshQueryRequest.FromQuery("nodeType:Notification sort:CreatedAt-desc"))
    .Subscribe(change =>
    {
        notifications = change.Items?.ToList() ?? [];
        InvokeAsync(StateHasChanged);
    });
```

This is the **set** side of CQRS — a query is right here because the bell wants *all* notifications the user can see, live. (For one specific thread's notifications: `path:{threadPath}/_Notification scope:children nodeType:Notification` — filtering by `nodeType` keeps the result robust when other satellite types live under the same thread.)

🚨 **That shape names no partition, so on Postgres it UNIONs every partition schema** — the single largest cross-schema fan-out measured on memex-cloud (444 unions per five minutes), grandfathered today by the storage layer's shrink-only `unanchored-queries.allow` rather than refused. It cannot simply be pinned to the viewer's partition: notifications are not written there. [Addressed Notifications](/Doc/Architecture/AddressedNotifications) is the design that makes the anchor possible, and [Cross-Schema Fan-Out Elimination](/Doc/Architecture/CrossSchemaFanOutElimination) is the wider census.

## 3. Mark-as-read — `stream.Update`, like everything else

Clicking a notification navigates to its `TargetNodePath` and flips the scalar through the canonical mutation API:

```csharp
Hub.GetMeshNodeStream(node.Path)
    .Update(n => n with { Content = ((Notification)n.Content!) with { IsRead = true } })
    .Subscribe(_ => { }, ex => Logger.LogWarning(ex, "mark-read failed"));
```

A scalar flip is race-safe across mirrors (RFC 7396 merges object keys), so the bell, the panel, and any other reader converge on the next emission.

## 4. Routing beyond the bell — rules, channels, triage

Where a notification *also* goes is the user's data, not code:

| Node type | Lives at | Holds |
|---|---|---|
| `NotificationRule` | `{user}/_NotificationRule/…` | Plain-English routing intent ("approvals → Teams immediately", "thread completions → email digest"), with `order` precedence |
| `NotificationChannel` | `{user}/_NotificationChannel/…` | A channel: `kind` (`InApp` / `Email` / `Teams`), optional `target`, `enabled` |

The **[NotificationTriage](/Agent/NotificationTriage)** agent reads the recipient's rules and channels, applies them to the event, and dispatches to the chosen channels — email delivery rides [Sending Email](/Doc/Architecture/SendingEmail). Users manage their rules and channels in settings — see [Notification Preferences](/Doc/GUI/NotificationPreferences).

This whole lane — the two node types plus the `NotificationTriageService` watcher that starts the agent — ships as the **`MeshWeaver.Notifications.Channels` module** ([Modules](/Doc/Architecture/Modules)); the bell and the deterministic email preferences stay core. The watcher self-skips unless `Email:Enabled`.

## Cross-references

- [Satellite Entity Patterns](/Doc/Architecture/SatelliteEntityPatterns) — the satellite shape notifications follow.
- [Thread Operations](/Doc/Architecture/ThreadOperations) — where completion emission sits in the round lifecycle.
- [CQRS — Queries vs. Content Access](/Doc/Architecture/CqrsAndContentAccess) — why the bell queries but mark-as-read streams.
- [Addressed Notifications](/Doc/Architecture/AddressedNotifications) — where notifications actually live today, and the design that lets the bell name its partition.
- Implementation: `src/MeshWeaver.Graph/NotificationService.cs` (core) · `src/MeshWeaver.Blazor.Portal/Components/NotificationCenter.razor` / `NotificationCenterPanel.razor` and `NotificationQueries.cs` (**MeshWeaver.Plugins** — the Blazor portal shell lives there).
