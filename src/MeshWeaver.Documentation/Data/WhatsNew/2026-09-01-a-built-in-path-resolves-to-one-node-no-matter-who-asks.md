---
Name: A built-in path resolves to one node, no matter who asks
Category: Fix
Description: Two parts of the platform could serve different content for the same built-in path, silently. They now share one rule — and a duplicate registration says so instead of being half-honoured.
Icon: Sparkle
Order: -20260901
---

# A built-in path resolves to one node, no matter who asks

Some content is built into the platform rather than stored: node-type declarations, the built-in
roles, the partition entries, the shipped documentation. A deployment can add its own, and more than
one contributor can end up offering something at the same address.

Two parts of the platform resolved that differently. Search and autocomplete picked one; opening the
address directly picked the other. In one running portal, the same address returned **different
content depending on how you got to it** — and nothing said so. No error, no warning, nothing in the
log. It also made "add my own version at the platform's address to override it" look like a
supported way to customise a deployment: the addition was accepted, so the obvious conclusion was
that it had worked, when in fact it had worked in one place and not the other.

Both now use one rule, in one place, so the two can no longer drift apart: the deployment's own
declaration wins, then contributors in the order they were registered. An address a deployment has
deliberately handed to the database stays handed over — no contributor can quietly take it back.

And a genuine clash is now **loud**. When two contributors offer *different* content at one address,
the portal logs a warning at start-up naming the address, which one won and which one was dropped,
and the refusal you get when something else tries to occupy that address says the same. Contributors
offering the *same* declaration twice — which several built-in types do on purpose — stay silent, so
the warning means something when you see it.

Overriding a built-in by adding a second entry at its address is not a supported customisation. Give
your own declaration its own address, or remove the one it was competing with.
