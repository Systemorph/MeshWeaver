---
Name: An install now says whether it used the assemblies that were already built for it
Category: Fix
Description: Installing a package could quietly ignore prebuilt assemblies sitting right beside it and compile everything from scratch instead — and nothing in the log distinguished that from a portal that simply had none.
Icon: BoxCheckmark
Order: -20260906
---

# An install now says whether it used the assemblies that were already built for it

When you install a package, its code types usually do not have to be compiled: the build that
published the package can ship the finished assemblies alongside it, and the portal adopts those
instead of running the compiler. A package installed **after** the portal started — which is what
happens every time you install from a catalog — depends entirely on that adoption happening during
the install itself. Nothing later goes back to look.

It could fail to happen, and when it did, nothing said so.

The measured case: a portal had forty sets of prebuilt assemblies mounted, one of them covering
exactly the package being installed. One package installed and adopted seventeen assemblies. Three
minutes later a second package installed ninety-nine nodes, adopted nothing, and compiled ten types
whose finished bytes were on disk a directory away. The log showed no adoption, no refusal and no
reason — the same thing it shows on a portal that ships no prebuilt assemblies at all. Roughly
fifteen minutes went into compiles that did not need to happen, and the only way to notice was to
compare two logs and spot a line that was missing from one.

**Every install now reports what adoption did, including when the answer is "nothing".** The line
names the package, how many types it covered, and — when it covered none — which of these actually
happened:

- this portal has no prebuilt assemblies mounted at all;
- they are mounted, but nothing was published for the version this portal is running;
- they are mounted and readable, and none of them covers the types this package installs;
- they *do* cover these types and the assemblies were still refused — which is a real fault, and is
  reported as a warning rather than a note.

Those four used to be one silence. They have different fixes — a missing volume, a missing build, a
build that covered the wrong package, and a genuine mismatch — so telling them apart is the whole
point.

## Also fixed

A portal could end up unable to adopt anything at all because the feature was bundled together with
an unrelated start-up option, and a portal that did not want the second one silently got neither.
The two are now separate, and a portal that has no adoption configured says so during the install
instead of quietly compiling.
