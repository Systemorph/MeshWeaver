---
Name: Removing someone from a group takes effect immediately
Category: Fix
Description: Group membership changes now reach permissions in a running portal — a revoked member loses access at once instead of keeping it until the next restart.
Icon: ShieldKeyhole
Order: -20260808
---

# Removing someone from a group takes effect immediately

Taking someone out of a group used to look like it worked and not actually work.
Search stopped listing them, the Effective Access view showed the grant as gone —
but the person could still open the records the group gave them access to, and they
kept that access until the portal was next restarted. Adding someone to a group had
the mirror-image problem: the new member saw nothing new until a restart.

Permissions resolve a person's groups from a search that deliberately spans every
partition, because a group and the grant that mentions it often live in different
places. That kind of search returned one snapshot and then stopped listening, so the
answer a running portal held was the answer from the moment it started. Membership
changes landed in the database and were simply never looked at again. Access that
should have been withdrawn stayed open — the direction that matters most.

Cross-partition searches are now live: they deliver the first snapshot and then keep
delivering additions, changes and removals as they happen. Joining a group opens the
group's records straight away, and leaving it closes them straight away, with no
restart and nothing to re-run.

The second half of the same problem was newer spaces. The list of partitions a
cross-partition search covers was refreshed on a timer, so a space created in the
last half-minute could be missing from it — and because a live search only takes
another look when something changes, a membership written into a space that young
was checked once against a list that did not include it and then never again. A new
space now joins that list the moment it is created, so it participates in live
results from its first second.

A third problem hid behind the same symptom, and it was the widest: a single partition
that carried no permission data of its own could silently empty every signed-in user's
cross-partition results. Searching across partitions asks each one "may this person read
this?", and a partition with nothing to answer from made the whole question unanswerable
— so the answer came back as "nothing found", for every partition at once, however
healthy the rest were. Such a partition is now left out of the question instead of
ending it, which changes nothing about what you are allowed to see: a partition with no
permission data never granted anyone access to begin with.

If you worked around this by restarting the portal after changing group membership,
or by granting people directly instead of through a group because group changes
"didn't take", neither workaround is needed any more.
