---
nodeType: Skill
name: /share-email
description: Send a mesh document as an email — in the user's own name, with embedded layout areas rendered into the message and markup a mail client can actually display.
icon: Mail
category: Skills
order: 16
---

Use this when the user wants to **send or share a document by email** ("email this to X", "share ⇒ as email", "send the proposal to the client").

The document becomes the **message itself** — not an attachment — with its embedded layout areas resolved to static markup. Sending is done through the framework; never hand-build the mail.

# 1. How to invoke

**From the UI**: node menu → **Share ⇒ as email**, in the same group as Export to PDF / DOCX (`SendDocumentLayoutArea.SendArea`).

**Programmatically** — one entry point, reactive, subscribe to drive:

```csharp
SendDocumentDispatch.ExportAndSend(
    hub, workspace, sourcePath,
    new DocumentExportOptions { Format = ExportFormat.Html, BaseUrl = portalOrigin },
    recipientUserPaths: [userNodePath],     // mesh User nodes — email read under the caller
    rawEmails: ["client@example.com"],
    subject: "…",
    htmlBody: "<p>covering note</p>",       // prepended INTO the document
    delivery: DocumentDelivery.EmailBody,   // the document IS the body
    identity: EmailDelivery.AsUser(objectId))
  .Subscribe(r => { /* r.Success, r.SentTo, r.Error */ });
```

`DocumentDelivery.Attachment` + `ExportFormat.Pdf` keeps the old attach-a-file behaviour.

To render the HTML without sending: `EmailDocumentComposer.Compose(hub, title, markdown, nodePath, new EmailHtmlOptions(baseUrl))`.

# 2. Identity — send as the person, never silently as the portal

Sharing is a **personal act**: the mail goes out **as the signed-in user**, via their delegated personal-assistant credential (`EaCredential` + delegated `Mail.Send`, already part of `EaGraphAuth.Scopes` — connecting the personal assistant grants it, no extra consent step). It lands in their own Sent Items and replies come back to them.

- **Always probe first**: `hub.CanSendAsUser(objectId)`. State the identity in the UI before composing.
- **Not connected ⇒ do NOT fall back silently.** Offer `/auth/ea/connect` as the primary action. The shared mailbox is only ever an **explicitly chosen** second option, and then `EmailDelivery.AsSharedMailboxReplyingTo(userEmail)` so replies still reach the human.
- The shared mailbox (application credential) remains correct for **system** mail — notifications, invitations, automation.

# 3. Email-safe HTML — every rule below is a real Outlook defect

Outlook on Windows renders through the **Word** engine. `EmailDocumentComposer` handles all of this; keep it that way rather than emitting mail markup by hand.

- **Resolve live layout areas** to static markup. An `@@("area:…")` embed is only an empty anchor div until something fills it — see §5.
- **Tables only** for multi-column layout: Word has neither flexbox nor grid.
- **Width on every `<td>`/`<th>` — Word IGNORES `<colgroup>`.** Widths proportional to each column's content volume, square-root damped with a floor and cap, so a prose column cannot starve short ones (`EmailTableSizer`).
- **Inline CSS only** — no `<style>`, no `<link>`; Gmail/Outlook.com strip them.
- **Absolute https URLs** — a mail client has no page origin, so a relative link is dead in the inbox.
  🚨 Never test "is this absolute?" with `Uri.TryCreate(v, UriKind.Absolute, …)`: on Unix a root-relative path parses as an implicit `file:` URI and reports absolute (and returns false on Windows, hiding it). Use `EmailHtmlSanitizer.HasScheme`.
- **No inline `<svg>`** — Word draws a broken-image box. Omit the icon instead.
- **Images as `cid:` inline parts** (`EmailAttachment.ContentId`): `data:` URIs are stripped by classic Outlook and remote images are blocked until "Download pictures".
- **No script.**

Also: HtmlAgilityPack caches rendered markup — mutate attributes with `SetAttributeValue`, never `attribute.Value = x`, or the DOM changes and the output does not.

# 4. Graph data is queried LIVE — never replicated

Graph is the system of record and always current. **Do not ingest, sync or mirror mailbox data (messages, conversations, contacts) into the mesh** — it buys nothing and leaves personal correspondence at rest in the mesh.

- **Recipients**: live `GET /me/people?$search=`, debounced. Cache per user only in an instance cache on a mesh-scoped singleton — never a static collection, never as nodes.
- **Replying into a thread**: query Graph live for the user's recent messages, let them pick, then `POST /me/messages/{id}/createReply`, replace the body with the rendered document, and send. **Graph supplies the threading headers** (`In-Reply-To`/`References`) — never hand-roll them. The mesh may hold at most an `InternetMessageId`/`ConversationId` reference for audit.
- The inbound mail→agent channel (`EmailInboundProcessor`, `GraphSubscriptionService`) is a separate, deliberate path — leave it alone.

All Graph calls go through `IIoPool` and return `IObservable<T>`; never `async`/`await` in hub or Blazor code, never `Observable.FromAsync`.

# 5. Known limitation — state it honestly

**Export to PDF and DOCX do NOT render embedded layout areas.** The document builder's Markdig pipeline omits the layout-area extension, so an `@@("area:…")` embed prints as **literal source text**; the pixel deck path prints it **blank**. Only the email/HTML path resolves areas. If a user needs a PDF of a document with embedded views, tell them this rather than letting them send a broken document.

Reference: [Sending Email](/Doc/Architecture/SendingEmail).
