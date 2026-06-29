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
   acting.
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
| Mail | `ListInbox`, `SearchMail`, `ReadMail`, `SendMail`, `ReplyToMail` |
| Calendar | `ListEvents`, `CreateEvent` (book + invite attendees), `CancelEvent` |

Example asks: *"Book 30 min with Alice next Tuesday afternoon and invite her"*, *"reply to the vendor that
we accept"*, *"clear my Friday"*, *"email me when an approval needs me"* (the last manages your
[notification rules](/Doc/GUI/NotificationPreferences)).

## Architecture

- **`IEaGraphAuth` / `EaGraphAuth`** — builds the consent URL, exchanges the auth code, stores/refreshes
  the encrypted per-user refresh token, and mints delegated access tokens. `IEaGraphAuth` is a test seam:
  tests substitute a fake so the consent step is mocked away (CI has no real auth).
- **`EaConsentController`** — `/auth/ea/connect` (incremental-consent redirect) and `/auth/ea/callback`
  (code exchange + store). The acting user comes from the authenticated principal.
- **`ExecutiveAssistantPlugin`** — per call, fetches the user's delegated token and calls Graph `/me/…`;
  if the user hasn't connected, returns the connect link instead of acting.
- **`EaCredential`** — the encrypted refresh token, one per user under `Auth/_EaCredential/{objectId}`.

These live in `Memex.Portal.Shared` (they reuse the portal's Microsoft sign-in app + the master-key
`IProviderKeyProtector`); the agent definition is `Agent/ExecutiveAssistant`.

## Azure setup (one-time, by an admin)

The EA reuses the portal's **sign-in** app registration (the `Authentication:Microsoft` client). On it:

1. Add the **delegated** Microsoft Graph permissions: `Mail.ReadWrite`, `Mail.Send`,
   `Calendars.ReadWrite`, `offline_access`.
2. Add the redirect URI **`{BaseUrl}/auth/ea/callback`** (e.g. `https://memex.systemorph.com/auth/ea/callback`).
3. No admin pre-consent is required — each user consents for themselves on first use (that's the point).

No application-wide Graph permission is needed for the EA (the standing `Calendars.ReadWrite` *application*
grant used by an earlier iteration can be removed; the shared `memex@` ingestion mailbox keeps its
`Mail.ReadWrite` / `Mail.Send` application permissions — those are a separate concern).

## Privacy & revocation

- The portal stores only your **encrypted refresh token**, never your password and never the raw token.
- The token is scoped to exactly the delegated permissions you approved.
- Revoke at any time from your Microsoft account (My Apps → the portal app → Revoke), or by deleting your
  `EaCredential` node; the EA then falls back to asking you to reconnect.
