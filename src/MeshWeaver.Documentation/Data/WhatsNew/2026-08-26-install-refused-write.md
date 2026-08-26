---
Name: Plugin installs no longer silently drop a node
Category: Fix
Description: A package install now re-applies any node the store refused, instead of reporting it as installed while the old content stayed in place.
Icon: Sparkle
Order: -20260826
---

# Plugin installs no longer silently drop a node

Installing a plugin writes its nodes in batches, and each batch is classified as "new" from a
snapshot taken at the start of the install. If something else wrote one of those paths in the
meantime, the storage layer correctly refused the batch's write for that node — but the install
counted it as written anyway, so the partition kept the older content while the install reported
success. Nothing surfaced it: the node only got its real content on the *next* install of the same
package.

The installer now notices the refusal and re-applies the node the same way it applies any node that
already exists — honouring a node you have claimed as your own, skipping one that is already
identical, and otherwise landing the package's content. So a plugin's content is what the package
says after one install, and re-installing an unchanged package really does nothing.
