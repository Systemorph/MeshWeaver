---
Name: Two more ways to break someone else's build are now caught
Category: Fix
Description: Adding to an interface can stop other people's code compiling even though nothing was removed. Two of those ways were invisible to the platform's checks; a change that uses either now has to say who it affects.
Icon: ShieldCheckmark
Order: -20260907
---

# Two more ways to break someone else's build are now caught

Taking something away is the obvious way to break code that depends on you, and the platform has
stopped that for a while. Adding is where it gets counter-intuitive: if you *use* an interface,
nothing added to it can hurt you. If you **implement** one, everything added to it is work you now
have to do, and your build stops until you do it.

That case was already caught for the ordinary shape — a new method on an interface. Two other
shapes said exactly the same thing to an implementer and were invisible to the checks.

## What was missed

**An interface inherits from another one.** Writing `interface IStore : IDisposable` adds no member
to `IStore` — and that is precisely why it slipped through: the check compares the *list of members*
before and after, and that list did not change. Every implementer of `IStore` now has to supply
`Dispose` all the same.

**A `protected abstract` member on a public abstract class.** The index the checks read holds public
members, by design. A protected one is not a smaller entry in it; it is not in it at all. Anyone
subclassing that class from another repository stops compiling with the same error a public
`abstract` member would have produced.

Both were measured against the existing detector before anything was built, with a working example
in the same run — so each was a genuine gap rather than a broken test.

## What changes for you

Nothing, unless you make one of those two changes. If you do, the check now asks the same question it
already asks for a new interface method: **who implements this, and what happens to them?** One
sentence in the change's description, in the same form as before. A change that gives the member a
default implementation keeps every implementer compiling and the question does not arise — that is
the real fix, and the check is happy to be silenced by it.

## Two ways of obliging someone are still not caught, on purpose

Adding an *overload* of a name an interface already declares is a real gap, and closing it would
change what "member" means everywhere else in the same checks, including the half that guards
removals. It would also fire on every overload anyone adds anywhere. A check that has to be worked
around is worse than no check, so this one stays open and written down.

Turning an existing default member into an `abstract` one cannot be seen by this kind of check at
all: nothing is added and nothing is removed. It is the same blind spot as changing what a method
*does* without changing how it is written, and it is recorded with the others rather than pretended
away.

Four ways, two now caught, two documented — and a reviewer of an interface change has two things
left to check by hand instead of four.
