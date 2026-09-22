---
Name: The local test loop for mesh code could not start, and every build stayed green
Category: Fix
Description: The command authors are told to run before pushing — the one that actually executes a package's in-mesh tests — stopped working in three content repositories at once, and nothing in any build reported it, because no build ever ran that command. It works again, it now lives in one place instead of three, and a build that changes what it depends on now fails.
Icon: Bug
Order: -20260919
---

# The local test loop for mesh code could not start, and every build stayed green

Code that lives inside the mesh — the C# behind a package's types and views — is not compiled by the
ordinary build. It is compiled by the portal, at runtime. Two separate tools stand in for the build a
normal project would get: one proves that each type's code **compiles**, and a second one actually
**runs that type's tests**, on a developer's machine, in seconds, without starting a portal.

Only the first of those runs in continuous integration. The second is a local loop, and every content
repository's contributor guide names it as the step to take before pushing. That matters more than it
sounds: a compile proves a type builds, never that it is right. In one large port, every real defect
found — a cash-flow array supplied yearly onto a monthly grid, a currency constant that never compared
equal to anything — compiled perfectly and returned silently wrong numbers.

**That loop stopped starting.** The two tools share their understanding of what the portal compiles,
on purpose: a test harness that compiles something different from the gate reports defects that do not
exist and hides the ones that do. When the shared half was improved — to concatenate a type's sources
into the single unit the portal really builds, instead of compiling them file by file — the helper the
harness called went away with the old model.

In three repositories, three different things happened, and **none of them was a red build**:

- in one, the harness died on its first step, before a single test ran — while its own self-check
  still printed *green*, because that check only ever examined an unrelated part of the script;
- in another, someone had patched around the missing helper with a local re-implementation, so it kept
  running, green, reproducing a model the compile gate no longer used;
- in the third it kept working only because that repository also kept an old copy of the *shared* tool,
  which still contained the removed piece.

Nothing was red because no build anywhere ran the harness. For a day, the step authors are told to take
before pushing could not be taken in two of those repositories — and work reached CI unverified.

## What changed

**The harness now lives in one place** and each content repository keeps a small launcher that fetches
it, at the same version it fetches the compile gate at. A version mismatch between the two is no
longer expressible.

**It no longer has its own idea of what the portal compiles.** It asks the compile gate to build the
unit, and compiles exactly that. The tempting fix — putting the removed helper back — was measured
against a real package and rejected: it makes the script *start*, and then refuses a type that both the
portal and the gate compile cleanly, because the old model could not carry a type alias from one file
to its sibling.

**And a change that breaks it is now red where the change is made.** The harness's own self-check runs
in the platform's build, next to the gate's, and asserts the thing that matters — that the harness
compiles what the gate compiles. Four deliberate sabotages of it were each confirmed to fail that
check, naming what broke. A second guard refuses a re-appearing local copy in any content repository,
wherever in the tree it is put.

One repository's report also changed shape, for the better: it used to compile a package's whole
folder as one program, which is not a program the portal ever builds. It now compiles each type on its
own, the way the portal does — the same suites and the same tests, run once per type that contains
them.
