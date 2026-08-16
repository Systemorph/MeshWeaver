# MeshWeaver.Notifications.Channels

Notification **delivery channels** as a MeshWeaver module: the user-authored
`NotificationRule` / `NotificationChannel` node types plus the AI **notification triage** watcher
that escalates in-app notifications to the recipient's other channels (email today, Teams next)
per the rules that recipient authored.

The in-app bell is the platform's always-on default and stays in the core
(`MeshWeaver.Graph.NotificationService`). This module adds the *routing-beyond-the-bell* lane:

- **`NotificationRule`** (`{user}/_NotificationRule/{id}`) — plain-English routing intent,
  interpreted by the triage agent.
- **`NotificationChannel`** (`{user}/_NotificationChannel/{id}`) — a delivery channel
  (`InApp` / `Email` / `Teams`, optional `target`).
- **`NotificationTriageService`** — watches new `Notification` nodes and, only for recipients who
  authored rules, starts the cheap `NotificationTriage` agent to decide whether to escalate.
  Self-skips unless `Email:Enabled` (bound from the host's `Email` configuration section).

## Activation

List the DLL under `Modules:Assemblies` (production), or call `AddNotificationChannels()` on the
mesh builder (test fixtures, bespoke hosts) — both share one configure path.

Delisting the module removes the two node types from create/search contexts and stops triage;
existing rule/channel nodes remain as data. Note the compiled residue that stays in the platform:
`NotificationService` still defers its deterministic email to triage when a recipient has rule
nodes, so delisting while users have rules leaves those users on the in-app bell only.
