---
Name: An update is held when its packages do not fit together
Category: Fix
Description: Before your installation moves to a new platform build, it checks that every installed package has been prepared for that build. It was only checking that the prepared packages existed — not that they had all been prepared against the same pieces — so an update could go through and then leave a package's pages empty.
Icon: ShieldCheckmark
Order: -20260903
---

# An update is held when its packages do not fit together

Every package you install carries pages and views that are compiled ahead of time, for the exact
platform build your installation runs. Before your installation updates itself, it asks whether that
preparation exists for the new build — and refuses the update if a package would otherwise have to
be compiled from scratch at start-up, which is slow and can fail.

That check had a gap. It confirmed that each package **had been prepared**, but not that the
packages had been prepared **against the same pieces**. Some packages build on others — a social
feed on the collaboration tools, a map gallery on the map control — and when the piece a package was
prepared against is not the piece your installation actually receives, the preparation is unusable.
Your installation notices this only after the update, when it quietly discards the prepared package
and the pages that depend on it come up empty or slow. That is what happened to the **Posts** page
on one portal on 2 September.

**The check now looks at the whole set.** For the candidate build it reads what each prepared
package was built against and compares it with what is actually being shipped for that build. If any
package does not fit, the update is **held**, and the **Updates** settings tab names the package, the
piece it depends on, and the two versions that disagree — so the mismatch is fixed in the build
pipeline instead of being discovered on your pages.

The same morning, the map galleries (Apple Maps, Google Maps, OpenStreetMap and the Cornerstone
pricing map) were failing this fit test everywhere, because the map control was being shipped
twice — once inside the platform and once as its own package. The map control now ships only as a
package, and the build refuses to prepare a package against a piece that is shipped both ways.
