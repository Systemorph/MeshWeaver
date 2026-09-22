---
Name: One Log Site, Many Terminals
Category: Architecture
Description: An incident fingerprint keys on the exception a log site reports, so it both SPLITS one log site across several tickets and FOLDS several unrelated roots into one. How to group a cluster of timeout tickets before fixing any of them, what the split and the fold each look like from the issue tracker, and the four terminals the mesh's write-wait produces.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M12 3v6"/><path d="M12 9 5 15"/><path d="M12 9l7 6"/><circle cx="12" cy="3" r="1.6"/><circle cx="5" cy="16.5" r="1.6"/><circle cx="19" cy="16.5" r="1.6"/><path d="M12 9v7.5"/><circle cx="12" cy="18" r="1.6"/></svg>
---

# One Log Site, Many Terminals

**A bug-filing fingerprint is computed from the message a log site reported, not from the code path
that reported it.** That single fact produces two opposite errors at once, and a cluster of
timeout tickets will contain both:

| | what it looks like in the tracker | what it costs |
|---|---|---|
| **the SPLIT** | two or more tickets, different fingerprints, **the same log site**, differing only in the INNER exception | the same line is fixed twice, or fixed once and the other ticket outlives the fix |
| **the FOLD** | one ticket whose samples carry **several different inner exceptions** | one occurrence count over several roots; a fix is judged against a counter it cannot move |

Neither is visible from a ticket's title. Both are visible from its samples.

## Group first, and group on the TERMINAL

The grouping key that works is not the logger category and not the exception type the outer frame
carries — those are what the fingerprint already used. It is the **innermost terminal**: the
sentence produced by the code that actually gave up.

The mesh's write-wait — `GetMeshNodeStream(path).Update(…)` waiting for the owning per-node hub to
deliver its first frame — produces **four** of them, and they have four different causes and four
different owners:

| terminal | what it means | whose defect |
|---|---|---|
| `BaseStateTimeoutException` — *"NO change item at all reached the mirror"* | nothing arrived inside the bound | unresolved: the owner's activation, its routing, or its initial read |
| `InvalidOperationException` — *"this hub's mirror ended without ever carrying the node's state"* | the mirror COMPLETED without state | mirror lifecycle — acquired and not held for the write |
| `MeshNodeStreamException` — *"Host is shutting down, cannot route to …"* | the write was issued during shutdown | **the caller**: a reconcile that keeps working while the host stops |
| `MeshNodeStreamException` — *"the owner returned no verdict for this update within …"* | the delivery reached a hub and no handler ran | delivery/routing for that owner |

The third is the one that matters most for grouping, because it is **not a mesh defect at all**. A
write refused at shutdown is the platform answering correctly; what is wrong is that the caller
issued it and then reported the refusal as a failure. A ticket whose samples are mostly that
terminal is a **classification** ticket, and reading its occurrence count as a mesh-reliability
number is reading a shutdown census.

## How to tell a SPLIT from two real tickets

Two signals, both on the ticket itself, neither of which requires reading code:

1. **The ancestor fingerprint.** A re-addressed incident records the identity it inherited. Two
   tickets naming the **same** ancestor are one log site the identity function later separated —
   that is a split, and one of them is a duplicate of the other.
2. **The log line prefix.** Two tickets whose samples share the same message template up to the
   `--->` are the same site whatever their fingerprints say.

When both hold, close the newer as a duplicate **naming the survivor**, and say which terminal the
closed one contributed — otherwise the fold loses the one piece of information the split had.

### When a "duplicate" must NOT be closed

A diagnosed root and an undiagnosed symptom family that share a log site are **not**
interchangeable. Folding the family into the root buries whichever of the two is wrong: if the root
is fixed and the family keeps firing, the fix reads as ineffective; if the family is closed on the
root's fix, its remaining causes vanish from the queue.

So the rule has a direction: **a split is closed toward the older, better-diagnosed ticket only
when the terminal is the same.** Same site, different terminal ⇒ keep both, and cross-reference
them by terminal rather than by title.

## How to tell a FOLD

Read every retained sample's inner exception, not the first one. A ticket folding several terminals
is a ticket whose count is a sum over different defects — so:

- a fix that closes one terminal **cannot** be verified against the count;
- the honest verification is per-terminal: the terminal you fixed must be absent from samples taken
  after the fix reached the replica, and the count may legitimately keep climbing on the others;
- and a fix that CHANGES the message changes the fingerprint, which stops the old ticket folding
  and re-files the residual under a new identity. That is desirable — it un-pools the terminals —
  but it also means **the old ticket's counter freezes for a reason that is not the fix working.**

A frozen `lastSeen` therefore has three causes and only one of them is good news: the fault stopped,
the fingerprint moved, or the ingestion that folds sightings is itself down. Say which one you
established.

## Related

- [The Recursive-Delete Drain](../RecursiveDeleteDrain) — the same reading problem inside ONE
  report: a field whose "not measured" rendered identically to its "none".
- [A Failure Report Answers Its Own Instruction](../AFailureReportAnswersItsOwnInstruction) — what
  a single report has to carry so it does not need this page.
- [Reading CI Signals](../ReadingCiSignals) — the same defect class in the build instruments.
- [Issue Taxonomy](../IssueTaxonomy) — severity, and what a release is allowed to carry.
