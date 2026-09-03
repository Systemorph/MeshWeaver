---
Name: The fit check now looks inside every package
Category: Fix
Description: The check that holds an update when your installed packages do not fit together was only comparing the main piece each package ships. Packages also carry copies of pieces that belong to other packages, and those copies were never compared — so a mismatch hiding in one of them could still let an update through.
Icon: ShieldCheckmark
Order: -20260903
---

# The fit check now looks inside every package

Before your installation moves to a new platform build, it checks that every installed package has
been prepared for that build **and that the packages fit together** — that they were all prepared
against the same pieces. If they were not, the update is held rather than applied, because a package
prepared against the wrong piece is quietly discarded at start-up and its pages come up empty.

That check was only comparing the **main piece** each package ships. It turns out most packages
carry more than one. A package that builds on a small shared piece — the collaboration tools, the AI
core, the map control — carries a **copy** of it, so the package works even when it is installed on
its own. On the current package set, 19 of 37 packages carry a copy of a piece that some other
package owns.

Those copies were never compared with each other, so two of them could disagree and nothing would
notice. The result would be the failure the fit check exists to prevent, arriving anyway: your
installation keeps whichever copy it loads first, and every page prepared against the other one is
discarded.

**The check now reads every copy inside every prepared package**, not only the main piece. If one
piece appears twice as two different builds, the update is **held** and the **Updates** settings tab
names both packages, the piece, and the two versions that disagree.

Carrying the copy is deliberate and has not changed — removing it would break installing those
packages on their own. What changed is that "the copies agree" is now something your installation
confirms before it updates, instead of something the build pipeline happened to get right.
