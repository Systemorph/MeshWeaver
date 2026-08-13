---
Name: An update that could not check itself no longer reports success
Category: Fix
Description: Before a platform update takes over, the new instance rebuilds the code behind your content types and refuses to go live if anything broke. If that check could not run at all, it used to count as a pass. It now counts as unanswered, and the update waits.
Icon: ShieldCheckmark
Order: -20260813
---

# An update that could not check itself no longer reports success

Every platform update recompiles the code behind your content types against the
new version. Because that can break something, the new instance builds
everything **before** it takes over: if a type that used to work no longer
compiles, it declines to go live, the update stops, and the version you were
already running keeps serving. You see a paused update instead of broken pages.

That check had one gap. It reported a pass in two quite different situations:

- **It looked, and found nothing wrong.** The genuine all-clear.
- **It could not look at all.** If the step that lists your content types failed
  — it could not reach the store, or the request never came back — the failure
  was quietly discarded and the result came out as "nothing to report", which is
  indistinguishable from a clean pass.

So an instance that had verified *nothing* could report itself ready and take
over. Nothing was broken by this on its own, but it was the one way an update
could get past the very safeguard meant to catch it.

Now the two are told apart. A check that **could not run** is recorded as
unanswered rather than passed: the new instance does not take over, the update
waits, and the version you are on carries on serving. The status message says so
in plain terms instead of showing a green tick nobody earned. If the cause was
temporary, the next attempt simply runs the check properly and the update
continues on its own.

Finding **nothing** is still a perfectly good answer. An instance with no
content types of its own verifies successfully and goes live as normal — the
change is only about the difference between *"nothing is wrong"* and *"I could
not find out"*.
