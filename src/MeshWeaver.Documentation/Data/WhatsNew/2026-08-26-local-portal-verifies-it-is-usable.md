---
Name: The local portal says when it came up unusable
Category: Fix
Description: Bringing up a local portal reported success even when it had no sign-in page and no view packs — every control rendering as debug text, with no way in and no way to repair it from its own UI. The bring-up now checks all three and refuses to report success without them.
Icon: ShieldCheckmark
Order: -20260826
---

# The local portal says when it came up unusable

`memex-local up && memex-local update` printed two green lines and handed back a portal that could
not be signed into and could not be read: `/login` served nothing, and every control on every page
rendered as its own `ToString()` — `NamedAreaControl { Id = , Style = , … }` where the UI should be.

The verification could not have said otherwise. It fetched the home page and printed
*"Portal reachable"* whenever **curl itself** exited 0 — so a 503, a 404, and a portal rendering
debug text all produced the same line. `update` was worse: it printed *"update complete"* straight
after the rollout, having verified nothing at all.

It now asserts the three things a local portal is unusable without, and each failure names its own
remedy:

- **The portal serves** — an HTTP status in the serving range, judged on the code rather than on
  whether the request completed.
- **The sign-in route is routed.** The login pages compile *into* the portal image, so a 404 here
  means the image predates the GUI extraction and the answer is to rebuild it — never to install
  something.
- **The view packs are present.** The default control views and the mesh-node views left the image
  and now arrive as installable packages. Without them nothing has a view, so every control falls
  back to its `ToString()` — and the documented repair, the Plugin Catalog, is itself one of the
  missing views. A portal in that state cannot repair itself through its own UI, which is exactly
  why this has to be asked from outside it.

The last check waits rather than samples, because the packages install at boot and compile as they
go; and where they are on disk but not yet in the running process it performs the one restart that
activates them, instead of leaving it as a manual step. `memex-local verify` asks the same question
of a running install at any time.

Two further faults surfaced while proving the new check could fail. `memex-local help` was an
unbounded fork bomb: a backtick in the usage text re-entered the script's own entry point. And the
deploy aborted on macOS's bash for the exact paths the tool itself calls normal — an empty option
list is an error there, and both option lists are empty on a plain pull-from-registry run.

None of the three had ever gone red, because nothing in CI had ever opened this script. It is now
parsed, linted and behaviour-tested on every build, including static assertions for the two traps a
Linux runner cannot reach by executing anything.
