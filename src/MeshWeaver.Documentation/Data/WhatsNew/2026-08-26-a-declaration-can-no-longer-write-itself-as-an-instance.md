---
Name: A node type declaration can no longer write itself as an instance
Category: Fix
Description: Creating or updating a node type declaration that also claims to be one of its own instances is now refused at write time, closing the class of bug behind the user-directory type-confusion incidents for good.
Icon: PersonQuestionMark
Order: -20260826
---

# A node type declaration can no longer write itself as an instance

A recent fix retyped the three built-in declarations that had been filed under the type they
describe — `User`, `Virtual User` and `Partition` — so they stopped showing up in their own
`nodeType:` queries. That cleaned up the three known cases, but nothing stopped the same mistake
from being written again later: a repair, a package install, or a hand-authored edit could still
create a new declaration that files itself under its own type, and the portal's user directory would
go right back to handing out a description-of-a-person where it expected an actual person.

Creating or updating a node now checks that rule directly: a node whose content *describes* a type
can no longer also file itself under **that same type**. The write is refused with a message naming
the node and the type it claims, rather than surfacing later as a silent lookup failure. This closes
the underlying class of bug rather than the three instances of it that were already fixed.

The rule is deliberately narrow. A node that describes a type while being filed under some
*unrelated* type is still allowed — a package root that is a space, and whose content happens to
also describe a type, is a real and shipping shape. Refusing that would have made those packages
impossible to install, which is a worse outcome than the mismatch it would have prevented.
