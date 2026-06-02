# The Executive Assistant Agent

The **Executive Assistant (EA)** is a personal agent that works your **own** mailbox and calendar on your
behalf — triage and write mail, read your inbox, and "do your booking" (schedule / reschedule / cancel
meetings). It also helps you manage your [notification preferences](../GUI/NotificationPreferences.md).

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
[notification rules](../GUI/NotificationPreferences.md)).

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
