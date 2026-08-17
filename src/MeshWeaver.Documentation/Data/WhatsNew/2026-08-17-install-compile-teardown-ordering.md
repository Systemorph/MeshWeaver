---
Name: Package installs no longer race their own teardown
Category: Fix
Description: Installing a package no longer starts its NodeType compiles against a hub the install is about to recycle, so types stop landing on the compilation-error overlay.
Icon: Sparkle
Order: -20260817
---

# Package installs no longer race their own teardown

Installing a package used to start compiling every node type it ships and then, moments later,
recycle the package's own root — the very node those compiles read. Types that happened to be
reading at that instant were marked as failing to compile, so a freshly installed package could
show a handful of its types on the compilation-error overlay for no reason you could see, and a
re-install would "fix" it.

The install now sequences the two: the root is recycled first and answers again before the
remaining types are asked to compile. Nothing is retried and nothing waits longer — the work is
simply put in an order where it cannot collide.

Teardown problems are also easier to diagnose now: when a hub takes too long to shut down, the
error names the hub that is actually holding things up and the message occupying it, instead of
reporting only which shutdown phase was reached.
