---
Name: A refusal no longer blames a timeout that never happened
Category: Fix
Description: When creating an item was refused because the platform could not read the item type's definition, the message always said the read had timed out — even when it had failed outright, in a fraction of a second. It now names both possibilities, so nobody goes looking for a delay that never happened.
Icon: Bug
Order: -20260918
---

# A refusal no longer blames a timeout that never happened

Creating an item whose **type is defined in the mesh** — a CRM client, a course lesson, anything a
package brought with it rather than the platform shipping it — makes the platform read that type's
definition first, to find out whether items of it live at the top level or inside something else.

If that read does not produce an answer, the create is refused rather than guessed at, and you are
asked to try again. That is deliberate and unchanged: the platform will not place an item on a
half-answer.

## What was wrong with the message

There are **two** ways the read produces no answer — it can **fail**, or it can **take too long** —
and by the time the refusal is written the platform genuinely cannot tell which happened. The
message only ever described the second one:

> …its NodeType definition could not be read **within 10 s**.

So a read that failed *immediately* was reported as a ten-second wait. On 2026-09-17 that sentence
sent an investigation after a delay that had never occurred: the refusal it described had been
produced in **148 milliseconds**, and establishing that the "10 s" was wording rather than a
measurement cost real time before anyone could look at the actual fault.

## What changes

**The message now names both possibilities**, because both are possible and neither can be ruled
out:

> …the read of its NodeType definition **failed, or did not answer within 10 s**.

German reads the same way, and the refusal is otherwise untouched — same conditions, same
retry advice, same fail-safe behaviour.

## Why this is worth a note

A message that names one cause when it knows of two is not a wording problem. It points the next
person at the wrong thing, and here it pointed at a time limit — where the tempting "fix" is to
raise the limit, which would have changed nothing at all except how long the failure took to
appear.

The full design — why the read happens, why an unanswerable one refuses rather than assumes, and
what it deliberately does not do — is
[Partition Ownership Resolution](/Doc/Architecture/PartitionOwnershipResolution).
