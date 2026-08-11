---
Name: Platform updates no longer stall on a source file that could not be read
Category: Fix
Description: When the platform cannot read a type's source files during an update, it now says so instead of reporting an invented error in your code — and the update keeps going.
Icon: Sparkle
Order: -20260811
---

# Platform updates no longer stall on a source file that could not be read

While a platform update is being prepared, every custom type is rebuilt. Until now, if the
platform could not read a type's source files at that moment — busy machines during a
changeover, a slow answer from another part of the mesh — it went ahead and built the type
from whatever it had managed to load. The result was an error message about *your* code, for
code that was perfectly fine: "the name X does not exist", naming a class that was sitting
right there in a file the platform simply had not read.

Those invented errors looked exactly like real ones, so the update safety-check treated them as
damage caused by the new version and refused to finish. Updates stalled on healthy content, and
the only way out was to switch the safety-check off — the very thing it exists to make
unnecessary.

Now the platform never builds a type from an incomplete set of source files. If the files
cannot be read, it reports exactly that — "the build state could not be determined", naming the
read that failed — the affected type shows a *retry* message instead of "please correct the
code", and the update continues. A genuine error in your code is unchanged: it is still
reported as an error, and it still holds the update back, which is what the safety-check is
for.
