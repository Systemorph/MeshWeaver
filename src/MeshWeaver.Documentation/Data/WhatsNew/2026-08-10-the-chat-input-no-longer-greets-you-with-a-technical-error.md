---
Name: The chat input no longer greets you with a technical error
Category: Fix
Description: Opening the portal could pop a raw "No response received…" message while the chat input was waking up. The chat now waits and retries quietly, and only reports genuine failures.
Icon: Sparkle
Order: -20260810
---

# The chat input no longer greets you with a technical error

Navigating to the home page sometimes greeted you with an internal-looking error — "Loading the
composer: No response received in hub … the target hub was not found" — before you had done
anything at all. Dismissing it and carrying on usually worked, which made the message all the more
confusing: nothing was actually broken.

Your chat input keeps its selections (agent, model, effort) on a small per-user record. Like
everything else in the mesh, that record's owner is put to sleep when it has been idle for a while
and woken on the next visit. Waking up occasionally takes longer than a single request is allowed
to wait, and the chat treated that late wake-up as a hard failure: it gave up on the first try and
showed you the framework's raw timeout text — a message that describes plumbing, not anything you
could act on.

The rest of the portal already knew better. Every page view classifies this exact situation as
transient and retries a bounded number of times while the owner finishes waking; the chat input was
the one reader that never did. It now uses the same bounded retry: a slow wake-up is retried
quietly until the record answers, and the selections appear on their own. If something genuinely
fails — a real error, not a slow start — it is still reported. The same fix covers the side
panel's listener that opens your newly created thread after you press Send, which a single slow
wake-up could previously switch off unnoticed until the next page visit.
