---
Name: Track changes now come from the document's history
Category: Feature
Description: Tracked changes are computed from the version history instead of being stored beside the document — edits apply immediately, each one shows who made it and when, and the only action is a one-click revert.
Icon: Sparkle
Order: -20260808
---

# Track changes now come from the document's history

Every version of a document already records who saved it, when, and exactly what
it said. That is the whole story of "what changed" — so tracked changes are now
**computed** from it rather than kept as a second, separate record next to the
document.

What you see is the same redline as before: insertions underlined, deletions
struck through, a card in the sidebar naming the author and the time. What is
gone is the copy that used to drift. A stored suggestion carried its own idea of
where it belonged, and that idea went stale every time somebody edited the
paragraph above it — leaving suggestions pointing at text that had moved, or at
text that no longer existed. Nothing to go stale now: the redline is worked out
fresh against whatever the document says at the moment you look at it.

## Suggesting an edit applies it

**Suggest Edit** — in the document and as an agent tool — now writes the edit
straight into the document. There is no pending limbo where a proposal sits until
somebody notices it, and no way for a suggestion to be silently lost because the
surrounding text moved on.

That also means there is no **Accept** button any more: the change is already
there, so keeping it is simply doing nothing.

## Rejecting is reverting — and it is on the record

Each change card has one action: **↩ Revert**. It puts the previous text back,
re-locating the passage against the live document first, so a colleague editing
at the same time can never cause a revert to cut the wrong words out. The revert
is a normal save, which means it shows up in the version history exactly like the
edit it undoes — rather than a suggestion quietly vanishing with no trace of who
turned it down.

To undo a whole batch, use **Versions** and restore the version you want. One
decision, one save.

## Attribution is honest about what it does not know

A change is credited to the version that introduced it. When a passage was worked
over by several people, the card credits nobody rather than guessing — the edit
genuinely belongs to more than one of you.

Documents that still carry suggestions created by an older version of the platform
keep showing them; nothing new is written that way.
