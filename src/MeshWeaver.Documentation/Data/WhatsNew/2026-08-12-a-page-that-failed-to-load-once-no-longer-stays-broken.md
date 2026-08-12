---
Name: A page that failed to load once no longer stays broken
Category: Fix
Description: A single slow or unreachable moment could leave a document permanently unreadable until the server restarted — reloading just replayed the same old error. Reloading now genuinely tries again.
Icon: ArrowSync
Order: -20260812
---

# A page that failed to load once no longer stays broken

Some documents simply refused to open. Not slowly — instantly, with the same technical message
every time: *"No response received … the request may have been undeliverable."* Reloading changed
nothing. Waiting changed nothing. The page stayed broken for hours, while its neighbours in the
very same folder opened fine, the search results still listed it, and the folder listing still
showed it. Only opening the document itself failed.

That combination is the clue. Nothing was wrong with the document, and nothing was wrong with the
server. What was wrong was the memory of a single bad moment.

Behind every open document there is one shared connection to the piece of the system that owns it.
When that owner is briefly unreachable — it was asleep and is waking up, a deployment is rolling,
the database blinks — the connection fails, and the failure is handed to whoever was waiting. That
part is fine and expected; the next visitor should simply get a fresh connection.

Except the failed connection was kept. Every later reader was handed the *recorded* failure instead
of a new attempt, so no new attempt was ever made — which meant the system never learned that the
owner had come back. The proof was in the error itself: three attempts eleven minutes apart came
back quoting the identical internal request number. It was not failing three times; it was showing
one old failure three times. The safety valve designed to notice repeated failures and slow them
down could not help either, because from its point of view the failure had only ever happened once.

Two things kept the stale connection alive: the shared connection register, and a second cache one
layer below it that handed the same dead connection back even after the first was cleared. Both are
now dropped the moment a reader arrives and finds the previous attempt had ended in failure, with
nothing currently holding it back on purpose. The next read opens a genuinely new connection to the
owner, and a document that failed once opens normally as soon as its owner is reachable again.

The protection against a genuinely broken owner is unchanged, and in fact now works as intended:
repeated real failures are still counted and still backed off, briefly and then progressively, so a
wedged owner is never hammered. The difference is that "try again" now means trying again.
