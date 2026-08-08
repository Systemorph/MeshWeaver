---
Name: Shutting down no longer crashes when custom types are in use
Category: What's New
Description: A portal or test host that had compiled custom NodeTypes could abort instead of exiting cleanly; shutdown now waits for in-flight work before reclaiming those types.
Icon: Sparkle
---

# Shutting down no longer crashes when custom types are in use

When a mesh shut down, the memory for each dynamically compiled NodeType was reclaimed a moment too
early — while background rendering work that used those very types could still be running. Most of
the time nothing noticed, but under load the process could abort outright instead of exiting
cleanly, which showed up as unexplained crashes at the end of a test run or a pod restart.

The reclaim now happens at the true end of shutdown, after all in-flight work has been stopped and
joined. Nothing is held any longer than before during normal operation: a single node going away
while the mesh keeps running still releases its memory immediately.
