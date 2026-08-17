---
Name: Updates now wait for the packages they need
Category: Feature
Description: A platform update no longer applies just because the build is newer — it applies once every package this deployment runs is actually available for it, and says so plainly when it is waiting.
Icon: PauseCircle
Order: -20260817
---

# Updates now wait for the packages they need

An update used to apply on one condition: the build is newer. That misses the condition that
actually matters — whether the packages this deployment runs can come along. When they cannot, the
update still went ahead and the deployment spent its first minutes rebuilding content it should
have received ready-made, with anything that failed to rebuild taking its part of the workspace
offline.

Now the check runs before anything is rolled, on all three routes an update can take: the automatic
check, the delivery pipeline's own assertion after publishing a release, and the manual **Apply
update now** button. If a package this deployment runs is not available for the target build, the
update is **held** — the deployment stays exactly where it is.

A hold is a state you can see, not a silence:

- the About tab reads **⏸️ Update held** rather than *update available*, because an install that has
  refused a build should not look like one that is about to take it;
- the Updates tab names the package that blocks it, and when the hold was recorded;
- "we could not check at all" is reported as its own thing, separately from "this package cannot
  come along" — they are different problems with different fixes.

**Nothing needs unsticking.** The check is re-run on every cycle, so publishing the missing piece is
the entire remedy: the next check clears the hold and the update applies on its own. That is what
makes holding safe — a deployment that quietly stopped updating for weeks would be a worse outcome
than the update it avoided.
