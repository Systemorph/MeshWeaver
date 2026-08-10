---
Name: A plugin's files arrive with it
Category: Fix
Description: Installing a course or plugin that ships videos, images or other files could hang for a minute and then quietly leave those files behind. They now travel with it.
Icon: CloudArrowUp
Order: -20260810
---

# A plugin's files arrive with it

A course does not consist only of pages. It also carries the videos, posters and
images those pages point at, and installing it is supposed to bring all of it.

For most plugins it did. For one particular shape it did not: a plugin whose
front page is built by code the plugin itself ships — a shop front, a catalog, a
course landing view. Installing one of those could sit for a full minute doing
nothing visible, finish reporting success, and leave every one of its files
behind. The pages were there. The videos 404'd. The only sign anything had gone
wrong was a line in the log.

The two halves of the plugin were fighting each other. A plugin's code is
rebuilt on your portal when it arrives, and while that rebuild is running the
plugin's front page has no code to serve it yet. Publishing the files happens
through that same front page — it is where the portal keeps a node's files — so
the publish arrived while the page was still waiting for the rebuild. It waited
with it. And if the rebuild finished without producing anything usable, because
the plugin's code no longer compiles against this version of the platform, the
page waited for a result that was never coming, forever. The file publish
eventually gave up on its own clock, a minute later, and dropped what it was
carrying.

Two things changed. The publish now waits for the plugin's code to be ready
before it goes through the front page, so on a healthy install it simply happens
in the right order. And a page that is waiting on code which turns out never to
arrive now stops waiting and says so, instead of hanging silently — so it opens
with an explanation of what is wrong with the plugin, and the files are
published either way.

If you have ever installed a course and found its videos missing, that is the
bug. Re-installing it now publishes them.
