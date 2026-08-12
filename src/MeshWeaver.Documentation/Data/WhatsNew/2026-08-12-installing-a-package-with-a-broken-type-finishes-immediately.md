---
Name: Installing a package whose type will not build no longer takes a minute and a half
Category: Fix
Description: When a package ships a custom type that cannot build, the install now recognises that straight away and finishes in seconds, instead of waiting out a ninety-second budget for a rebuild that was already over.
Icon: Sparkle
Order: -20260812
---

# Installing a package whose type will not build no longer takes a minute and a half

A package can define its own custom type and then use it for its own top-level page. Installing
one of those means the platform has to rebuild the type before it can put the package's images and
videos in place — publishing them any earlier would attach the page to a version of the type that
does not exist yet, and the page would come up empty for as long as it lived.

So the install waits for that rebuild. And when the rebuild fails — the package ships code with a
mistake in it — the install is supposed to notice, skip the file publishing, and finish, leaving a
log line naming the type at fault. The files go out on the next install, once the code is fixed.

That is what happened, but only when the timing was lucky. The install checks twice, and the wait
recognised "the rebuild is over" only by having watched it happen. The second check usually starts
after the rebuild has already finished, so there was nothing left to watch: it sat for its full
ninety-second budget and only then gave the answer it could have given at once. An install of a
package with one compile error took **93 seconds**; the same install now takes **3**, and
re-installing it without changing anything takes a tenth of a second.

The wait now reads the platform's own record of the failure. When a type's build fails, the
platform already files that away and stops rebuilding it until its code changes — precisely so a
broken type cannot spin. The install consults that record instead of waiting to witness something
that has already happened. Nothing was retried or given more time; a wait for an event that could
no longer occur was replaced by the answer already on hand.

Healthy packages are untouched — they publish as promptly as before — and a rebuild that is
genuinely still running, or one this very install has just asked for, is still waited for in full.
