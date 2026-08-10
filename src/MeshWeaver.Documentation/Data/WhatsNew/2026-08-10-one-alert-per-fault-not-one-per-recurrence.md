---
Name: One alert per fault, not one per recurrence
Category: Fix
Description: A recurring error opened a fresh ticket every few minutes. Recurrences now land on the ticket that already exists — reopening it if it had been closed.
Icon: BellAlert
Order: -20260810
---

# One alert per fault, not one per recurrence

Automatic error ticketing had one job beyond noticing a problem: tell you about it
**once**. It got the noticing right and the telling wrong. In its first live run it
opened 37 tickets for about a dozen distinct faults. One error — a routing failure
that fired 602 times in a quarter of an hour — was reported eight separate times in
seven minutes. Every one of those was a notification in your inbox, and every one of
them was about something you already knew.

The count itself was never confused. All 602 sightings stayed a single incident, on a
single record, with a single running total. What went wrong was the step after that.
When the fault fired again, the incident was handed back for a fresh look, a fresh
write-up came out of it, and the write-up was filed — without anyone checking whether
this fault already had a ticket open. Nothing on the incident pointed back at the
ticket it had produced, so nothing could tell the difference between "report this" and
"report this again".

A second, smaller version of the same gap produced pairs: two tickets for one fault,
created in the same second. Two views of the same incident both read "ready to file"
before either had a chance to record that it was filing.

Both are closed now, by the same change. An incident **claims** the job before it does
it — the record is marked first, so a second attempt finds the work already taken and
stands down. And the incident now remembers its ticket. A fault that comes back is
folded into the ticket it already has, as a short update saying how many times it has
fired since the last one, at most once every six hours no matter how continuously the
error is firing.

Recurrence after a ticket was **closed** is treated as the news it is: the ticket is
reopened and the update posted, rather than silently piling up under something marked
done. A defect that returns after you fixed it is exactly the thing worth telling you
about.

Nothing is quieter than it should be. The first sighting of a new fault still opens a
ticket immediately, still with the full evidence — the exact log lines, the counts, the
pods it happened on. What changed is that the second, third and eighth sighting no
longer look like new problems.
