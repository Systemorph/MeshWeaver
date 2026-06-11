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
        "<p>Hi Alice — your nightly export finished. <a href='https://memex.systemorph.com/...'>Open it</a>.</p>")
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

## Related

- [Script Execution](/Doc/Architecture/ScriptExecution) — the `Mesh`/`Log`/`Ct` globals and progress conventions.
- [Invitation-Only Onboarding](/Doc/Architecture/InvitationOnlyOnboarding) — the first consumer; full Graph/Azure setup.
- [Feature Flags](/Doc/Architecture/FeatureFlags) — deploy-time capability toggles.
- [Asynchronous Calls](/Doc/Architecture/AsynchronousCalls) — why mail stays `IObservable<T>` in hub-reachable code.
