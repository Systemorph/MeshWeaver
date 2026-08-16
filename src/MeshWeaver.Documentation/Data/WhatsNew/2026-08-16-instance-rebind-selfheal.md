---
Name: Pages refresh themselves after plugin updates
Category: Fix
Description: Open pages no longer keep showing an outdated view after a plugin or platform update.
Icon: Sparkle
Order: -20260816
---

# Pages refresh themselves after plugin updates

When a plugin or the platform updated, an already-open page could keep showing its old state — in the worst case losing its buttons and panels — until someone recycled it by hand. The self-healing watcher that should have caught the update was blind to one common message shape; it now sees every update, so affected pages rebind to the new version automatically.
