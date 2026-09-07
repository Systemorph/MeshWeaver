---
Name: A synced Space can say when it last synced
Category: Fix
Description: The GitHub Sync tab used to show a "last synced" date that could be months older than the commit beside it, because it was showing a different fact under that name. It now says when a sync last ran, what it concluded, and which commit the Space is on.
Icon: Checkmark
Order: -20260907
---

# A synced Space can say when it last synced

The **Settings → GitHub Sync** tab has a status line under the Sync section. It used to read
something like *"Last synced: 2026-07-11 — commit e8f315bf"* — a July date beside a commit from
this morning. That is not a small display glitch: it made the tab unable to answer the first
question anyone asks about a Space that is behind, which is simply **when did this last sync?**

## What was actually going on

The date and the commit were two different facts, printed as if they were one.

The date was the **conflict horizon** — the last moment the Space and the repository were fully
reconciled. It is deliberately held back whenever a sync did *not* fully reconcile: when the
repository moved but its content did not change, when your own edits on the server were preserved
rather than overwritten, or when something failed to land. Holding it back is what protects
uncommitted work on the server from being deleted by a later push, so it is not a value that can
simply be made to move.

The commit, meanwhile, advances whenever the Space has genuinely got that commit's content —
including when there was nothing new to import.

So both values were right, and neither of them was *"when did a sync last run"*. Nothing recorded
that.

## What changes

**The Space now records every sync run.** When a sync last ran and what it concluded — imported,
nothing to import, imported with errors, failed, committed — is stored alongside the commit, on
every outcome, including the ones that deliberately change nothing else.

**The status line shows three facts as three facts**, in your language: when the last sync ran,
what it concluded, and which commit the Space is on. A Space whose last sync predates this change
shows the commit alone rather than inventing a date for it.

**A sync that fails now leaves a trace.** Previously a run that could not land everything wrote
nothing at all to the Space's sync settings, so a repeatedly failing sync looked exactly like a
Space nobody had touched.

## What this does not change

**The conflict horizon still behaves exactly as before.** It is a separate value and still moves
only on a sync that really reconciled — that is what keeps a *Two-way* Space from losing edits made
on the server. Recording that a sync ran cannot, and does not, move it.

**Nothing about what gets imported changes.** Which commit is read, what is written, and what is
pruned are all untouched. This is about what the Space can tell you afterwards.
