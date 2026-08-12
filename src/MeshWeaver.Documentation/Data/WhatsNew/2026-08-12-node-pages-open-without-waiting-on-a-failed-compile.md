---
Name: Node pages open without waiting on a failed compile
Category: Fix
Description: Sample and documentation node types that referenced code from a neighbouring node now compile wherever they are served, instead of stalling and then showing a compile error.
Icon: Sparkle
Order: -20260812
---

# Node pages open without waiting on a failed compile

Some node types pull in code from a neighbouring node — an analysis page reusing the currency and
line-of-business definitions next to it, for example. Those references are written the way the
content is laid out, and they stopped being found when the same content was served from a different
place in the mesh. The reference was then left in the code as-is, so the page failed to compile and
reported errors pointing at the reference line rather than at anything a user had written.

Opening such a page was slow before it failed: every reference that could not be found waited on its
own lookup first. References are now matched against wherever the node is actually served, so the
pages compile and open normally. Three sample article pages that used a view helper removed earlier
this summer were fixed at the same time.
