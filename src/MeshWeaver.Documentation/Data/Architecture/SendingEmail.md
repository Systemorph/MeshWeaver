---
Name: Sending Email
Description: "Send outbound mail from the mesh — the IEmailSender abstraction, the Mesh.SendEmail(...) script extension for triggering notifications from scripts, configuration, and the Microsoft Graph (M365) reference sender."
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="2" y="4" width="20" height="16" rx="2"/><path d="m22 7-10 5L2 7"/></svg>
Category: Architecture
---

# Sending Email

The mesh can send outbound mail through a single framework abstraction,
`IEmailSender`. The concrete sender is
registered by the host — the portal ships a Microsoft Graph implementation (`GraphEmailSender`) and a
`NoOpEmailSender` for when email is disabled — so callers never reference a mail SDK or a specific
mailbox provider.

Mail is **reactive end-to-end**: `SendEmail` returns a cold `IObservable<bool>` — the send runs on
`Subscribe` and emits `true` on success (or surfaces the failure via `OnError`).

---

## Triggering mail from a script

Every mesh script (Code node, interactive markdown cell, or MCP `execute_script`) gets the `Mesh`
global (an `IMessageHub`). The framework extension
`Mesh.SendEmail(...)` resolves the
registered sender and sends — no DI lookup, no SDK types:

```csharp
Mesh.SendEmail(
        "alice@example.com",
        "Your export is ready",
        "<p>Hi Alice — your nightly export finished. <a href='https://portal.example.com/...'>Open it</a>.</p>")
    .Subscribe(
        ok => Log.LogInformation("Email sent: {Ok}", ok),
        ex => Log.LogError(ex, "Email send failed"));
```

`SendEmail` is in the `MeshWeaver.Mesh` namespace, which the kernel imports by default — so the call
works unqualified in any script. See [Script Execution](/Doc/Architecture/ScriptExecution) for the `Mesh`/`Log`/`Ct`
globals and progress conventions.

> **Graceful degradation.** On a deployment with no `IEmailSender` registered (or `Email:Enabled=false`),
> `Mesh.SendEmail` returns an observable that yields `false` instead of throwing — a script written
> against it runs everywhere, and only actually sends where email is configured.

### Using it for notifications

This is the building block for "notify by email" flows — pair it with the in-app
[Notification](/Doc/Architecture/SatelliteEntityPatterns) node, or call it from an
[operation-as-script](/Doc/Architecture/ActivityControlPlane) when a long job finishes:

```csharp
Log.LogInformation("Rollup complete — notifying owner");
Mesh.SendEmail(ownerEmail, "Daily rollup finished",
        $"<p>Wrote {rowCount} rows at {DateTimeOffset.UtcNow:u}.</p>")
    .Subscribe(_ => Log.LogInformation("notified {Owner}", ownerEmail),
               ex => Log.LogWarning(ex, "notify failed"));
```

---

## Calling it from app code

Anywhere with an `IMessageHub` (handlers, services, Blazor click actions) the same extension applies;
or inject `IEmailSender` directly. Both return `IObservable<bool>` — **subscribe to drive** (the send
is the side effect on Subscribe):

```csharp
// Extension on the hub:
hub.SendEmail(to, subject, html).Subscribe(_ => { }, ex => logger.LogWarning(ex, "mail failed"));

// Or inject the sender:
public sealed class Inviter(IEmailSender email) { /* email.SendEmail(...).Subscribe(...) */ }
```

Do **not** `await`/`.ToTask()` it inside hub-reachable code — keep the chain reactive
(see [Asynchronous Calls](/Doc/Architecture/AsynchronousCalls)). Tests may bridge with `.FirstAsync().ToTask()`.

---

## Configuration

Bound from the `Email` section into
`EmailOptions`. **Disabled by default** —
when off, the host registers `NoOpEmailSender`, which logs the would-be send and reports success, so
local dev and tests never send mail.

| Key | Type | Default | Notes |
|---|---|---|---|
| `Email:Enabled` | bool | `false` | When `false`, the NoOp sender is registered. |
| `Email:MailboxAddress` | string | `""` | The mailbox the portal sends **and** receives **as** — a real/shared mailbox (e.g. `memex@yourtenant.com`). |
| `Email:TenantId` | string | `""` | Entra tenant id (client-secret flow). |
| `Email:ClientId` | string | `""` | App-registration client id (client-secret flow). |
| `Email:ClientSecret` | string | `""` | App-registration client secret (keep in Key Vault). |
| `Email:UseManagedIdentity` | bool | `false` | When `true`, authenticate via `DefaultAzureCredential` (managed identity) instead of a client secret. |
| `Email:InboundEnabled` | bool | `false` | When `true`, the portal subscribes to the mailbox inbox (Graph change notifications → agent threads). |
| `Email:WebhookBaseUrl` | string | `""` | Public base URL Graph calls back for inbound notifications (e.g. `https://memex.yourtenant.com`); the webhook lands at `{WebhookBaseUrl}/api/email`. |
| `Email:SubscriptionClientState` | string | `""` | Per-deployment random value Graph echoes on each inbound notification; the webhook rejects mismatches. |

> **Graph permissions.** Outbound (`/sendMail`) needs the **`Mail.Send`** application permission;
> inbound (inbox subscription + read) needs **`Mail.ReadWrite`**. Both are tenant-admin-consented
> application permissions on the mailbox's app registration.

Registration (in the portal's `MemexConfiguration.ConfigureMemexServices`):

```csharp
var email = builder.Configuration.GetSection(EmailOptions.SectionName).Get<EmailOptions>() ?? new();
services.AddSingleton(email);
services.AddSingleton<IEmailSender>(email.Enabled
    ? sp => new GraphEmailSender(...)      // Microsoft Graph /sendMail
    : sp => new NoOpEmailSender(...));
```

---

## The Microsoft Graph reference sender

`GraphEmailSender` calls Graph
`/users/{mailbox}/sendMail` using the `Mail.Send` **application** permission, bridging the async Graph
call to the reactive surface via `Observable.FromAsync`. Credentials come from `EmailOptions`:
`DefaultAzureCredential` (managed identity) in production, or a `ClientSecretCredential` for self-host.

The one-time Azure setup — a dedicated app registration, **admin-consented `Mail.Send`** (plus
**`Mail.ReadWrite`** when inbound is enabled), a real shared mailbox the portal sends and receives
as, and (recommended) an Exchange **Application Access Policy** scoping the app to only that mailbox —
is covered in
[Invitation-Only Onboarding → Sending email](/Doc/Architecture/InvitationOnlyOnboarding#sending-email-microsoft-graph).

### Swapping the implementation

`IEmailSender` is a plain framework interface — a different host can register its own sender (SMTP,
SendGrid, Azure Communication Services) without touching any caller. Register your implementation as
the `IEmailSender` singleton and every `Mesh.SendEmail(...)` call routes through it.

---

## Who the mail comes FROM

Two identities exist, and the recipient can tell them apart — so the choice is never implicit.

| Identity | Credential | Use for |
|---|---|---|
| **The signed-in user** — `EmailDelivery.AsUser(objectId)` | The user's **delegated** `EaCredential` (Graph `/me/sendMail`) | A personal act: sharing a document. Recipient sees the person, replies come back to them, and it lands in their own Sent Items. |
| **The shared mailbox** — `EmailDelivery.AsSharedMailbox` | Application credential, `EmailOptions.MailboxAddress` | System mail: notifications, invitations, automation. |

The delegated scope needed is `Mail.Send`, and it is **already part of `EaGraphAuth.Scopes`** —
connecting the personal assistant grants it, so there is no separate consent step for sending.

- **Probe before composing**: `hub.CanSendAsUser(objectId)`, so the UI can STATE the identity rather
  than the user discovering it in the recipient's inbox.
- **Never fall back silently.** If the user is not connected, offer `/auth/ea/connect`; the shared
  mailbox is only ever an explicitly chosen second option — and then set
  `EmailDelivery.AsSharedMailboxReplyingTo(userEmail)` so a reply still reaches the human.

🚨 **Mailbox data is queried LIVE from Graph and never replicated into the mesh.** Graph is the
system of record and always current; a mirror would buy nothing and would leave personal
correspondence at rest in the mesh. Recipients come from `/me/people`, a reply target from a live
message query, and a reply from `POST /me/messages/{id}/createReply` (Graph supplies the
`In-Reply-To`/`References` threading — never hand-roll those headers). The mesh may hold at most an
`InternetMessageId`/`ConversationId` reference. The inbound mail→agent channel is a separate,
deliberate path and is unaffected.

---

## Sending a document AS the email

The node menu's **Share ⇒ as email** entry (`SendDocumentLayoutArea`, alongside Export to PDF and
Export to DOCX) can put a rendered document in the message **body** instead of attaching a file.
`DocumentDelivery.EmailBody` runs the standard node ⇒ file pipeline with
`ExportFormat.Html` — `Templates/Export/Html` — and uses the result as `htmlBody`; the sender's
covering note is prepended into the document so it is not lost.
`DocumentDelivery.Attachment` keeps the original PDF-attachment behaviour.

### Why an email needs its own renderer

Email is not a browser. Outlook on Windows renders through the **Word** engine, so
`EmailDocumentComposer` produces markup for that lowest common denominator:

| Constraint | What the renderer does |
|---|---|
| No stylesheets survive (Gmail/Outlook.com strip them) | All CSS is inline; no `<style>`, no `<link>` |
| Word has no flexbox or grid | Multi-column layout is a `<table>` |
| **Word ignores `<colgroup>`** | `EmailTableSizer` writes the width on **every** `<td>`/`<th>`, as both the attribute and the style |
| Equal columns read badly | Widths are proportional to each column's content volume, **square-root damped** with a floor and a cap, so a prose column cannot starve the short ones |
| Word shows inline SVG as a broken box | `EmailHtmlSanitizer` strips `<svg>` entirely — an omitted icon beats a broken one |
| A mail client has no page origin | Every `href`/`src` is made absolute against the portal base URL |
| `data:` URIs are stripped; remote images are blocked until "Download pictures" | `EmailImageInliner` converts embedded pictures into **`cid:` inline parts** (`EmailAttachment.ContentId`), which travel inside the message and render immediately |

🚨 Do **not** test "is this URL already absolute" with `Uri.TryCreate(value, UriKind.Absolute, …)`.
On Unix that parses a root-relative path (`/Some/Page`) as an implicit `file:` URI and reports it
absolute, so the link ships relative and is dead in an inbox — while the same call returns `false`
on Windows, hiding the bug. `EmailHtmlSanitizer.HasScheme` inspects the scheme instead.

### Embedded layout areas are resolved

This is the capability PDF and DOCX do not have. A markdown document can embed a live view with
`@@(…)`; the markdown pipeline only emits an empty `<div class='layout-area'>` anchor that a
**browser** fills. The PDF/DOCX templates parse markdown with a pipeline that has never heard of the
embed syntax, so an embed is printed as its **literal source text**; the pixel path emits the anchor
and prints a blank.

`EmailAreaResolver` closes that hole server-side: for each anchor it opens the area's synchronization
stream (the same one the browser subscribes to, under the **caller's** `AccessContext`), snapshots
the settled control tree, and `EmailControlRenderer` serializes it to table-based markup.

A live area has no completion signal — `OgCard` emits placeholder cards and fills each in as its node
stream or Open Graph fetch lands — so the snapshot is taken when the tree has been **quiescent** for
`EmailHtmlOptions.SettleWindow`, or at the `Timeout` deadline, whichever comes first. Taking the
first emission would export the placeholders.

---

## Related

- [Script Execution](/Doc/Architecture/ScriptExecution) — the `Mesh`/`Log`/`Ct` globals and progress conventions.
- [Invitation-Only Onboarding](/Doc/Architecture/InvitationOnlyOnboarding) — the first consumer; full Graph/Azure setup.
- [Feature Flags](/Doc/Architecture/FeatureFlags) — deploy-time capability toggles.
- [Asynchronous Calls](/Doc/Architecture/AsynchronousCalls) — why mail stays `IObservable<T>` in hub-reachable code.
