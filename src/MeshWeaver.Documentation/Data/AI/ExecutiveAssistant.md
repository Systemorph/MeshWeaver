---
NodeType: Markdown
Name: "The Executive Assistant Agent"
Abstract: "A personal agent that works your own mailbox and calendar — triage and write mail, read your inbox, do your booking — under per-user, just-in-time delegated consent: no app-wide Graph access, an encrypted per-user refresh token, and short-lived delegated tokens that call Graph as you."
Icon: "<svg viewBox='0 0 24 24' xmlns='http://www.w3.org/2000/svg'><rect width='24' height='24' rx='4' fill='#6a1b9a'/><rect x='4' y='9' width='16' height='10' rx='2' fill='white'/><path d='M9 9V7a2 2 0 0 1 2-2h2a2 2 0 0 1 2 2v2' fill='none' stroke='white' stroke-width='2' stroke-linecap='round'/><rect x='10.5' y='12' width='3' height='2' rx='0.5' fill='#6a1b9a'/></svg>"
Thumbnail: "images/agenticai.svg"
Authors:
  - "Roland Buergi"
Tags:
  - "AI"
  - "Agents"
  - "Email"
  - "Calendar"
  - "Access Control"
---

# The Executive Assistant Agent

The **Executive Assistant (EA)** is a personal agent that works your **own** mailbox and calendar on your
behalf — triage and write mail, read your inbox, and "do your booking" (schedule / reschedule / cancel
meetings). It also helps you manage your [notification preferences](/Doc/GUI/NotificationPreferences).

## Least-privilege by design: per-user, just-in-time consent

The EA never uses standing, application-wide Graph access. Instead it asks for access to **your** mailbox
and calendar **only when it first needs them**, and only **you** can grant it:

1. You ask the EA to do something with your mail/calendar (e.g. *"what's on my calendar tomorrow?"*).
2. If you haven't connected yet, the tool replies with a **connect link** (`/auth/ea/connect`) instead of
   acting. 🚨 **For an agent that answer is the instruction, not an obstacle:** hand the user
   `{BaseUrl}/auth/ea/connect` and wait for them to consent. Never route around it by minting a
   token of your own — a credential the user did not grant for this is not a substitute for the one
   they did.
3. You click it → Microsoft shows a consent screen for the EA's **delegated** scopes → you approve.
4. The portal stores your **refresh token encrypted** (AES-GCM via the deployment master key) as an
   `EaCredential` node and from then on mints short-lived **delegated** access tokens to call Graph as
   **you** (`/me/…`). You can revoke anytime in your Microsoft account.

Because access is delegated and per-user, the EA can only ever touch **your** data — there is no app-wide
grant that could reach anyone else's mailbox.

## What it can do

The EA agent declares the `Mesh` + `ExecutiveAssistant` plugins. The `ExecutiveAssistant` tool surface:

| Area | Tools |
|---|---|
| Mail | `ListInbox`, `SearchMail`, `ReadMail`, `DraftMail`, `DraftReply` — and `SendMail`, `ReplyToMail` **only where the deployment opted in**, see below |
| Calendar | `ListEvents`, `GetEvent`, `CreateEvent` (book + invite attendees), `UpdateEvent`, `CancelEvent` |

Example asks: *"Book 30 min with Alice next Tuesday afternoon and invite her"*, *"reply to the vendor that
we accept"*, *"clear my Friday"*, *"email me when an approval needs me"* (the last manages your
[notification rules](/Doc/GUI/NotificationPreferences)).

### 🔒 The agent DRAFTS and the human sends — `Email:AgentSend`

`MailAgentOptions.SendMode` is read from the configuration key **`Email:AgentSend`** and defaults to
**`DraftOnly`**. In that mode the agent composes into your own **Drafts** folder with `DraftMail` /
`DraftReply`, and **you** press Send in your mail client.

The safety here is structural, not a policy the model is asked to respect: in `DraftOnly` the send
tools are **never handed to the model at all**, so no prompt, no injected instruction and no model
misjudgement can reach a live send. A deployment that wants agents to send directly sets
`Email:AgentSend=Send`; then `SendMail` / `ReplyToMail` appear in the tool list. If they are called
in `DraftOnly` they refuse by name and tell the caller to use the draft tool instead.

**What this means for an agent asked to "send an email":** on a default deployment you cannot, and
saying you will is wrong. Prepare the draft, then tell the person it is in their Drafts and what it
says. The human-in-the-loop step is real and needs no extra UI.

### No mail tool attaches a file — and none amends a draft

Two limits of the mail surface, both worth knowing before you promise an outcome:

- **Attachments.** No `ExecutiveAssistant` mail tool attaches anything; the tools carry a text body
  only. A message that must carry a file goes through the document path instead — **Share ⇒ as
  email** in the node menu, i.e. `SendDocumentDispatch.ExportAndSend` with
  `DocumentDelivery.Attachment` — see [Sending Email](/Doc/Architecture/SendingEmail) and the
  `/share-email` skill (`get Skill/share-email`, served from MeshWeaver.Plugins). It sends **as the
  user**, off the same
  `EaCredential`, so it needs no second consent.
- **Amending.** There is no `GetDraft`/`UpdateDraft` and no delete. Correcting a saved draft is
  therefore not possible — writing a second draft beside the first is the only move, and it leaves
  the person choosing between two near-identical messages. Get the wording right the first time, or
  hand the correction to the person along with the draft. (The calendar surface fixed the same
  asymmetry after the 2026-08-16 data loss; see the next section.)

### Editing an event is READ then PATCH, never cancel-and-recreate

`GetEvent` and `UpdateEvent` exist because their absence caused real data loss. With only
`ListEvents` / `CreateEvent` / `CancelEvent`, "add one line to that meeting's agenda" had exactly one
possible shape — cancel the event and create a new one — and `ListEvents` returned no body, so the
agent could not read what it was replacing. On 2026-08-16 that wiped an eight-item checklist the user
then had to re-dictate by hand; the original was unrecoverable (no mail, no mesh node, the cancelled
event gone).

So the amend flow is **`GetEvent` → edit the returned body → `UpdateEvent`**: a Graph `PATCH` that
carries only the fields you pass, leaving every omitted one at its stored value. `CancelEvent` means
*cancel the meeting*, not *change it*. `ListEvents` now also returns a `preview` for quick triage, but
a body you intend to REPLACE must be read in full with `GetEvent` first — `UpdateEvent` replaces the
whole body, so whatever you do not send back is gone.

## Architecture

- **`IEaGraphAuth` / `EaGraphAuth`** — builds the consent URL, exchanges the auth code, stores/refreshes
  the encrypted per-user refresh token, and mints delegated access tokens. `IEaGraphAuth` is a test seam:
  tests substitute a fake so the consent step is mocked away (CI has no real auth).
- **`EaConsentController`** — `/auth/ea/connect` (incremental-consent redirect) and `/auth/ea/callback`
  (code exchange + store). The acting user comes from the authenticated principal.
- **`ExecutiveAssistantPlugin`** — per call, fetches the user's delegated token and calls Graph `/me/…`;
  if the user hasn't connected, returns the connect link instead of acting.
- **`EaCredential`** — the encrypted refresh token, one per user under `Auth/_EaCredential/{objectId}`.

The consent/credential half lives in the portal (it reuses the portal's Microsoft sign-in app + the
master-key `IProviderKeyProtector`); `ExecutiveAssistantPlugin` itself ships in the
**`MeshWeaver.Mail.MicrosoftGraph` module** (`Systemorph/MeshWeaver.Plugins`), which is why a
deployment that sends no mail pays neither the 43 MB Graph/Kiota closure nor its Roslyn reference
cost. The agent definition is `Agent/ExecutiveAssistant`.

## Azure setup (one-time, by an admin)

The EA reuses the portal's **sign-in** app registration (the `Authentication:Microsoft` client). On it:

1. Add the **delegated** Microsoft Graph permissions: `Mail.ReadWrite`, `Mail.Send`,
   `Calendars.ReadWrite`, `offline_access`.
2. Add the redirect URI **`{BaseUrl}/auth/ea/callback`** (e.g. `https://portal.example.com/auth/ea/callback`).
3. No admin pre-consent is required — each user consents for themselves on first use (that's the point).

No application-wide Graph permission is needed for the EA (the standing `Calendars.ReadWrite` *application*
grant used by an earlier iteration can be removed; the shared `memex@` ingestion mailbox keeps its
`Mail.ReadWrite` / `Mail.Send` application permissions — those are a separate concern).

## Privacy & revocation

- The portal stores only your **encrypted refresh token**, never your password and never the raw token.
- The token is scoped to exactly the delegated permissions you approved.
- Revoke at any time from your Microsoft account (My Apps → the portal app → Revoke), or by deleting your
  `EaCredential` node; the EA then falls back to asking you to reconnect.
