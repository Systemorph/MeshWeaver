---
Name: A page no longer loses its own views to a rebuild happening next door
Category: Fix
Description: A page could come up with only the generic menu — none of its own views — and stay that way, because it opened at the instant its code was being rebuilt.
Icon: ArrowSync
Order: -20260810
---

# A page no longer loses its own views to a rebuild happening next door

A page whose behaviour comes from code you wrote could open showing only the
generic node menu — none of the views that code defines — and reporting that the
view you asked for could not be found. Reloading did not help. The page stayed
that way until somebody restarted it by hand, even though the code it was missing
had built perfectly well seconds earlier.

## What was happening

A page works out which code serves it exactly once, when it first opens. That
lookup reads the compiled result of your code.

Rebuilds swap that compiled result out. If a page happened to open in the instant a
rebuild was swapping — and a rebuild can be triggered by something entirely
unrelated to that page — the lookup found itself reading a result that was being
retired underneath it. Instead of looking again at the current one, it gave up and
reported that your code defines no views at all.

That answer was then treated as the truth. The page bound itself to the generic
defaults, and because the lookup only ever happens once, it kept them for as long
as it stayed open.

## What changed

A lookup that finds itself reading a result being retired now simply looks again at
the current one, which is what it should have done all along. The rebuild next door
no longer costs a page its own views, and a page that opens during one comes up
complete.
