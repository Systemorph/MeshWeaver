---
Name: A page now tells you when it is running old code
Category: Fix
Description: If a page is executing a different build from the one its type reports, it now says so — instead of showing you old content under a green "compiled" status.
Icon: Sparkle
Order: -20260829
---

# A page now tells you when it is running old code

A page could show you the previous version of itself while every status in the portal said the
newest one had been compiled successfully. Recycling the page, recycling its type, and pressing
Compile all reported success and changed nothing, so a fix that had been published and verified was
indistinguishable from one that had not.

The reason nothing noticed is that the portal was comparing the wrong thing. It checked *where* a
build was stored, and the storage location does not change when a type is rebuilt without its
version moving — so the location matched perfectly while the code behind it differed. Every warning
built on that comparison was silent by construction, including the "a newer build is available"
banner, and every recycle re-loaded the same copy it already had.

Pages now compare the **identity of the code they are actually executing** against the identity the
type records for its latest build. When they differ:

* the page carries a banner saying it is not running the published build — and deliberately does
  **not** offer a recycle link, because recycling does not fix this state;
* the portal logs it as an error, naming both builds;
* `get_diagnostics` names the build its `Ok` is about, and no longer claims the assembly "is
  loaded" — it reports what it knows, which is that the build succeeded.

This makes the situation visible and loud. It does not yet get the page onto the new code by itself;
if you see the banner, report it.
