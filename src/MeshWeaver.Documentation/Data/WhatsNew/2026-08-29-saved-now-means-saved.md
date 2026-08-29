---
Name: "Saved" now means the change was actually saved
Category: Fix
Description: An edit to a busy document could be reported as saved while the server was still deciding — and a refusal that arrived a moment later was never shown. Edits now report the server's real answer.
Icon: CheckmarkCircle
Order: -20260829
---

# "Saved" now means the change was actually saved

When you edited something whose owner was busy — a document being written to by an agent round, a
node under load — the edit was reported as saved after a short wait, before the server had actually
decided anything. Almost always it then landed and nobody noticed. But if the server went on to
refuse the change — most importantly because you did not have permission to make it — that refusal
arrived too late to be shown, and nothing anywhere reported a problem. The screen kept your edit,
the stored value never changed, and any work that followed carried on as if it had.

An edit now reports what the server actually decided. If it was accepted, you are told so once it is
committed. If it was refused, you get a real error saying why, whether that answer comes back
immediately or a few seconds later. Adding and deleting have always worked this way; editing now
does too.

The trade is visible in one place: editing something whose owner is genuinely busy now waits for
that owner instead of answering optimistically, so a save can take a moment longer than it used to.
It waits for an answer, not forever — if the owner never answers at all, the edit is reported as
unconfirmed rather than as saved, and you can re-open the item and try again.
