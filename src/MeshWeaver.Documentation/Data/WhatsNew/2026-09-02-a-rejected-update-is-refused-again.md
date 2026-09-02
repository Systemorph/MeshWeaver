---
Name: A rejected update is refused again
Category: Fix
Description: An update that a validation rule should have refused — a version going backwards, for instance — could briefly go through with no error anywhere, because the rule was shown the current content in an unreadable form and stepped aside. Rules now see the current content the same way they see the proposed one.
Icon: ShieldCheckmark
Order: -20260902
---

# A rejected update is refused again

Some node types carry rules about how they may change: a version may not go backwards, a name may
not be emptied, a reference may not point at nothing. Those rules compare what a node currently
holds with what an update proposes, and refuse when the two do not fit together.

For a short window on 2 September 2026 a rule could be shown the node's current content in a raw,
unreadable form while the proposed content arrived readable. A rule that compares the two found
nothing to compare, stepped aside, and the update landed — no error, no warning where a person
would see it. The rule was fine; what it was handed was not.

Updates now always present the current content to the rules in the same readable form as the
proposed content, so a refusal is a refusal again. Nothing changes for updates the rules accept.
