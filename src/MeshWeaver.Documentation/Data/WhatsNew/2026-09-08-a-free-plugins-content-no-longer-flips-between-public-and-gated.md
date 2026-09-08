---
Name: A free plugin's content no longer flips between public and gated
Category: Fix
Description: Two parts of the platform disagreed about whether a free plugin's content is readable by everyone, and each undid the other on every install and every restart. One of them now owns the answer, so the setting stays where it is put.
Icon: Checkmark
Order: -20260908
---

# A free plugin's content no longer flips between public and gated

A plugin that carries no price had **two parts of the platform writing its access setting, and they
disagreed**. One published the whole partition as readable by everyone. The other applied the store
rule — the cover page is public, the content opens when you have access to it — and closed it again.
Neither gave way, so each install and each restart set the plugin one way and, seconds later, the
other way put it back.

Nobody chose that behaviour. It was simply whichever ran last.

## What changes

**The store rule now owns the answer for a plugin you install.** Cover page public, content reached
through your entitlement — one component decides it, and the setting stays put.

**Platform content that ships with your installation is unaffected.** The catalog, the built-in
apps, the documentation and the other pre-installed partitions are public exactly as before, and the
repair that restores them if they were ever left unreadable still runs.

**A brief window where a free plugin's content was readable by anyone is gone.** After every restart
the partition was reopened and then re-closed a few seconds later. In between, content that the
store rule intends to be gated could be read by anyone, signed in or not. That window no longer
opens.

**A large amount of pointless write traffic stops.** On one installation this loop had rewritten a
single plugin's access setting more than 200,000 times. Those writes did nothing except undo each
other, and they were slow enough to make installing that plugin time out.

## What this does not change

**Nothing you have deliberately set is touched.** A plugin that ships its own access policy keeps
it, as before. Access you have granted to a person or a group is untouched — this only ever
concerned the "everyone" and "not signed in" entries the store rule manages for itself.

**Nothing becomes newly unreadable to someone who could already read it.** People with access to a
plugin's content keep it. What changes is that the intermittent, unintended public window closes.
