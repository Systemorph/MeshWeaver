---
name: teams
description: Microsoft Teams access from MeshWeaver — what the shipped Teams integration actually is (a bot people message; it reads and sends NOTHING on a user's behalf), and what reading or sending a user's teams, channels and chats needs — the delegated Graph scopes on the Executive Assistant consent link, the tools on the EA plugin, the consent rules, and the TENANT wall that makes "can you see PartnerRe ESL?" a different question from "can you see my teams?". Use when someone asks whether an agent can see, read, summarise or post to Teams, when adding Teams scopes or tools, or when a Teams-related answer from an agent has to be checked against what is wired.
user-invocable: true
allowed-tools:
  - Bash
  - Read
  - Grep
---

# /teams — reading and sending Teams on a user's behalf

**Measured 2026-09-09** by asking the Executive Assistant on memex "can you see my Teams channels,
in particular PartnerRe ESL?": it answered no, and it was right. Nothing in the fleet can enumerate
a user's teams or read a channel. This skill records what exists, what the ask needs, and the one
wall that no scope can climb.

## What exists: a bot, not a reader

`MeshWeaver.Teams` (MeshWeaver.Plugins, `src/MeshWeaver.Teams/`) is the Bot Framework channel
documented in `Doc/AI/TeamsBot`: a person messages the bot in Teams, `TeamsInboundProcessor`
finds-or-creates one agent thread per conversation, `TeamsReplySender` posts the agent's reply
back. Its outbound credential is an **app-only connector token** for `api.botframework.com` — it
can answer in a conversation the bot is part of and nothing else. On memex it is inert
(`Teams:Enabled=false`, zero `TeamsConversation` nodes).

The Executive Assistant (`src/MeshWeaver.Mail.MicrosoftGraph/ExecutiveAssistantPlugin.cs`) has
mail and calendar tools only. Its credential is the delegated one minted by the EA consent link,
and that credential is the seam everything below hangs on.

## The seam: the EA consent link IS a delegated Graph token

`{BaseUrl}/auth/ea/connect` sends the user through Microsoft's authorize endpoint with
`prompt=consent` and the scope string in `EaGraphAuth.Scopes` — **Memex repo**,
`Memex.Portal.Shared/Authentication/EaGraphAuth.cs`:

```
Mail.ReadWrite Mail.Send Calendars.ReadWrite offline_access
```

Add Teams scopes to that string and every user reconnects once; from then on the same
`GraphServiceClient` the plugin already builds reaches `/me/joinedTeams`, `/teams/{id}/channels`,
`/teams/{id}/channels/{id}/messages`, `/me/chats`, `/chats/{id}/messages` — as the user. Nothing
app-only, no new secret, no new token store. That is the whole point of routing it through the EA.

### Read

| Scope (delegated) | Lets the EA | Admin consent |
|---|---|---|
| `Team.ReadBasic.All` | list the user's teams | user-consentable |
| `Channel.ReadBasic.All` | list a team's channels | user-consentable |
| `ChannelMessage.Read.All` | read channel messages | **admin consent required** |
| `Chat.Read` | list and read the user's chats | user-consentable |

### Send

| Scope (delegated) | Lets the EA | Admin consent |
|---|---|---|
| `ChannelMessage.Send` | post to a channel as the user | user-consentable |
| `ChatMessage.Send` | post in a chat as the user | user-consentable |

Re-verify the consent column against the
[Graph permissions reference](https://learn.microsoft.com/en-us/graph/permissions-reference)
before adding a scope — a tenant's consent policy can make any of them admin-only. The Entra
**sign-in app registration** must list the same delegated permissions, or the authorize call fails
before the user sees a screen.

🚨 **Teams has no draft state.** Mail is DraftOnly by design (`Email:AgentSend`, the human presses
Send); a Teams send is immediate and irreversible. A send tool needs the same gate — a
`Teams:AgentSend` mode defaulting to a preview page the human confirms, the shape `PrepareMailing`
already has — and the sending tools must never be handed to the model in the default mode, exactly
as `SendMail`/`ReplyToMail` are not.

### The three pieces to write

1. **Memex repo:** the scopes in `EaGraphAuth.Scopes`, and the delegated permissions on the app
   registration (Roland's Entra tenant for the Systemorph portals).
2. **Plugins repo:** tools on `ExecutiveAssistantPlugin` — `ListTeams`, `ListChannels`,
   `ReadChannelMessages`, `ListChats`, `ReadChat`, and (gated) `SendChannelMessage` /
   `SendChatMessage`; the plugin's `ClientAsync()` already yields the delegated
   `GraphServiceClient`. Model-facing `[Description]`s stay English.
3. **Plugins repo:** `src/MeshWeaver.AI/Data/Agent/ExecutiveAssistant.md` names the new surface,
   or the agent will keep answering "I have no Teams tool" from its instructions.

## 🚨 The wall: "PartnerRe ESL" lives in PartnerRe's tenant

A delegated token is minted by the user's **home** tenant. A team the user reaches as a **guest**
(B2B) lives in the **resource** tenant, and Graph does not cross that line on a home-tenant token:
`/me/joinedTeams` does not list guest teams, and the channel endpoints answer 403/404 for them —
measured and documented by others
([Q&A](https://learn.microsoft.com/en-us/answers/questions/48733/read-teams-channels-using-graph-api-as-a-b2b-(gues),
[Tech Community](https://techcommunity.microsoft.com/t5/teams-developer/read-teams-channels-using-graph-api-as-a-b2b-or-guest-user/m-p/1502502)).
So the scopes above make the EA see **Systemorph's** teams. PartnerRe ESL needs one of:

- **A token for PartnerRe's tenant.** Run the same consent flow against
  `login.microsoftonline.com/<partnerre tenant id>` (the app registration must be multi-tenant),
  store the resulting refresh token per (user, tenant), and route calls for that team through it.
  `ChannelMessage.Read.All` then needs **PartnerRe's** admin to consent to the Memex app in their
  tenant — a conversation with Thomas Mager and their security team (Lucas Mebold), not a setting
  on our side.
- **The bot, installed in their team.** The existing `MeshWeaver.Teams` bot with resource-specific
  consent (`ChannelMessage.Read.Group`) in its manifest, added to the PartnerRe ESL team by a team
  owner. Messages are then PUSHED to `/api/teams/messages` as they happen; nothing is enumerated.
  Needs `Teams:Enabled` on the receiving portal and PartnerRe's app-upload policy to allow it.
- **Their export.** Ask for a channel export; ingest it as content. No integration at all.

Do not promise "I'll read the PartnerRe channel" until one of these exists; the honest answer today
is the Graph-visible substitute — the mailbox — which the EA already gives.

## Checking what an agent says about Teams

The EA's answer is only as good as its tool list. Before trusting a "no Teams access" or a
"connected" claim: `get @Agent/ExecutiveAssistant/data/` for the plugins, then read
`ExecutiveAssistantPlugin.cs` for the tools actually exposed, then `EaGraphAuth.Scopes` for what
the token can carry. All three moved independently in the past; the doc `Doc/AI/TeamsBot` describes
the bot only.
