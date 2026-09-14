---
Name: An install now says whether it actually landed whole
Category: Fix
Description: A plugin install that repaired itself was reported as a failure, and one that quietly left files behind was reported as nothing at all. Both now say what actually happened, once it has happened.
Icon: Checkmark
Order: -20260914
---

# An install now says whether it actually landed whole

Installing a plugin writes a set of files into the mesh and then stamps a record saying what it
wrote. Two things about that were reported the wrong way round.

## A repair that worked was reported as a failure

Before installing, the platform compares what the record claims against what is really there. When
something is missing it repairs it — and it said so, loudly, in the voice reserved for faults:

> *… 1 of 14 declared node(s) are ABSENT from the mesh … the install is being REPAIRED rather than
> skipped.*

That sentence was written **before** the repair ran, and nothing was ever written afterwards. So the
line an operator saw was an alarm about something that, eight seconds later, was fine. Measured on a
production portal on 13 September: a file was named missing at 22:02:37 and was back at 22:02:45.

Because those alarms are collected automatically into tickets, a repair that worked re-opened a
ticket about a completely different problem, every time it happened.

## An install that did *not* land whole was reported as nothing at all

The comparison only ran when the platform was about to **skip** an install. An install that actually
wrote files was never read back afterwards — it stamped its record and finished, and nothing asked
again until the next version of the package came along.

On the same portal, on the same evening, one package recorded 224 files as installed while three of
them never arrived. Eight features that depended on them stopped working and stayed broken for the
next twelve hours. Nothing in any log mentioned the install; the only clue anywhere was an unrelated
complaint about a missing piece of code.

## What changes

**Every install reads back what it wrote, and reports the outcome.** One of three sentences, at the
severity the outcome deserves:

- **It landed whole** — every file the record claims is present. Recorded as ordinary progress.
- **Files are still missing** — the install ran, the record is stamped, and the mesh is still short.
  This is the alarm, and it **names the files**, because "something is missing" is not something
  anyone can act on.
- **It could not be checked** — the mesh could not be read, or the package declares nothing to
  compare. Reported as a warning, and explicitly *not* as a pass.

**The pre-install notice stays, at the right volume.** It still says a repair is starting, as a
warning rather than an alarm — and now something follows it that says whether the repair worked.

## What this does not change

**The check can never fail the install it is checking.** An install that landed and a check that
could not run are different facts. A check that cannot run says so and gets out of the way.

**Nothing is checked twice.** An install the platform skips was just compared against the mesh —
that is why it was skipped — so it is not compared again.
