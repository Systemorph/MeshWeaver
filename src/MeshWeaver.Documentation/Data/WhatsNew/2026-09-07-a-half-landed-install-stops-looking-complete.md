---
Name: A half-landed install stops looking like a finished one
Category: Fix
Description: If part of an installed package goes missing, reinstalling it now actually restores it — and the portal says which pieces are gone instead of serving the gap in silence. Previously an install was judged by comparing two version stamps, which cannot see anything about what is actually there.
Icon: CheckmarkCircle
Order: -20260907
---

# A half-landed install stops looking like a finished one

When you install something from the Store, the platform writes down what it installed. Until now
that note was the only thing anyone ever consulted afterwards. Asked whether an install was still
whole, the portal compared the version stamp on its own note against the version stamp the Store was
offering — and if they matched, it concluded there was nothing to do.

That comparison is a fact about the Store, not about your space. If a piece of an installed package
went missing afterwards — deleted, or lost to an interrupted sync — the two stamps still matched, so
the portal reported the package as up to date. Worse, the obvious remedy did not work: pressing
Install again returned immediately, having decided from the same note that nothing needed doing.

That is not hypothetical. On one portal a package had been missing one of its parts since 26 August.
It was reinstalled on 3 September, which changed nothing, and the gap was still there — and still
unreported — eleven days later, by which point it had caused a much larger failure.

Three things change.

**Reinstalling repairs.** Before deciding an install is up to date, the portal now looks at what is
actually in your space and checks that every piece the install recorded is really there. If anything
is missing, the install runs and puts it back, instead of returning "nothing to sync".

**A gap is named, not swallowed.** On every start-up the portal checks each installed package the
same way and reports what it finds — by name, listing the exact pieces that are absent. It reports
only; repairing stays with the install itself, so nothing is quietly rewritten behind your back.

**An install that started and stopped is no longer invisible.** Installing a package creates its
space first and records the install last. If it failed in between, what was left behind was a space
that looked simply empty — indistinguishable from one you had made yourself and not filled in. Four
such leftovers were sitting on one portal, created within fifteen seconds of each other, with
nothing anywhere saying an install had ever been attempted. Those are now reported too.

One deliberate limit, so the reports mean what they say: where a package was installed too long ago
to have recorded which pieces it shipped, the portal cannot check it, and says exactly that rather
than passing it as healthy. "Not checked" and "checked and fine" are never printed as the same
thing.
