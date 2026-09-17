---
Name: A held sync says whether to roll or to fix the lane
Category: Fix
Description: A source held by the publication seal used to state the commit it was sealed at and stop there — a sentence equally consistent with a broken publishing lane and with an instance whose image has fallen behind. It now names which, and the remedy.
Icon: ArrowSync
Order: -20260916
---

# A held sync says whether to roll or to fix the lane

A GitSynced Space advances only to a commit **sealed for the framework identity its instance
runs**. When it cannot, the sync config says so:

```
lastSyncOutcome: Held
lastSyncNote: "built at e2ef5679, not sealed for this instance
               (identity sd608997…: 'plugins' is sealed at 627fb3cd)"
```

That sentence is true, and it describes two situations that call for opposite work: either
**nothing has sealed recently** — the publishing lane is broken, and another publication is exactly
what is needed — or **seals are advancing under a newer identity this instance does not run**, in
which case the instance's *image* is behind and no further publication will ever release the hold.

Read the wrong way it costs real time. On 2026-09-16 the public portal was held under identity
`sd608997…` while the live publication was sealed under `s799247a…` — the second case — and two
issues stood open describing it as the first, against a publishing lane that was green and an inbox
that was empty.

**The note now says which.** The release markers under the published root name every platform line,
not only the instance's own, so the instance can read the answer without being asked:

```
… 'plugins' is sealed at 627fb3cd. The registry has since sealed 3.0.0-ci.8600 under framework
identity s799247a…, which this instance does not run — so this source advances when this
instance's IMAGE does (a roll), NOT when another publication lands
```

Where the instance cannot be placed on a line at all — no markers, or an identity none of them name
— the note reads exactly as it did before. A hold is allowed to be silent about its direction; it is
not allowed to guess one.

Full detail: [Publication Seal Starvation](/Doc/Architecture/PublicationSealStarvation).
