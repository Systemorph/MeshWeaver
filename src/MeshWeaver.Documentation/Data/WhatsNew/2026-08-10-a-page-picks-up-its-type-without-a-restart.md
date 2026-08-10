---
Name: A page picks up its type without a restart
Category: Fix
Description: When a node's type arrives or changes — a plugin finishing its install, an import landing, someone switching a node's type — the page now rebuilds itself on the new type instead of serving the old one until the portal is restarted.
Icon: Sparkle
Order: -20260810
---

# A page picks up its type without a restart

Every node in the portal is backed by its own small server that decides, once, which pages and
views the node offers. It makes that decision from the node's type at the moment it starts — and
then it never looked again. If the node's type arrived or changed afterwards, the server kept
handing out the pages it had chosen at the start, for as long as it stayed up.

Most of the time that is invisible, because the type is set before anyone opens the node. It became
visible when a plugin installed. Installing a plugin creates the space for it a moment before it
writes the plugin's own root, so anything that touched the new space inside that gap — a background
sync, a nav refresh, someone clicking the link early — started the server while the root still had
no type. When the install then finished and gave the root its real type, the server did not notice:
the plugin's own pages were simply missing, and the node showed only the generic pages every node
has. Opening it again did not help, because "again" reached the same server. Only a portal restart
cleared it.

Now a node's server watches for its own type changing and rebuilds itself when it does. The next
visit gets the right pages. This is a general fix rather than an install-specific one — the same
thing happens when an import lands a typed row, when a repair changes a node's type, or when
someone switches a node's type by hand — and it needs no action from anyone: the page that was
showing the wrong thing simply starts showing the right thing.
