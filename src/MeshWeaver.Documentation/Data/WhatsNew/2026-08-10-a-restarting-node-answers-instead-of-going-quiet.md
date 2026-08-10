---
Name: A restarting node answers instead of going quiet
Category: Fix
Description: Anything asked of a node while it restarted got no reply at all, so the caller waited a full minute for nothing.
Icon: Sparkle
Order: -20260810
---

# A restarting node answers instead of going quiet

Nodes restart routinely — after their type is rebuilt, after a package is installed, after a
recycle. Until now, anything you asked of a node during that short window simply vanished.
The request was accepted, the node went down, and no reply of any kind came back. Whatever
was waiting — a page opening, an install finishing, a save confirming — sat there for a full
minute before giving up with a timeout that named nothing useful.

The most visible casualty was installing a package that ships images or videos: its files are
published into the node right as that node restarts, so the publish waited out the whole
minute and then reported that the package's binaries were not being served. The package
looked installed, but its pictures never appeared.

A restarting node now always answers, immediately, and says which kind of answer it is: "I am
coming back, ask again" rather than silence. Replies its own work already produced are
delivered instead of being thrown away on the way out. And installing a package now waits for
the node's restart to finish before publishing into it, so the files land the first time.
