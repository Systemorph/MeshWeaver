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
installation, and that installation then keeps the last 90 days of the account's inbox as nodes in a
partition of the person's OWN for their mail, `{owner}_MailCopy`: searchable, instant to open, and — for real
file attachments up to 25 MB — with the files stored and indexed.

## Where it lives

Since the move into core (2026-10) the provider-neutral half is in `MeshWeaver.Graph`, namespace
`MeshWeaver.Graph.MailCopy`. It was born in the Mail module (`MeshWeaver.Mail.MicrosoftGraph`) and holds no
Microsoft Graph code:

| Piece | What it is |
|---|---|
| `MailCopySource`, `MailMessageCopy`, `MailAttachmentCopy` | The stored shapes. The type names ARE the `$type` discriminators (`MailCopySource`, `MailMessageCopy`) and the property spellings are the stored ones — **do not rename**; every stored copy and the Executive Assistant read them. |
| `MailCopyNodeTypes` | Paths (`{owner}_MailCopy/{key}/{digest}`), ids, generations, the partition root and the owner's grant, the node types (`KeepsHistory = false`: a switch-off must leave nothing behind) and `AddMailCopyTypes()`. |
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

## The copy partition, `{owner}_MailCopy`

🚨 **The copies never live in the person's home partition** (owner, 2026-10-25: *"i don't feel good downloading
all this stuff to user space. let's see that we create a dedicated partition just for the inbox for every
user"*). Each owner's copies are their own top-level partition, named by the owner's id with `_MailCopy`
appended — the old `{owner}/_MailCopy` with the slash replaced (`MailCopyNodeTypes.NamespaceFor`).

- **Made on switch-on, as System.** `MeshMailCopyStore.Create` first makes the partition exist — the storage
  schema (`EnsurePartitionProvisioned`: no partition, no write), the `Admin/Partition/{owner}_MailCopy`
  definition that routes it, the root node (type `MailCopyPartition`) and the grant — each create-if-absent,
  so a second click or a second replica changes nothing.
- **ONE grant: the owner's Viewer grant**, `{owner}_MailCopy/_Access/{owner}_Access`. Reads run as the owner;
  every write runs as System, so Viewer is all the owner needs. Nobody else is granted anything — a global
  administrator included — and **a grant on the person's home partition does not reach their mail**: a
  delegate needs a grant on the copy partition itself.
- **An owner id that ends in `_MailCopy` is refused** (`MailCopyNodeTypes.IsValidOwner`): the home partition
  of a user called `jane_MailCopy` would be the copy partition of `jane`.
- **Attachment files follow the node**: the `attachments` collection of `{owner}_MailCopy/{key}/{id}` is
  stored under `attachments/{owner}_MailCopy/…` on the content store, never under the person's home folder.

### Copies filed before 2026-10-25 are moved, not downloaded again

Before the change the copies lived at `{owner}/_MailCopy/…`. Every sync first runs
`IMailCopyStore.MoveLegacy(owner, key)`, which moves what is still there into the copy partition: the source
with its generation, cursor and window (so the next sync resumes, and nothing is fetched again), then each
copy — written at its new path, its attachment files copied (`IMailAttachmentStore.CopyLegacy`), its stored
paths re-rooted (`MailCopyNodeTypes.Relocated`), and only then its legacy files (`DeleteLegacy`) and node
deleted — and the legacy source last. Every step is an overwrite or a no-op when repeated, so a move that
stops anywhere finishes on the next sync; a failure stops before the legacy files go. `Disable` moves first
too, so a copy switched off while still at the legacy place cannot come back on the next sync.

It is the portal, not the database migration Job, that moves them: the Job does not mount the content store,
so it could move the rows but never the files.

## Layout of an account's copy

The default account of a provider keeps `{owner}_MailCopy/{provider}`; every other connected account is its
own copy at `{owner}_MailCopy/{provider}.{accountId}` (`MailCopyNodeTypes.KeyFor`).

## Who may call the stores

`MeshMailCopyStore` and `MeshMailAttachmentStore` write as System on the owner's behalf, and every NodeType
can reach them, so they authorize the caller before they elevate
([Authorize as the Caller, Execute as System](../AuthorizeAsCallerExecuteAsSystem)):

- **The code** that called them is judged by the in-mesh impersonation guard exactly as if it had called
  `RunAsSystemFor` itself (each entry point is an `[ImpersonationSurface]`). A runtime check sees only the
  immediate caller, so the same entry points — on the interfaces and on the concrete stores, plus the
  engine's `Kick`, `Sync` and `Enable`, the account switch's `KickAll` and `Enable`, and the provider
  switch's `Enable` — are also reported at COMPILE time (`InMeshImpersonationReferences`), which finds a
  method group handed to a later platform callback as well. `KickAll` and every `Enable` judge THEIR caller
  and hand that verdict on, never the platform assembly that then calls the engine: `Enable` starts the
  System backfill, which calls the engine's store, so an in-mesh caller that brought its own store must not
  pass because the kick comes from platform code. The engine's sweep of obsolete copies is private: it runs
  only inside a checked sync.
- **The identity** the store was CALLED under — the request context, else the circuit's — read at the call,
  never when the cold result is subscribed: a write composed by one person and subscribed after their
  scope ended is still theirs. The owner may act on their own copy; anybody else needs the permission the
  act needs (Read, Update or Delete) on `{owner}_MailCopy`. System and hub principals pass. **No identity,
  or an anonymous one, is refused**: a lost identity is never read as trusted background work. **No mesh
  at the call is refused too**: the work resolves the hub again when it runs, so a call composed before the
  portal existed and subscribed after would otherwise run as System unchecked. A non-owner's permission
  answer is taken off the evaluator's gate before any work starts (`TakeDecisionOutsideGate`).
- **The background sync declares itself.** `MailCopyEngine` (given the host's `AccessService`) makes every
  store call of a sync as System on the owner's behalf, at the call — a sync runs after provider hops that
  keep no ambient identity. Switching on, off and reading go through the caller's own identity; `Enable`
  reads it when it is CALLED and re-establishes it when its cold result is subscribed, so a System or admin
  subscriber never launders another person's switch-on. The per-account wrappers compose their engine calls
  at entry for the same reason, and subscribe them only after their account check: composed after the feed's
  answer, they would be judged as whatever identity that answer arrived under. `MailCopySwitch.Offer` calls
every provider at entry, never in a lazy enumeration its `Merge` would run at subscribe. One sync per owner runs per engine: a `Sync`
  subscribed while another runs emits null at once, and the gate opens again when the running one
  completes, fails or is disposed. An engine
  built without the `AccessService` on a host that has one gets its syncs refused, loudly.
- **The target**: the attachment store writes to and empties only `{owner}_MailCopy/{key}/{id}` — a copy
  node of the owner — and never a relative path that leaves its `attachments` collection.

## Reads that decide writes

What a write is decided on comes from the node's stream, never from the index: `Read` and `Revision` use
the index only to learn that a copy EXISTS and then read it from its stream, and a copy is deleted with its
files when its LIVE content may hold files. An index miss is not proof of absence: `Remove` leaves a copy
the index does not list yet to the next sweep instead of deleting it without its files.

A tombstone keeps the "may hold files" marker (`HasAttachments`): once it lands, the copy's inventory is
gone, so a cleanup that failed after the tombstone was written is repeated by the next attempt, which finds
the marker on the tombstone it replaces.

**Open:** the conditional writes (`Commit`, `ReplaceIfNewer`, `WriteIfUnchanged`, `SetAttachments`) decide
on the writer's node-stream mirror. The owner's three-way merge refuses a field the write changes when the
owner's value moved since the mirror's base, which is what makes the lease safe across replicas; a field the
predicate only READS (a generation, a version, `Removed`) is not checked by the owner. Closing that needs an
owner-side precondition on a cross-hub update, which the platform does not have yet.

## What a provider adds beyond the sync

A provider supplies an `IMailCopyFeed` (its account, its incremental changes, its attachments) and the engine does
the rest. Three more things come from the engine, so the provider only maps copies to its own rows:

- **Reading back.** `ServesFromCopy(owner)` answers the source once the backfill is done and only for the account
  connected now (otherwise null, and the inbox reads live); `ReadInbox` pages the copy newest first and matches a
  search against subject, sender, preview and body *before* it applies the bound; `StoredCopy` opens one message.
- **Following the person's own Delete and Undo.** `FollowMove(owner, id, removed, askedAt)`: a Delete tombstones
  the copy unless something newer already happened to it; an Undo asks the feed to read the message again
  (`Reread`) and writes it back, inside the window only, stamped with when the person asked.
- **Settling an event that lost.** When the sync's write is refused because a newer local action landed first, a feed
  that can `Reread` lets the engine ask the mailbox for the message's present state and settle on it with a
  compare-and-set on the revision (up to `MaxReconcileAttempts`); a feed that cannot has the event dropped.

The engine's background paths declare System on the owner's behalf; the public surfaces (`Enable`, `Kick`, `Sync`,
`ServesFromCopy`, `FollowMove`) are judged as the CODE that calls them by the in-mesh impersonation guard.


## Listing the copies that are on

`IMailCopyStore.SourceKeys(owner)` lists the keys of the owner's persisted copies whether or not their account is still connected, so a copy whose account is gone can still be shown and deleted. `IMailCopyStore.EnabledSources()` lists every copy that is on across owners (platform scope, refused to in-mesh code); it is what a keeper reads after a restart to resume them. Both are default interface members that answer none, so a store that cannot list changes nothing.
