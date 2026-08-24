---
Name: Pages load lighter
Category: Fix
Description: The portal now serves its compressed, cacheable copies of the framework and component assets instead of the full-size originals.
Icon: Sparkle
Order: -20260824
---

# Pages load lighter

The portal already built compressed copies of its scripts and stylesheets, but was serving the
uncompressed originals — and telling your browser nothing about caching them, so every visit
re-downloaded the lot. It now serves the compressed copies, with caching instructions.

The single largest file drops from about 200 KB to about 56 KB, and versioned assets are held in
your browser's cache rather than fetched again. Nothing about the pages themselves changed; there
is just far less to fetch before they appear.
