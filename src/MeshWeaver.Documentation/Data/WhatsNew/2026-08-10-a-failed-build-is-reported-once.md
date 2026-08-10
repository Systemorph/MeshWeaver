---
Name: A failed build is reported once, not twice
Category: Fix
Description: When something you built does not compile, the failure is now recorded a single time, with the diagnostics and the source list attached — instead of twice, the first time with neither.
Icon: Sparkle
Order: -20260810
---

# A failed build is reported once, not twice

When the C# behind one of your types does not compile, the platform records the
failure so an operator can see it. Until now it recorded the same failure twice:
once at the moment the compiler returned its errors, and again when the build
pipeline handled the result.

Only the second record was useful. It carried the compiler diagnostics, the type
that failed, and the list of source files the build actually found — the thing
you need to tell "my code has a typo" apart from "the build did not pick up my
source files". The first record had none of that context, and because it arrived
first, it was the one that got read. It made a plain compile error look like a
problem with writing the built assembly to disk, which sent more than one
investigation down the wrong path.

The build now reports a failure in exactly one place: the one that knows the
whole story. Nothing was made quieter — the same failure still surfaces on the
type itself, in its build log, in the notification you get, and on the error page
— and the duplicate that carried no detail is gone.
