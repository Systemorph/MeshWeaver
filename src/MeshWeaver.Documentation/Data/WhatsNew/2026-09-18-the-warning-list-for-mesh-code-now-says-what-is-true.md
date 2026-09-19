---
Name: The warning list for code stored in the mesh now says what is actually measured
Category: Fix
Description: The two lists recording which warnings the platform's own example content is allowed to produce still named 37 warnings that the build stopped reporting a day earlier. They tolerated nothing and hid nothing, but they read as outstanding debt and the build asked on every run for them to be removed. They are gone, and the lists now record the measurement that replaced them.
Icon: Warning
Order: -20260918
---

# The warning list for code stored in the mesh now says what is actually measured

Some C# lives inside the mesh rather than in the platform's own source tree — the code behind a
sample data model, a worked example in the documentation, a view compiled where it is stored. That
code is compiled when the platform runs it, so none of the usual compiler checks a change goes
through ever see it. A separate check does: it compiles all of it on the way in and holds it to a
list of the warnings it is currently allowed to produce. The list can only ever shrink, which is
what stops the debt quietly growing back.

**The day before, two whole families of warning stopped being reported at all** — not because they
were hidden, but because the platform's own ordinary build does not report them either, so the mesh
check had been holding code stored in the mesh to a stricter standard than the platform holds
itself to. One family is about missing documentation comments; the other is about the *version
numbers on referenced libraries* not lining up exactly, a property of which libraries are in the
room and never of anything an author wrote.

That left 37 lines in the two lists naming warnings nothing could produce any more. Nothing was
broken by them: they permitted nothing, they concealed nothing, and the checks they belonged to
were already judging against zero entries. But they read like outstanding debt to anybody opening
the file, and every single run printed a line asking for them to be deleted.

**They are deleted.** Both lists are now empty of entries — which is the *strictest* setting
available, not the weakest: with nothing listed, any warning the check still reports, from any of
this code, fails immediately. The files themselves stay, because "there is no known debt" and
"nobody checked" must never look the same, and the check still refuses to pass unless it confirms it
actually ran.

**What the lists say now is what was measured.** All 4 documentation examples and 27 of the 28
sample models compile, and the check reports no warnings at all across them. That is a statement
about what the check counts, not about the compiler: the two retired families are still produced
every time this code is compiled, and are dropped before anything counts them. Each file records
that, records that the 37 lines went because those warnings were retired rather than because
anybody fixed anything, and says plainly that the missing documentation comments those lines once
counted are still missing — so nobody reading an empty file later mistakes it for work that was
done.

One inaccuracy went with them. The note explaining *why* those version-mismatch warnings are safe
to leave unreported credited a default setting that, measured, does not apply to this repository at
all. The real reason is a different one — an ordinary build is handed a set of libraries that
already agree, while the mesh compiler assembles its own and is shown the seam. The explanation now
states the measurement instead of the assumption.
