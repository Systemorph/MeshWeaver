---
Name: A failed plugin check no longer reports counts it never took
Category: Fix
Description: When a plugin's automated check broke part-way through, the summary printed "0 items" — indistinguishable from a plugin that genuinely contained nothing, and it named the wrong step. It now says which step failed and admits when nothing was counted.
Icon: ClipboardTaskListLtr
Order: -20260813
---

# A failed plugin check no longer reports counts it never took

Every plugin goes through an automated check before it ships: its contents are installed into a
throw-away workspace, each of its types is built, its views are rendered, and the whole thing is
installed a second time to prove that repeating it changes nothing.

When any part of that broke, the summary said the same thing regardless: the plugin's name, the
label **install**, and *0 items, 0 types*. Both halves of that could be wrong. The failure might
have happened long after the install finished — while building a type, or rendering a view — and the
zeros were never a measurement at all, just the empty defaults of a result the check gave up on.

That combination is worse than no information, because it reads as a confident statement: this
plugin installed nothing. On at least one occasion a plugin that had installed perfectly well —
proven by the very next run, which installed all of it — was reported that way, and the failure was
put down to background noise instead of being investigated.

The summary now names the step that actually failed — fetching, installing, or the per-type
checks — and where no count was ever taken it says so plainly instead of printing zeros. A broken
check still fails the plugin, exactly as before. It simply no longer claims to have measured
something it did not.
