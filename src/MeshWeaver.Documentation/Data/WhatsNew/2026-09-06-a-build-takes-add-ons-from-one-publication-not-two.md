---
Name: A build takes add-ons from one publication, not two
Category: Fix
Description: A build that downloaded its add-ons while the server was replacing them could end up with pieces from two different versions. It now notices, takes the version that applies, and says so.
Icon: ArrowSync
Order: -20260906
---

# A build takes add-ons from one publication, not two

When a build assembles the add-ons it needs, it asks the server for the list and then downloads each
one. Publishing a new set replaces those files, and for about a minute and a half the set being
replaced is deliberately unavailable — that is what stops anyone reading half of the old set and
half of the new one.

The list and the downloads are separate requests, though, so a build could read the list from the
version that was there a moment ago and then download files from the version that replaced it. The
result was a build carrying pieces of two different sets. Nothing said so: it finished normally, and
the mismatch only surfaced later, as add-ons a running server quietly refused to load.

A build now says which version its list came from, and the server refuses to hand it anything from a
different one. When that happens the build simply reads the version that now applies and starts its
downloads again, discarding what it had — so it always ends up with one complete set. If new
versions keep arriving faster than a build can read one, it stops and says exactly that, naming
itself rather than leaving the repository being built to look like the cause.

The same reading applies to the other side of it: asking for a set while it is being replaced now
answers *temporarily unavailable* instead of *no such file*. Those meant opposite things and looked
identical, which is what sent an earlier investigation looking for a file that had never gone
missing.
