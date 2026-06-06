---
NodeType: Markdown
Name: "Connecting Microsoft Teams"
Abstract: "The bidirectional Teams channel: a person messages the bot in Teams and a Memex agent answers in the same chat, reusing the email pipeline (inbound message → find-or-create a thread → agent → reply). Ships inert; turns on only when an admin provisions an Azure Bot and sets Teams:Enabled."
Icon: "<svg viewBox='0 0 24 24' xmlns='http://www.w3.org/2000/svg'><rect width='24' height='24' rx='4' fill='#6264a7'/><path d='M5 7a2 2 0 0 1 2-2h10a2 2 0 0 1 2 2v6a2 2 0 0 1-2 2h-7l-4 3v-3a2 2 0 0 1-1-2z' fill='white'/><path d='M9 9h6M9 12h4' stroke='#6264a7' stroke-width='1.6' stroke-linecap='round'/></svg>"
Thumbnail: "images/agenticai.svg"
Authors:
  - "Roland Buergi"
Tags:
  - "AI"
  - "Channels"
  - "Teams"
  - "Bot"
  - "Integration"
---

# Connecting Microsoft Teams

The Teams bot is a **bidirectional** channel: a person messages the bot in Teams, a Memex agent answers
**in the same chat**. It reuses the same pipeline as the [email channel](../Architecture/EmailIngestionAndNotifications.md)
— *inbound message → find-or-create a thread → agent → reply* — so a Teams chat is just another front end
onto an agent thread. It ships **inert** and turns on only when an admin provisions an Azure Bot and sets
`Teams:Enabled`.

## How it works

```
Teams user message
      │
      ▼
POST /api/teams/messages  ──(Bot Framework JWT)──►  ITeamsClient.ValidateInboundAsync   ── reject forged ──► 401
      │ (validated)
      ▼
TeamsInboundProcessor
      │  • map Teams user → Memex user by AAD object id (User.objectId)
      │  • find-or-create ONE thread per Teams conversationId (TeamsConversation link)
      │      new  → hub.StartThread  (first message seeded into PendingUserMessages)
      │      exists → hub.SubmitMessage (appended to pending)
      ▼
agent runs as that user  ──►  TeamsReplySender
                                  • ThreadFlow.ObserveResponses(threadPath)  ← shared read-side primitive
                                  • on each completed assistant reply → ITeamsClient.SendMessageAsync
                                  • send-once via TeamsConversation.LastDeliveredMessageId
                                  ▼
                           reply appears in the Teams chat
```

Key points:

- **One Teams conversation = one agent thread.** The `conversationId` is matched to its thread via a
  `TeamsConversation` link node (`{threadPath}/_TeamsConversation/…`); new → `StartThread`, existing →
  `SubmitMessage` — the canonical [thread extensions](../Architecture/ThreadOperations.md).
- **The agent runs as the mapped Memex user.** Teams users are mapped by **AAD object id** to a `User`
  node (`content.objectId`); an unmapped sender gets a polite "no account" reply.
- **The reply is read, not re-emitted.** `TeamsReplySender` uses **`ThreadFlow.ObserveResponses`** — the
  same read-side abstraction the GUI uses to render messages — to read each completed assistant
  `ThreadMessage` at `{threadPath}/{messageId}` and post it back. Nothing Teams-specific in the agent.
- **Secure by construction.** `/api/teams/messages` is anonymous at the pipeline, but every request is
  validated against the Bot Framework's OpenID metadata (issuer `api.botframework.com`, audience = the
  bot's app id) before any work happens. When disabled, the endpoint returns `NotFound` and the reply
  sender isn't even registered (the hosted service is feature-gated).

## Components

| Piece | Role |
|---|---|
| `TeamsBotController` (`/api/teams/messages`) | Validates the inbound JWT, parses the message activity, routes it |
| `ITeamsClient` / `TeamsClient` | Inbound JWT validation + outbound reply via an app-only connector token; **test seam** (fake on CI) |
| `TeamsInboundProcessor` | Map user → find-or-create thread per conversation → `StartThread`/`SubmitMessage` |
| `TeamsConversation` (NodeType) | Links a thread ↔ Teams conversation (serviceUrl, conversationId, `LastDeliveredMessageId`) |
| `TeamsReplySender` (hosted) | `ObserveResponses` → `SendMessageAsync`; send-once; registered only when enabled |

## Configuration

| Key | Meaning |
|---|---|
| `Teams:Enabled` | master switch — false = inert (endpoint `NotFound`, no reply sender) |
| `Teams:AppId` | the Azure Bot / app registration id (`MicrosoftAppId`) |
| `Teams:AppPassword` | the bot app client secret (`MicrosoftAppPassword`) — keep in Key Vault |
| `Teams:TenantId` | Entra tenant id for a single-tenant bot (optional; multi-tenant when empty) |

Configured through the AppHost fluent API, exactly like email:

```csharp
builder.AddMemex("memex", o => o
    // …
    .WithTeams(
        enabled: true,
        appId: "<bot app id>",
        appPassword: "<from Key Vault>",
        tenantId: "<tenant>"));
```

Deploy parameters (`Memex.Deploy.AppHost`): `teams-enabled`, `teams-app-id`, `teams-app-password`,
`teams-tenant-id` → emitted as `Teams__*`. On AKS the secret comes from Key Vault
(`teams-apppassword → Teams__AppPassword`), like the email client secret.

## Azure setup (one-time, by an admin)

1. **Create an Azure Bot resource** (Azure Portal → *Azure Bot*). Use a **multi-tenant** or
   **single-tenant** Microsoft App; note the **App ID** and create a **client secret**.
2. **Messaging endpoint:** set it to **`{BaseUrl}/api/teams/messages`**
   (e.g. `https://memex.systemorph.com/api/teams/messages`).
3. **Enable the Teams channel** on the Bot resource.
4. **Teams app manifest:** create a Teams app whose `bots[0].botId` is the **App ID**, with the
   `personal` (and optionally `team`/`groupchat`) scopes, and sideload/publish it to your tenant so users
   can chat with it.
5. **Configure Memex:** set `Teams:Enabled=true`, `Teams:AppId`, `Teams:TenantId`, and supply
   `Teams:AppPassword` via Key Vault. Redeploy; the endpoint goes live and validates inbound activities.
6. **User mapping:** ensure Memex `User` nodes carry the user's **AAD object id** (`content.objectId`) so
   the bot can run as the right person. Unmapped users get a "no account" reply.

> Until step 5, the bot is completely inert — the code ships with `Teams:Enabled=false`, the endpoint
> returns `NotFound`, and no reply sender runs.

## Notifications over Teams

The [notification system](../Architecture/EmailIngestionAndNotifications.md#3-the-notification-system)
already models a `Teams` channel kind; once the bot is connected, a recipient's
[notification rules](../GUI/NotificationPreferences.md) can escalate notifications into Teams the same way
they do email.
