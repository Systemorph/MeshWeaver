---
Name: A held sync source no longer looks settled
Category: Fix
Description: When the publication seal holds a GitSynced Space back from a green build, the Space's sync record now says so — outcome Held, with the reason and the commit the instance is sealed at. Until now a held source was field-for-field identical to one that was simply up to date, and two tagged releases sat undelivered on both portals for nine hours behind that reading.
Icon: Checkmark
Order: -20260912
---

# A held sync source no longer looks settled

A Space that syncs a module-bearing repository does not import every green build of it. It advances
only to a commit the registry has **sealed for the instance you are looking at** — the rule that
stops a portal receiving sources its running modules cannot serve. That is deliberate, and it is
usually invisible because the seal and the build agree.

When they do not, the Space is **held**. Until now that held Space was indistinguishable from one
that was simply current: the sync record read `Imported`, the attempted commit equalled the synced
commit, and `lastAttemptWasFinal` — the field that is supposed to separate *settled* from *stuck* —
said `true`. All three were about the previous evening's delivery.

On 2026-09-12 that reading cost nine hours: `Hosting/v1.17.1`, `Hosting/v1.18.0` and
`DeepSign/v1.1.0` were tagged by green builds, held on both production portals, and the records said
nothing was outstanding.

Now a hold is written where the outcome is:

```json
"lastSyncOutcome": "Held",
"lastSyncNote": "built at 222853d4, not sealed for this instance (identity s6649734…: 'plugins' is sealed at 24c2d024)"
```

The note names the commit the instance **is** sealed at, which is what tells you whether to wait for
the next publication or to roll the instance. The attempt pair is cleared with it, so a hold can
never leave a "we already looked at these exact bytes" licence standing for the delivery that
follows. The next import that actually runs replaces both.

Nothing about when a Space imports has changed — only what its record says when it does not. Why a
seal can stop advancing altogether, how to read a Space's real state, and what is still missing for
"published" to imply "live" are written up under
[When a Publication Seal Stops Advancing](/Doc/Architecture/PublicationSealStarvation).
