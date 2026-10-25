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

### Copies filed before 2026-10-25 are retired and downloaded fresh

Before the change the copies lived at `{owner}/_MailCopy/…`. They are **not moved**: the owner decided
(2026-10-25) to delete them and download fresh, because moving a live copy — while replicas on the previous image
keep syncing and serving it during a roll, and the index trails every write — kept producing races, each a way to
lose mail or bring a switched-off copy back. Every sync, switch-on and switch-off first runs
`IMailCopyStore.RetireLegacy(owner, key)`:

1. **Fence.** The legacy source is read from its stream and a generation-checked commit at its owning hub takes its
   lease for this attempt (`retire:{guid}`, `RetireLease` 2 h) **and marks it not backfilled**: a sync of the legacy
   place, of either image, stands down on the lease, and a reader of either image reads the inbox live (readers honor
   `Backfilled`). A lease held already refuses the attempt; the next sync repeats it.
2. **Delete the switch-off way.** Every legacy copy, from complete listing frames only: its attachment files and
   index first (`IMailAttachmentStore.DeleteLegacy`), then its node; the legacy source last. A file that cannot be
   deleted stops the retirement, and the node keeps the only handle on it.
3. **Switch a fresh copy on** — only when THIS attempt's delete removed the legacy source, and not for a switch-off:
   a new generation in `{owner}_MailCopy/{key}` with the legacy copy's window and account, no cursor, not backfilled.
   The next sync downloads it; with the generation rebuild (#7166) the inbox reads live meanwhile, never blank. A
   legacy source that vanished before this attempt's delete was switched off by an old-image replica, and nothing is
   switched on.

However an attempt ends — an error, the sync's bound, a disposal — it releases a fence it still holds, so the next
sync need not wait out the lease. `Disable` retires without switching a fresh copy on. The residual: an Undo or
Delete applied by an old-image replica (which takes no lease) between the last complete pass and the legacy
source's delete leaves that one copy's files, if it had any, under the home folder.

It is the portal, not the database migration Job, that retires them: the Job does not mount the content store, so
it could delete the rows but never the files.

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

## Rebuilding: alongside, never instead

A copy is rebuilt when its projection is older than the build's, when the provider refuses the stored cursor, or
when the connected mailbox is another account. What is served meanwhile depends on which:

- **Projection upgrade and refused cursor (a copy that is already `Backfilled`) build ALONGSIDE.** The engine mints
  `MailCopySource.PendingGeneration` and backfills it from `PendingCursor`; `Generation`, `Cursor` and `Backfilled`
  are untouched, so `ServesFromCopy`, `ReadInbox` and `StoredCopy` keep answering from the old generation for the
  whole backfill (90 days of mail). Only the step that reaches the present **flips** the source in ONE commit
  (`Generation` := pending, `Cursor` := the feed's, pending cleared), and only then are the old generation's copies
  removed — attachment files and index first, through the store's existing delete path. A failed retire is logged and
  left to the next sync's sweep; the flip stands. While the old generation is frozen it receives no delta steps; the
  person's own Delete and Undo keep acting on it, and the delta of the new generation reports whatever changed at the
  provider.
- **A crash or failure mid-rebuild RESUMES the pending generation** from `PendingCursor` on the next sync; it never
  mints another. A refused cursor on the pending generation itself replaces it (the abandoned one is removed).
  The leftovers sweep stands down while a pending generation exists (its copies are not leftovers); the next sync
  after the flip sweeps.
- **A mailbox-account change rebuilds IN PLACE.** The old account's mail must never be shown under the new one, so the
  generation rotates at once, `Backfilled` is false, the copy is not served (the inbox reads live) and the old and any
  pending generation are removed. Readers are never failed: a copy that disappears between the index answer and the
  node read (the stream ended with that path's delete, or the failure is stamped `NodeAbsent`) reads as absent; any other failure propagates. A copy that was never served is rebuilt in place too.
- **An account the feed cannot name right now** (an empty id) is *unknown*, not *another account*: it never rebuilds a
  healthy copy, the sync pauses (no feed step runs, so nothing lands on the bound account's generation), and it is not
  served (reads need an id equal to the bound one).
- **Switching off** (`Disable`) is unchanged: the generation rotates (and any pending one is dropped from the source),
  every copy of every generation under the source is deleted with its files, then the source.
