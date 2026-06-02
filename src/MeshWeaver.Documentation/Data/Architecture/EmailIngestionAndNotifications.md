# Email Ingestion, Channels & the Notification System

Memex talks to people over real-world channels. Two directions:

- **Ingestion (inbound):** a person emails the portal mailbox and a Memex agent answers — mail used as a
  chat device. Pluggable transport (email today, Teams next) feeding one shared pipeline.
- **Notifications (outbound):** Memex tells a person something happened — in-app, by email, or (next) in
  Teams — routed by **the recipient's own rules**, decided by a small **triage agent**.

This document covers both, the NodeTypes involved, configuration, and how to send a notification from your
own code (with a runnable sample). For the outbound **credential/Graph** setup specifically, see
[SendingEmail.md](SendingEmail.md); for the onboarding gate see
[InvitationOnlyOnboarding.md](InvitationOnlyOnboarding.md).

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
            sender is a known Memex user                        sender is NOT a user
                  │                                                        │
   create Email node {recipient}/_Email/{id}                 create Email node Admin/Inbox/{id}
   find-or-create conversation thread                        notify admins (in-app)
   append "process this email" (references the              (surfaced in the Admin ▸ Inbox menu)
   Email by PATH — body is not duplicated)
   the Email Router agent runs as that user,
   does the work, emits an Outbound Email reply
```

Key properties:

- **One conversation = one thread.** Each inbound email is matched to its conversation by *normalized
  subject* (strip `Re:/Fwd:/AW:/WG:/…` repeatedly → a stable `ThreadKey`) using the **vector index** over
  the sender's `_Email` namespace, confirmed by `ThreadKey` equality. Match → append to the existing
  thread; no match → start a new one. Replies with accumulating `Re:`/`Fwd:` land in the same thread.
- **The agent acts on the sender's behalf** (runs with their identity), treats the first line as the
  likely instruction, infers intent with `Search` if absent, and **cuts slop** (forwarding banners, quoted
  history, signatures). See the [Email Router agent](../../Agent/EmailRouter.md).
- **The reply is emailed back** by creating an **Outbound `Email`** node (see §4) — the agent never sends
  mail directly.
- **Everything is a MeshNode.** Every inbound and outbound mail is persisted as an `Email` node, so the
  whole exchange is queryable, access-controlled, and visible in the UI.

### Moving parts (code)

| Concern | Type |
|---|---|
| Reactive Graph client (read message, mark read, manage subscription) | `Memex.Portal.Shared.Email.GraphMail` |
| Webhook endpoint (`/api/email`) — validation echo + notification batch | `EmailWebhookController` |
| Keeps the Graph subscription alive (create on `ApplicationStarted`, renew every 24 h) | `GraphSubscriptionService` |
| Sender → user match, find-or-create thread, slop-free seed message | `EmailInboundProcessor` |
| Outbound: drains `Email` nodes with `Direction=Outbound, Status=New` and sends them | `OutboundEmailSender` |

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
  **[TeamsBot.md](../AI/TeamsBot.md)** for setup (Azure Bot + Teams app), config, and security.

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

All three are registered through static extensions in the `AddGraph()` chain
(`AddNotificationType` / `AddNotificationChannelType` / `AddNotificationRuleType`) — so they exist in every
deployment that calls `AddGraph()`, with their content types in the mesh TypeRegistry.

### Triage agent

The [Notification Triage agent](../../Agent/NotificationTriage.md) runs on the **`light`** model tier
(fast + cheap — sized for classification, configured per deployment via `ModelTier:Light`). Given an event
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
  [AsynchronousCalls.md](AsynchronousCalls.md)).
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
| `Email:WebhookBaseUrl` | public base URL Graph calls back (e.g. `https://memex.systemorph.com`) |
| `Email:SubscriptionClientState` | shared secret echoed on each inbound notification (webhook validation) |
| `ModelTier:Light` | the cheap model the triage agent runs on (a cheap-yet-capable Azure model, e.g. a `*-mini`/`*-nano` deployment) |

Deploy parameters (`Memex.Deploy.AppHost` → `MemexOptions`) map 1:1: `email-enabled`, `email-mailbox-address`,
`email-tenant-id`, `email-client-id`, `email-inbound-enabled`, `email-webhook-base-url`,
`email-subscription-client-state`, plus the KV mapping for the secret.

> **Graph permissions:** the shared-mailbox app registration needs the **application** permissions
> `Mail.Send` and `Mail.ReadWrite` with tenant-admin consent, and a real licensed/shared mailbox it may
> act as. Missing consent → Graph 403. See [SendingEmail.md](SendingEmail.md). (The Executive Assistant is
> separate — it uses **per-user delegated** scopes on the sign-in app, not these application permissions;
> see [ExecutiveAssistant.md](../AI/ExecutiveAssistant.md).)

---

## 7. Executive Assistant — a mail & calendar agent

The [Executive Assistant agent](../AI/ExecutiveAssistant.md) gives each user a personal assistant over
**their own** mailbox and calendar (triage/write mail, "do my booking"). Unlike the shared `memex@`
ingestion mailbox — which uses an **application** Graph credential — the EA acts with **per-user,
just-in-time delegated** consent: the user grants the EA access to *their own* mailbox/calendar only when
they first use the tool, and every Graph call targets `/me/…` with that user's own delegated token. No
standing application-wide grant.

See **[ExecutiveAssistant.md](../AI/ExecutiveAssistant.md)** for the full design (consent flow, the
`EaCredential` encrypted-token store, tools) and the one-time Azure setup (delegated scopes + the
`/auth/ea/callback` redirect URI on the sign-in app).
