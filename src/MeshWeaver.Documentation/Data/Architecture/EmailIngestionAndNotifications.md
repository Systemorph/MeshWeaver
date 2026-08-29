---
NodeType: Markdown
Name: "Email Ingestion, Channels and Notifications"
Abstract: "Memex's two-directional channel system: inbound mail as a chat device (pluggable transport — email today, Teams next — feeding one shared agent pipeline) and outbound notifications routed by the recipient's own plain-English rules via a small triage agent. Covers the NodeTypes, configuration, and how to send a notification from your own code."
Icon: "<svg viewBox='0 0 24 24' xmlns='http://www.w3.org/2000/svg'><rect width='24' height='24' rx='4' fill='#00838f'/><rect x='4' y='7' width='16' height='11' rx='2' fill='white'/><path d='M4.5 8l7.5 6 7.5-6' fill='none' stroke='#00838f' stroke-width='1.8' stroke-linecap='round' stroke-linejoin='round'/></svg>"
Thumbnail: "images/DataMesh.svg"
Authors:
  - "Roland Buergi"
Tags:
  - "Architecture"
  - "Email"
  - "Channels"
  - "Notifications"
  - "Agents"
---

# Email Ingestion, Channels & the Notification System

Memex talks to people over real-world channels. Two directions:

- **Ingestion (inbound):** a person emails the portal mailbox and a Memex agent answers — mail used as a
  chat device. Pluggable transport (email today, Teams next) feeding one shared pipeline.
- **Notifications (outbound):** Memex tells a person something happened — in-app, by email, or (next) in
  Teams — routed by **the recipient's own rules**, decided by a small **triage agent**.

This document covers both, the NodeTypes involved, configuration, and how to send a notification from your
own code (with a runnable sample). For the outbound **credential/Graph** setup specifically, see
[SendingEmail.md](/Doc/Architecture/SendingEmail); for the onboarding gate see
[InvitationOnlyOnboarding.md](/Doc/Architecture/InvitationOnlyOnboarding).

---

## 1. Ingestion — mail as a chat device

A person emails the portal mailbox (e.g. `memex@systemorph.com`). A Microsoft Graph change-notification
subscription on that inbox calls back to the portal, which turns each message into an agent conversation.

### Pipeline

```
inbound mail ──▶ Graph subscription ──▶ POST /api/email (webhook)
                                              │
                                              ▼
                                   EmailInboundProcessor.Route
                                              │
                  ┌───────────────────────────┴───────────────────────────┐
            sender is a known Memex user                        sender is anyone else
                  │                                                        │
   Email node {recipient}/_Email/{id}                         Email node Admin/Inbox/{id}
                  └───────────────────────────┬───────────────────────────┘
                                              ▼
                        claim ▸ find-or-start a thread ON the Email node
                            ({emailPath}/_Thread/{id}, Email as MainNode)
                                              ▼
                        the Email Router agent works it, and either replies
                        (an Outbound Email node) or forwards it to info@
```

**This is the mail instance of one pattern the platform runs three times** — a red log becomes a
`LogIncident` and a triage thread, a ticket becomes a thread, a delivered message becomes a thread.
Both mail lanes are the same code; the difference is only *whose* partition the mail lands in and
whether the agent acts on the sender's behalf.

Key properties:

- **At most once, because Graph delivers at least once.** A `created` change notification is
  re-delivered after a retry, after a duplicate subscription, and after a portal restart that raced
  the 202. Two claims make that harmless, and they land *before* any thread is created:
  the Email node's **id is derived from the message id**, so the create-only
  `CreateNodeRequest` — answered from persistence — refuses a redelivery; and the artefact is flipped
  **`New → Read` inside the update lambda**, on the node's serialised write queue, so of two workers
  racing one artefact exactly one proceeds. A failed round puts the mail back to `New` and leaves it
  UNREAD in the mailbox, which is the signal a person actually sees.
- **One conversation = one thread.** Candidates come from an exact query on the stored `ThreadKey` —
  the normalized subject with any number of `Re:/Fwd:/AW:/WG:/…` layers stripped — and among them
  Graph's **`ConversationId` decides**, so two mails that merely share a subject are never merged.
- **The agent acts on the sender's behalf ONLY in the user lane** (it runs with their identity). On
  `Admin/Inbox` the sender has no account and no authority: the job there is triage. See the
  [Email Router agent](/Agent/EmailRouter).
- **The reply is emailed back** by creating an **Outbound `Email`** node (see §4) — the agent never sends
  mail directly. **Forwarding** to the team's `info@` (`Email:Inbound:ForwardAddress`) writes the same
  kind of node, with an id derived from the mail being forwarded, so asking twice forwards once.
- **Everything is a MeshNode.** Every inbound and outbound mail is persisted as an `Email` node, so the
  whole exchange is queryable, access-controlled, and visible in the UI.

### Moving parts (code)

Everything inbound rides the **`MeshWeaver.Mail.MicrosoftGraph` module** (Systemorph/MeshWeaver.Plugins
`src/`), because the Graph SDK is 43 MB a deployment that sends no mail should not carry. Only the
outbound drain and the no-op fallback are compiled into the portal.

| Concern | Type |
|---|---|
| Reactive Graph client (read message, mark read, manage subscription) | `GraphMail` *(module)* |
| Webhook endpoint (`/api/email`) — validation echo + notification batch | `EmailWebhookEndpoints` *(module)* |
| Keeps the Graph subscription alive (create on `ApplicationStarted`, renew every 24 h) | `GraphSubscriptionService` *(module)* |
| Claim, route, find-or-start the thread, notify | `EmailInboundProcessor` *(module)* |
| The deterministic ids the claims rest on | `InboundMailIdentity` *(module)* |
| Forward a shared-inbox mail to `info@` | `InboxForward` / the `MemexInbox` agent tool *(module)* |
| Outbound: drains `Email` nodes with `Direction=Outbound, Status=New` and sends them | `OutboundEmailSender` *(portal)* |

> **Startup ordering matters.** Both hosted services defer their work to
> `IHostApplicationLifetime.ApplicationStarted`: the Graph subscription can only be created once Kestrel
> is listening (Graph validates the webhook URL synchronously), and the outbound watcher can only query
> the mesh once the Orleans client + mesh hub are up. Touching the hub in `StartAsync` races startup and
> NREs — don't.

---

## 2. Ingest channels (extensible transport)

The pipeline below the transport is transport-agnostic (*inbound message → find-or-create thread → agent
→ reply*), so each channel is just an adapter onto it — not a second pipeline:

- **Email** (this doc) — Graph mailbox subscription → `EmailInboundProcessor`.
- **Teams** — a Bot Framework messaging endpoint → `TeamsInboundProcessor`, with the agent's reply read
  back via the shared `ThreadFlow.ObserveResponses` and posted into the chat. See
  **[TeamsBot.md](/Doc/AI/TeamsBot)** for setup (Azure Bot + Teams app), config, and security.

---

## 3. The notification system

A notification is "something happened that a person might care about". Memex decides — per **the
recipient's own rules** — whether it's worth telling them and through which **channel(s)**.

### NodeTypes

| Type | Owner | Path | Purpose |
|---|---|---|---|
| `Notification` | system | `{entity}/_Notification/{id}` | the in-app bell item (always-on default channel) |
| `NotificationChannel` | user | `{username}/_NotificationChannel/{id}` | a delivery channel the user has: `InApp` / `Email` / `Teams` (+ optional `target`) |
| `NotificationRule` | user | `{username}/_NotificationRule/{id}` | a **plain-English** (or lightly structured) rule: which events go to which channel |

`Notification` is registered in the `AddGraph()` chain (`AddNotificationType`) — the bell exists in
every deployment. `NotificationChannel` / `NotificationRule` ride the
**`MeshWeaver.Notifications.Channels` module** together with the triage watcher
(`Modules:Assemblies` in production; `AddNotificationChannels()` for explicit composition — see
[Modules](/Doc/Architecture/Modules)). A deployment without the module keeps the bell and the
deterministic email preferences; it has no rules/channels lane.

### Triage agent

The [Notification Triage agent](/Agent/NotificationTriage) runs on the **`chat`** model tier
(fast + cheap — the everyday round; see [Model Tiers](/Doc/AI/ModelTiers)). Given an event
and a recipient it:

1. loads the recipient's enabled `NotificationRule`s and `NotificationChannel`s,
2. applies the rules (plain English is the recipient's intent — honored; `order` resolves precedence),
3. decides the channel set (empty is common and fine — most events are not worth an email), and
4. **creates the delivery node(s)**: an in-app `Notification`, and/or an Outbound `Email` (and Teams once
   that transport exists).

Defaults when a user has no rules: **in-app only** — never escalate to email/Teams without a rule asking
for it, and never notify a user about their own action.

#### Example rules (what a user writes)

> *"Send approval requests to my Teams right away. Batch general thread completions to my work email.
> Don't notify me about anything I did myself."*

The user creates one `NotificationRule` node per rule (or several intents in one `RuleText`) under
`{username}/_NotificationRule`, plus the `NotificationChannel` nodes they reference.

---

## 4. How to send — from your own code

Three levels, cheapest first.

### a) Fire a one-off email (no node)

The simplest path — resolve `IEmailSender` (registered when `Email:Enabled=true`) or use the hub
extension:

```csharp
// IObservable<bool> — cold; you MUST subscribe.
mesh.SendEmail("alice@example.com", "Build finished", "<p>Your build is green ✅</p>")
    .Subscribe(ok => { /* sent */ }, ex => logger.LogWarning(ex, "send failed"));
```

### b) Persist an outbound email (recommended — auditable, retried)

Create an **Outbound `Email`** node; `OutboundEmailSender` drains it (claims `New → Sending`, sends, flips
to `Sent`/`Failed`). Dedup + restart-safety live in the node's status — no in-memory queue.

```csharp
workspace.GetMeshNodeStream($"{recipient}/_Email/{Guid.NewGuid()}").Update(_ =>
    new MeshNode("Email", $"{recipient}/_Email/{id}")
    {
        NodeType = EmailNodeType.NodeType,
        Content = new Email
        {
            Direction = EmailDirection.Outbound,
            To = "alice@example.com",
            Subject = "Build finished",
            Body = "<p>Your build is green ✅</p>",
            Status = EmailStatus.New,
        }
    }).Subscribe(_ => { }, ex => logger.LogWarning(ex, "queue failed"));
```

### c) Notify through the recipient's rules (let triage decide)

Raise a notification and let the triage agent route it to whatever channels the recipient configured —
this is the right call when *you* shouldn't hard-code the channel. Create the in-app `Notification` (the
bell) and/or hand the event to triage; triage creates the channel deliveries.

---

## 5. Runnable sample — "email me a test notification" button

Drop this in as a **Code** MeshNode (a layout area). Rendering it shows a button; clicking it sends an
email to the signed-in user via `IEmailSender`. This is the smallest end-to-end proof of the outbound path.

```csharp
using System.Reactive.Linq;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;                 // IEmailSender
using MeshWeaver.Mesh.Security;        // AccessService — who am I
using Microsoft.Extensions.DependencyInjection;

public static class EmailNotificationSample
{
    public static object TestEmailButton(LayoutAreaHost host, RenderingContext _)
    {
        var sp     = host.Hub.ServiceProvider;
        var access = sp.GetRequiredService<AccessService>();
        var sender = sp.GetService<IEmailSender>();     // null-safe: NoOp when Email:Enabled=false
        var me     = access.Context?.Name ?? access.CircuitContext?.Name;   // the signed-in user's email

        return Controls.Stack
            .WithView(Controls.Markdown(me is null
                ? "Sign in to email yourself a test notification."
                : $"Send a test notification to **{me}**."))
            .WithView(Controls.Button("Email me a test notification")
                .WithClickAction(ctx =>
                {
                    if (sender is null || me is null)
                    {
                        ctx.Host.UpdateData("emailResult", "Email is not configured (Email:Enabled=false).");
                        return Task.CompletedTask;
                    }
                    sender.SendEmail(me,
                            "Memex test notification",
                            "<p>👋 This is a test notification sent from Memex when you pressed the button.</p>")
                        .Subscribe(
                            ok => ctx.Host.UpdateData("emailResult", ok ? $"Sent to {me} ✅" : "Send returned false."),
                            ex => ctx.Host.UpdateData("emailResult", $"Send failed: {ex.Message}"));
                    return Task.CompletedTask;
                }))
            .WithView((h, _) => h.Stream.GetDataStream<string>("emailResult")
                .Select(msg => (object?)Controls.Markdown(msg ?? "")));
    }
}
```

Notes:
- `IEmailSender.SendEmail` returns a **cold** `IObservable<bool>` — the send only runs on `Subscribe`
  (it is subscribed in the click action above).
- It is reactive end-to-end — no `await` in the click action (see
  [AsynchronousCalls.md](/Doc/Architecture/AsynchronousCalls)).
- For an **auditable** send (visible in the mailbox history, retried on restart) prefer creating an
  Outbound `Email` node (§4b) instead of calling `IEmailSender` directly.

---

## 6. Configuration

All keys live under the `Email` section (env-var form uses `__`). Outbound needs only the first block;
inbound adds the subscription block. The client secret comes from Key Vault in prod
(`email-clientsecret → Email__ClientSecret`), never from a checked-in file.

| Key | Meaning |
|---|---|
| `Email:Enabled` | master switch — `false` registers a NoOp sender (local dev/tests never send) |
| `Email:MailboxAddress` | the mailbox to send/receive as (e.g. `memex@systemorph.com`) |
| `Email:TenantId` / `Email:ClientId` / `Email:ClientSecret` | app-only Graph credential (`Mail.Send` + `Mail.ReadWrite`) |
| `Email:UseManagedIdentity` | use a managed identity instead of a client secret (prod) |
| `Email:InboundEnabled` | turn on the inbound channel (Graph subscription + webhook) |
| `Email:WebhookBaseUrl` | public base URL Graph calls back (e.g. `https://portal.example.com`) |
| `Email:SubscriptionClientState` | shared secret echoed on each inbound notification (webhook validation) |
| — | the model the triage agent runs on is DATA, not config: label one model node `"tier": "chat"` ([Model Tiers](/Doc/AI/ModelTiers)). The deprecated `ModelTier:Light` key still works. |

Deploy parameters (`Memex.Deploy.AppHost` → `MemexOptions`) map 1:1: `email-enabled`, `email-mailbox-address`,
`email-tenant-id`, `email-client-id`, `email-inbound-enabled`, `email-webhook-base-url`,
`email-subscription-client-state`, plus the KV mapping for the secret.

> **Graph permissions:** the shared-mailbox app registration needs the **application** permissions
> `Mail.Send` and `Mail.ReadWrite` with tenant-admin consent, and a real licensed/shared mailbox it may
> act as. Missing consent → Graph 403. See [SendingEmail.md](/Doc/Architecture/SendingEmail). (The Executive Assistant is
> separate — it uses **per-user delegated** scopes on the sign-in app, not these application permissions;
> see [ExecutiveAssistant.md](/Doc/AI/ExecutiveAssistant).)

---

## 7. Executive Assistant — a mail & calendar agent

The [Executive Assistant agent](/Doc/AI/ExecutiveAssistant) gives each user a personal assistant over
**their own** mailbox and calendar (triage/write mail, "do my booking"). Unlike the shared `memex@`
ingestion mailbox — which uses an **application** Graph credential — the EA acts with **per-user,
just-in-time delegated** consent: the user grants the EA access to *their own* mailbox/calendar only when
they first use the tool, and every Graph call targets `/me/…` with that user's own delegated token. No
standing application-wide grant.

See **[ExecutiveAssistant.md](/Doc/AI/ExecutiveAssistant)** for the full design (consent flow, the
`EaCredential` encrypted-token store, tools) and the one-time Azure setup (delegated scopes + the
`/auth/ea/callback` redirect URI on the sign-in app).
