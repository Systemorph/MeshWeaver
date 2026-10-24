---
Name: The Opt-In Mail Copy
Category: Architecture
Description: >-
  The provider-neutral mail copy that lives in core (MeshWeaver.Graph, namespace MeshWeaver.Graph.MailCopy):
  the stored shapes, the node types, the copy store, the generic engine and the attachment rules, how a
  provider module plugs a feed into it, and the per-installation, per-account switch. What is pinned because
  stored copies and the Executive Assistant's StoredPath contract outlive any move.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="3" y="5" width="18" height="14" rx="2"/><path d="m3 7 9 6 9-6"/></svg>
---

# The Opt-In Mail Copy

Mail is read **live** from the provider by default. A person can switch a **copy** on for an account, on one
installation, and that installation then keeps the last 90 days of the account's inbox as nodes in the
person's own partition: searchable, instant to open, and — for real file attachments up to 25 MB — with the
files stored and indexed.

## Where it lives

Since the move into core (2026-10) the provider-neutral half is in `MeshWeaver.Graph`, namespace
`MeshWeaver.Graph.MailCopy`. It was born in the Mail module (`MeshWeaver.Mail.MicrosoftGraph`) and holds no
Microsoft Graph code:

| Piece | What it is |
|---|---|
| `MailCopySource`, `MailMessageCopy`, `MailAttachmentCopy` | The stored shapes. The type names ARE the `$type` discriminators (`MailCopySource`, `MailMessageCopy`) and the property spellings are the stored ones — **do not rename**; every stored copy and the Executive Assistant read them. |
| `MailCopyNodeTypes` | Paths (`{owner}/_MailCopy/{key}/{digest}`), ids, generations, the two node types (`KeepsHistory = false`: a switch-off must leave nothing behind) and `AddMailCopyTypes()`. |
| `IMailCopyStore` / `MeshMailCopyStore` | The copies as mesh nodes, written as System on the owner's behalf; generation-checked commits; deletes files and index before nodes. |
| `MailAttachments`, `IMailAttachmentSource`, `IMailAttachmentStore` | The attachment rules (files only, up to 25 MB, not inline) and the seam a provider implements to list and fetch. |
| `MailCopyEngine`, `IMailCopyFeed` | The generic sync: generations, the lease, the window, tombstones, attachments, the switch-off. A provider supplies a feed (its incremental change source). |
| `IMailCopySwitch`, `IMailAccountCopy` | The per-installation, per-account switch (below). |

A provider module (Mail for Outlook, Google for Gmail) owns only what talks to its provider: Outlook's engine
(Graph delta), Gmail's feed (history id), the inbox legs that read the copy, and the settings tab.

## The attachment contract

`MailMessageCopy.Attachments` is `null` while undecided and a list once decided (an empty list for a message
without any). Each `MailAttachmentCopy` carries `AttachmentId`, `Name`, `ContentType`, `Size`, `IsInline`,
`Kind` (`file`, `item`, `reference`), `StoredPath` and `SkippedReason` (`inline`, `item`, `reference`,
`too-large`, `fetch-failed`). **`StoredPath` = `{copyPath}/attachments/{attachmentIdDigest}/{cleanedFileName}`**,
read through the copy node's `attachments` collection (every node has one; a nested node has no `content`
collection of its own). The shape is identical across providers.

## Opt-in, per installation, per account

A copy exists only where the person switched it on, **on that installation**. A connected account never
starts one by itself, and a Google or Microsoft grant held on a mother instance for a child implies no copy
there. `IMailCopySwitch.Offer(owner)` lists the connected accounts with their switch (all off until someone
says otherwise) — the call onboarding makes to ask "Keep a copy on this installation?" — and `Enable` /
`Disable` / `IsOn` act per `(provider, accountId)`. The caller must have established that the viewer is the
owner.

## Layout of an account's copy

The default account of a provider keeps `{owner}/_MailCopy/{provider}`; every other connected account is its
own copy at `{owner}/_MailCopy/{provider}.{accountId}` (`MailCopyNodeTypes.KeyFor`).
