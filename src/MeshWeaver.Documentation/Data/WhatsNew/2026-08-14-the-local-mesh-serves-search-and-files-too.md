---
Name: The local mesh serves search and files too
Category: Fix
Description: Search, the file browser and uploads reached endpoints the local mesh never published, so they failed with a confusing parsing error.
Icon: Sparkle
Order: -20260814
---

# The local mesh serves search and files too

The apps that run against the local mesh — the desktop apps and the packaged web app — ask it for
searches, for a folder's contents and for uploads over the same web endpoints a hosted portal
serves. The local mesh only published one of those. The rest quietly fell through to the app's own
start page, which answered successfully with a web page, so the app reported a parsing error rather
than a missing feature.

The local mesh now publishes the same set as a portal does, and a check on both sides fails the
build if one of them ever stops serving something the apps call.
