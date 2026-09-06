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
(see [Asynchronous Calls](/Doc/Architecture/AsynchronousCalls)). 🚨 **Tests are not an exception**
(2026-08-30: *"no ToTask ever"*): assert on the observable — `await hub.SendEmail(to, subject, html).Should().Emit();`
— and let the assertion own the wait. The old sentence read `.FirstAsync().ToTask()`.

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

Registration is split between the host and the module, and the pairing is deliberately
**order-independent**: the host registers its no-op with `TryAddSingleton` and the
`MeshWeaver.Mail.MicrosoftGraph` module registers the Graph sender with a plain `AddSingleton`.
Whichever runs first, the last registration of a service type wins and `TryAdd` declines when one
already exists — so module listed ⇒ the Graph sender; module absent ⇒ the no-op keeps the two
`GetRequiredService<IEmailSender>()` call sites resolvable instead of throwing at startup.

```csharp
var email = builder.Configuration.GetSection(EmailOptions.SectionName).Get<EmailOptions>() ?? new();
services.AddSingleton(email);
services.TryAddSingleton<IEmailSender, NoOpEmailSender>();   // host fallback
// …and, when the module is listed under Modules:Assemblies:
services.AddSingleton<IEmailSender, GraphEmailSender>();     // MeshWeaver.Mail.MicrosoftGraph
```

### 🚨 Refused configurations

`Email:Enabled=true` on an install that cannot actually deliver is **refused**, never quietly
succeeded. There are two such configurations and they are diagnosed separately, because they need
opposite fixes:

| Configuration | Verdict reached from | What the operator is told |
|---|---|---|
| Enabled, but the credential keys the selected flow needs are unset (`EmailOptions.MissingCredentialKeys()`) | **Configuration** — inert data | The exact keys to set, e.g. `Email:TenantId` |
| Enabled, complete, but the resolved sender reports `DeliversMail == false` (the module is not on this install) | The **container** | Land the `MeshWeaver.Mail.MicrosoftGraph` module |

On either, `OutboundEmailSender` and `InvitationEmailSender` **do not start at all**, so queued mail
stays visibly `New` and every other caller gets a loud failure instead of `true`.

🚨 **Recovery needs a restart, and the refusal says so.** The `Email` section is bound **once at host
start** (`Configuration.GetSection(...).Get<EmailOptions>()` into a singleton — not `IOptionsMonitor`),
and both watchers are `IHostedService`s that start with it, so neither re-reads configuration at
runtime. Completing the section therefore takes effect on the next portal start / rollout — at which
point the queued `New` mail goes out by itself, with no data repair and nothing to re-queue by hand.
That is the whole reason refusing beats succeeding quietly: mail stamped `Sent` that was never sent
is indistinguishable from mail that really was sent, and no restart recovers it (#2023).

### 🚨 An INCOMPLETE section refuses at STARTUP, by key (#2636, #2637)

The watcher refusal above is correct and stays. It is also, on its own, **invisible**: its whole
output is one `Error` line per host start. On memex the `Email` section sat half-set — enabled, with
`Email:TenantId` and `Email:ClientId` unset — and the portal came up perfectly healthy with mail
dark. `/health` was 200, the site served, and every invitation, notification and document share
queued as `New` and stayed there until a human noticed mail had not arrived.

So `MemexConfiguration.ConfigureMemexMesh` now runs `EmailConfigurationGuard.Validate(configuration)`
at boot, beside `ValidateContentStorageDurability` and `MicrosoftTenant.Validate`. **Two rules, and
they are deliberately different:**

| The `Email` section is… | Verdict | Why |
|---|---|---|
| **absent, or `Email:Enabled=false`** | **starts** — never refused, however blank | Blank is what "no mail on this install" looks like: every local dev run, every test host, every deployment that never wanted mail. Aborting a portal for an unconfigured OPTIONAL integration is #2510 verbatim. |
| **`Email:Enabled=true` and complete** (either flow) | **starts** | Nothing to refuse. Managed identity needs no credential keys at all. |
| **`Email:Enabled=true` and INCOMPLETE** | **refuses at startup**, naming every missing key in **both** forms — `Email:TenantId` *and* `Email__TenantId` | Someone MEANT to enable mail. Such an install sends nothing today, so refusing cannot take working mail down — it converts a silent drop into a named configuration error an operator can act on. |

Required when enabled: `Email:MailboxAddress` (both flows — the system send path is
`/users/{MailboxAddress}/sendMail`), plus `Email:TenantId`, `Email:ClientId`, `Email:ClientSecret`
for the client-secret flow, or nothing further for `Email:UseManagedIdentity=true`. The credential
half is `EmailOptions.MissingCredentialKeys()` — the same answer the watchers and `NoOpEmailSender`
report, deliberately not a second copy. Scope is OUTBOUND; the inbound keys
(`Email:InboundEnabled`, `Email:WebhookBaseUrl`, `Email:SubscriptionClientState`) are not guarded.

> 🚨 **This does NOT re-open #2510.** That incident was `EmailDeliveryGuard` *activating* the Graph
> sender from `IHostedService.StartAsync`, whose `ClientSecretCredential` constructor threw
> `ArgumentException: Invalid tenant id provided` → `Hosting failed to start` — an unactionable
> message from a container resolution. This verdict is reached from **inert configuration data
> only**: no container, no credential object, no I/O, so it cannot throw for any reason other than
> the one it reports. And the **unconfigured** install still starts. What changed is only the
> severity of the enabled-but-incomplete case, from a log line nobody reads to a named refusal.

⚠️ **Operationally**: an install that is enabled-but-incomplete TODAY will refuse to start once it
rolls onto a build carrying this guard. Complete the section, or set `Email__Enabled=false`, **before**
that roll. That is the intended trade — a portal that says mail is on and drops every message is the
defect this replaces.

🚨 **The order the two questions are asked in is load-bearing** (#2510). The guard runs inside
`IHostedService.StartAsync`, where a throw aborts the **host**, not a feature. It therefore asks the
configuration *first*, from data that cannot throw, and only a completely-configured install goes on
to resolve the sender. Asking the container first meant activating the Graph sender there — and it
built an Azure `ClientSecretCredential` in its constructor, which validates the tenant id eagerly —
so an unset `Email:TenantId` produced `Hosting failed to start` and a pod that never became ready.
A half-configured optional integration must never be able to do that.

---

## The Microsoft Graph reference sender

`GraphEmailSender` calls Graph
`/users/{mailbox}/sendMail` using the `Mail.Send` **application** permission, bridging the async Graph
call to the reactive surface through a bounded `IIoPool` (`_http.Run(...)`) — **never**
`Observable.FromAsync`, which is forbidden outside `IoPool` (see
[Controlled I/O Pooling](/Doc/Architecture/ControlledIoPooling)). Credentials come from `EmailOptions`:
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

- **Probe before composing**: `hub.ObserveSendAsCapability(objectId)`, so the UI can STATE the
  identity rather than the user discovering it in the recipient's inbox.
- **Never fall back silently.** If the user is not connected, offer `/auth/ea/connect`; the shared
  mailbox is only ever an explicitly chosen second option — and then set
  `EmailDelivery.AsSharedMailboxReplyingTo(userEmail)` so a reply still reaches the human.

### 🚨 The probe has THREE answers, and only one of them may say "you have not connected"

`IEmailSender.CanSendAsUser` returns `IObservable<bool>` — two answers for three states of the
world. Both consumers wrapped it in `.Catch(_ => Observable.Return(false))`, so a probe that timed
out or faulted became the same `false` as a probe that completed and found no credential. The send
dialog renders `false` at open time as *"Your Microsoft 365 mailbox is not connected yet"* beside a
Connect button — a sentence that is simply untrue on a transient fault, shown to a user who **had**
connected. Nothing was logged, so the third state was not even greppable. That is
[#3450](https://github.com/Systemorph/MeshWeaver/issues/3450), and it is
[#3433](https://github.com/Systemorph/MeshWeaver/issues/3433) one layer down.

`IEmailSender.ObserveSendAsCapability(userObjectId)` answers with `EmailSendAsCapability`:

| `EmailSendAs` | Means | What the UI may say |
|---|---|---|
| `Available` | The check completed; this sender can act as the person | Name the address the mail leaves from |
| `Unavailable` | The check completed and found nothing | *"Not connected yet"* + a Connect button — **the only truthful moment** |
| `Undetermined` | The check produced **no answer** (timeout, transport fault, unreadable content) | *"We could not check just now"* + a retry. **Never** a claim about the mailbox |

**The rendering rule lives on the answer, not in each UI.** `capability.OffersConnect` is true for
`Unavailable` **alone** — deliberately not `!IsAvailable`, because that expression *is* the collapse
written out. A consumer cannot re-derive the defect by accident.

Faults route into the state rather than being swallowed: `hub.ObserveSendAsCapability(...)` maps a
faulted **or silent** probe to `Undetermined` and logs it at Warning naming the user and the
diagnostic, once, where the sender is resolved. A probe that completes without emitting is the same
non-answer as one that threw — that is exactly how a dropped mesh read looks.

**The send path is different, and is deliberately unchanged.** At send time the decision really is
binary: send as the person, or show `AskWhichMailbox`. `hub.CanSendAsUser(...)` remains, folding
`Undetermined` to `false`, and that is the *correct* answer there — the user is **asked** which
mailbox rather than assumed connected. Only code that RENDERS A CLAIM must ask for the three-state
answer.

`IEmailSender.CanSendAsUser` itself is retained as the two-state shim so every existing implementer
keeps compiling and answering exactly what it answered before. The two defaults point in opposite
directions and never recurse: the interface's `ObserveSendAsCapability` folds `CanSendAsUser`, while
the hub extension `CanSendAsUser` folds `ObserveSendAsCapability`. So an old sender is unchanged, and
a sender that overrides only the new member is folded correctly at every consumer. It is not
`[Obsolete]` for the reason given on `IEaGraphAuth`'s retiring surface — MeshWeaver.Plugins builds
`-warnaserror` against a core checkout at a pinned ref, and the attribute would red that repo before
its own half could land.

---

## Cc and Bcc — one message, not N

A message with copied recipients **cannot be delivered as several mails**. Before
[#3473](https://github.com/Systemorph/MeshWeaver/issues/3473) every `SendEmail` overload took a
single `toAddress`, so `SendDocumentDispatch.ExportAndSend` did the only thing available to it —
`emails.ToObservable().SelectMany(to => hub.SendEmail(to, …))`, one mail per recipient. A To with
seven Cc, the ordinary shape of a business mail, went out as eight separate messages:

- no recipient could see who else received it, because each message's visible header named one person;
- Reply-All reached nobody but the sender, so the thread split into eight unrelated conversations;
- **Bcc had no expressible form at all** — sending separately to a blind recipient produces a message
  whose header does not match what anyone else got.

`EmailMessage` carries the whole envelope — `To`, `Cc`, `Bcc`, `Subject`, `HtmlBody`, `Attachments`,
`Delivery` — and `IEmailSender.SendEmail(EmailMessage)` sends it as ONE mail:

```csharp
hub.SendEmail(new EmailMessage
    {
        To = ["client@example.com"],
        Cc = ["colleague@example.com", "manager@example.com"],
        Bcc = ["archive@example.com"],
        Subject = "Q3 review deck",
        HtmlBody = "<p>Attached.</p>",
        Delivery = EmailDelivery.AsUser(objectId),
    })
    .Subscribe(ok => Log.LogInformation("sent: {Ok}", ok),
               ex => Log.LogError(ex, "send failed"));
```

`VisibleRecipients` is To ⧺ Cc (de-duplicated, case-insensitively) — the header every reader sees.
`Bcc` is deliberately absent from it: a blind recipient appearing there is the disclosure the field
exists to prevent. `AllRecipients` adds Bcc, for *"the send reached these addresses"* reporting only.

🚨 **A sender that cannot carry copies REFUSES; it never drops them.** The default implementation
forwards to the existing single-address overload when the message fits one (`To` of exactly one, no
copies) — so every sender written before this member keeps serving every call it could already
serve — and otherwise faults with a `NotSupportedException` naming the counts. Delivering to fewer
people than the sender addressed is the same class of defect as stamping an undelivered mail `Sent`
(#2023): the person is told the thing they asked for happened.

The host's `NoOpEmailSender` is the one deliberate exception. It delivers nothing to anybody and
says so, so there is no smaller message for it to deliver silently; it accepts the full envelope,
names every count in its log line, and still refuses outright on a mail-enabled-but-unconfigured
install.

### 🚧 The consumers live in MeshWeaver.Plugins, and land after the pin moves

Both seams above are **contract-side only** in this repository. Everything a person can SEE is in
`MeshWeaver.Plugins`, which builds `-warnaserror` against a core checkout at `MW_PLATFORM_REF` — so
the consuming half cannot compile until that pin carries these members. The order is therefore
**core → pin → Plugins**, and it is the reason the members are additive rather than a signature
change: an old sender at the old pin keeps compiling and answering exactly as before.

What remains, precisely:

| File (MeshWeaver.Plugins) | Change |
|---|---|
| `src/MeshWeaver.Mail.MicrosoftGraph/GraphEmailSender.cs` | Override `ObserveSendAsCapability` off `IEaGraphAuth.GetConnection`, mapping `EaConnection.Connected/NotConnected/Undetermined` straight through — it is the only implementation that can OBSERVE the third state. Override `SendEmail(EmailMessage)` to build one Graph `Message` with `ToRecipients`/`CcRecipients`/`BccRecipients` (the SDK already supports all three). |
| `src/MeshWeaver.Markdown.Export/Layout/SendDocumentLayoutArea.cs` | At dialog open, replace the `CanSendAsUser` probe and its `.Catch(… => false)` with `host.Hub.ObserveSendAsCapability(...)`; render the Connect panel on `capability.OffersConnect`, and a third panel — `ui.sendDocument.connectUnknown` plus a `ui.sendDocument.checkAgain` retry — on `IsUndetermined`. **Leave `SubmitSend` alone**: asking which mailbox is already the right answer for an unknown. |
| `src/MeshWeaver.Markdown.Export/Handlers/SendDocumentDispatch.cs` | Replace `SendToAll`'s per-recipient `SelectMany` with ONE `hub.SendEmail(EmailMessage)`, and give `ExportAndSend` `cc`/`bcc` parameters beside `rawEmails`. |

The two localization keys the third panel needs (`ui.sendDocument.connectUnknown`,
`ui.sendDocument.checkAgain`) ship in this change, in both `en` and `de`, so the consumer half adds
no catalog entries of its own. They still need the mirror sync described in
[Localization](/Doc/Architecture/Localization) — the plugins repo's React catalog compares against a
PINNED core commit, so a key added here reddens nothing and leaves that mirror silently stale until
`npm run sync:i18n -- --ref <merged core sha>` runs.

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
