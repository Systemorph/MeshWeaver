---
Name: Downloading a plugin bundle mid-update returns the retry response too
Category: Fix
Description: The bundle and module downloads themselves — and the module set's own listing — now answer a publication that is being replaced with the retry response, closing the last places a build could still receive an unexpected server error.
Icon: ArrowSyncCheckmark
Order: -20260910
---

# Downloading a plugin bundle mid-update returns the retry response too

A publication update briefly removes the files it is replacing, and a build reading through that
moment must be told to come back — not handed an unexpected server error. That was fixed for the
publication's completion marker, but two places on the same routes still read a file without
allowing for its removal, and each produced the same unexpected error from a slightly different
step. Both are now closed.

**The download itself.** The registry checked that a bundle was listed and present, then opened it
again to send the bytes — and by then the publication may have been replaced or removed. The file
is now opened while the request is still being decided, and the already-open file is what the
response sends, so a download that started before an update is delivered whole from the version it
began with rather than failing part-way.

**The module set's own listing.** The list of module packages a publication shipped was read the
same way the completion marker used to be, with the same outcome. It now distinguishes the two
cases that need opposite answers: a publication that never shipped a module set still reports that
permanently, so the fix is to republish it, while one whose listing vanished because the
publication is being replaced returns the retry response like everything else.

A related detail: a download is now served under the name the publication itself sealed. Names were
matched ignoring case, but the storage does not ignore case, so asking for `store.zip` when the
publication sealed `Store.zip` used to fail on the server. That is a permanent mistake by the
caller, and it now reads as one.
