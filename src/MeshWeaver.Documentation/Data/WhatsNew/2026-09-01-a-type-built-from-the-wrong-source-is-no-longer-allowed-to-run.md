---
Name: A type built from the wrong source is no longer allowed to run
Category: Fix
Description: When the platform can prove a type's installed build was made from different source than this portal holds, it now refuses to start anything with it — and says so on the page, with the button that fixes it.
Icon: ShieldError
Order: -20260901
---

# A type built from the wrong source is no longer allowed to run

Installing a package brings a **prebuilt** version of its types with it, which is what makes installs
and restarts fast. Recently the platform learned to check those prebuilt versions: the package now
records which source it was built from, and the portal compares that against the source it actually
holds. When the two disagree, the build is marked as rejected.

Marking it was only half the job. A rejected build could still be *started* — its pages activated,
its background rules armed, its handlers registered — and code that was built from older source
running against current data is exactly how a customer lost four documents' contents earlier this
year. Stale code sitting unused harms nobody; stale code that is running is the incident.

**So now it is not started.** When a type's build has been *proven* to come from different source,
the platform declines to bring up anything that runs it. Pages of that type show a short explanation
instead — that a build exists, that the platform refused to run it, and that **this is not a mistake
in your code** — and any request sent to it comes back with a clear "refused", never a silent hang
or a misleading "still compiling". The **Recompile** button on that page is the fix: it rebuilds the
type from the source this portal actually has, and every affected page comes back on its own once it
succeeds. Where a deployment only accepts prebuilt packages and cannot rebuild locally, the message
says that too, and names what has to happen instead.

Two things deliberately did **not** change, because getting them wrong would be worse than the
problem:

- **Packages published before this check existed still work exactly as before.** They carry no
  record of their source, so nothing can be proven about them either way — and "we don't know" is
  not the same as "we know it's wrong". Only a build that *states* its source and disagrees is
  refused.
- **The type's own pages keep working** — its overview, its data model, its build diagnostics. Those
  are what you need in order to understand and fix the problem, so they stay available.
