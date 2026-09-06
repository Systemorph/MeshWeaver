---
Name: A catalog over a package.json checkout lists its packages again
Category: Fix
Description: A plugin catalog node pointed at a local checkout of package.json packages, with no format declared, rendered an empty page since the format unification; the format is now read off the checkout's layout, and a declared format still wins.
Icon: Wrench
Order: -20260906
---

# A catalog over a package.json checkout lists its packages again

A plugin catalog node that points at a local checkout of `package.json` packages, and declares no
format, showed an empty catalog page after the recent change that made every reader of a catalog
node agree on one format. The default those readers agreed on was the node-repo format, and nothing
had ever needed to declare the manifest format before, so those catalogs went dark.

The catalog now reads the format off the checkout itself when none is declared: a folder of
`package.json` packages with no node-repo roots is treated as a manifest catalog, and everything
else keeps the shipped default. A format you declare on the node still wins.
