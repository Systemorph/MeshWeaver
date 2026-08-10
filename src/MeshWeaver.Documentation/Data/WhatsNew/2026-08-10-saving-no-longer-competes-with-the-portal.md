---
Name: Saving no longer competes with the rest of the portal
Category: Fix
Description: A burst of new pages could make everything else on screen stall; creating, moving and deleting content now runs out of the way of the traffic that keeps views live.
Icon: Sparkle
Order: -20260810
---

# Saving no longer competes with the rest of the portal

Installing a course, importing a folder or letting an agent write a batch of pages creates a lot of
content at once. While that was happening the portal could go strangely quiet: a page you had open
stopped refreshing, a listing took seconds to appear, and occasionally an action gave up entirely
and reported a timeout — even though nothing had actually gone wrong with the content being saved.

Everything in the portal reaches you through one dispatcher, whose only job is to pass messages
between the parts of the system. Creating, moving and deleting content was being carried out by that
same dispatcher rather than merely passed through it. Because it does one thing at a time, a burst of
saves left every other message — including the ones that keep your open views up to date — waiting in
line behind them.

That work now runs somewhere of its own. The dispatcher stays free to do what it is for, so saving a
hundred pages no longer slows down the page you are reading while it happens.
