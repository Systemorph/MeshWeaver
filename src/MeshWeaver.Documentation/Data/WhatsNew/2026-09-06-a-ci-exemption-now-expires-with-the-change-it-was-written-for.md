---
Name: A CI exemption now expires with the change it was written for
Category: Fix
Description: A one-off entry in a binary-compatibility allow list used to survive its own merge and turn red on every other pull request in the fleet. It is now scoped to the diff that adds it and expires the moment that lands.
Icon: ClockDismiss
Order: -20260906
---

Two of the gates that refuse a binary-breaking change — the one guarding a public record's
constructor, and the one guarding a public type moving between assemblies — take an allow list for
the rare case where the break is deliberate and the whole fleet is being moved at once. Every entry
in one is written for exactly **one** merge. Both files asked the author to delete the line
afterwards, and one entry even said so in its own text.

Nobody deleted it. From the minute that change merged, every pull request in the fleet whose diff
touched any C# file reported *"1 stale allow entr(ies)"* — three were ejected from the merge queue
over it — while the branch that actually carried the line read green, because its own run of the
gate had been skipped as already-proven. The instruction was addressed to a future reader, and the
pull request that adds such an entry is by construction the one whose merge makes it stale: the
author is asked to remember something at the moment they stop looking.

The deletion is no longer anyone's job. An entry is now in force **only in the diff that adds it**,
so merging is the moment it stops applying — there is no line left for anyone to forget. An entry
already on the main branch permits nothing, which is the protection the old rule was really after
(it can no longer hide the *next* break on the same type), and fails nothing, which is the red that
should never have reached anybody else.

An entry also now names the pull request it travels with, and the gate resolves it: open, and the
exemption stands; already merged, closed without merging, an issue rather than a pull request,
missing, or unreadable, and the gate goes red naming which. There is deliberately no outcome where
being unable to check reads as permission.
