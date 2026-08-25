---
Name: A type that was already built no longer gets rebuilt on first open
Category: Fix
Description: Opening a page whose type had never been touched on this instance used to start a fresh compile even when the deployment already shipped that assembly — the prebuilt build is now looked for at that moment too, not only at startup and install.
Icon: TopSpeed
Order: -20260825
---

# A type that was already built no longer gets rebuilt on first open

Most page types are built once, centrally, and shipped with the deployment. An instance is
supposed to simply take that build rather than make its own — which is why a portal that adopts
its types starts in a few seconds instead of a minute.

Until now an instance could only take a shipped build at two moments: when it started up, and
when a package was installed or content was pushed. Any other way of reaching a type — someone
opening a page of it for the first time, asking for a release, a self-repair after a redeploy —
went straight to building it from source, without ever checking whether the finished assembly was
already sitting there. That did not break anything; it just meant the first person to open such a
page waited for a build that never needed to happen, and their page was the thing that waited.

That check now happens on every route into a build. The finished assembly is looked for first, and
only when there genuinely isn't one does the type get built from source. On a deployment that
requires prebuilt assemblies, the same change means a type whose build has since arrived is picked
up on first open, instead of refusing until the next restart.

Two things deliberately did not change. If nothing is found, the type builds exactly as it did
before — the search is time-boxed and never fails a build that would otherwise have succeeded. And
a build is only counted as taken when the type itself can actually serve it, so a partial or failed
attempt still falls back to building rather than leaving the page waiting on something that never
arrived.
