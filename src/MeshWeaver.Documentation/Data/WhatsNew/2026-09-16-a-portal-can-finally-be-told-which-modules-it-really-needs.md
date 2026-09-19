---
Name: A portal can finally be told which modules it really needs
Category: Fix
Description: Clearing a portal's list of required modules used to require MORE of them, not fewer, and a short list quietly demanded modules nobody had listed — because the list replaced the built-in one entry by entry instead of as a whole. A portal's record can now say "these and only these", and a portal that has not said it now reports which requirements it inherited rather than leaving them to be guessed.
Icon: Box
Order: -20260916
---

# A portal can finally be told which modules it really needs

Every portal carries a list of **required modules** — add-ons it cannot serve correctly without, so
that an update which loses one stalls instead of quietly rolling out a portal with a feature
missing. A portal's own record can name that list.

It could not, in fact, name it. The record's list was applied to the built-in one **entry by entry**:
first entry replaces first entry, second replaces second, and anything further down the built-in list
simply stayed. Two things followed, and both were the opposite of what an operator meant:

- **A shorter list demanded modules nobody had listed.** A record naming five modules, against a
  built-in list of nine, left four requirements standing that the record never mentions — and they
  showed up in the portal's missing-module report as if someone had asked for them.
- **Clearing the list made it stricter, not looser.** An empty list replaces nothing at all, so the
  whole built-in list stood. One portal was edited to require nothing and the next pod to start
  reported five required modules missing.

Neither could be fixed by writing a longer list, because nobody outside the platform can see how long
the built-in list is — and it grows. The advice used to be "put your entry at the first free slot";
that slot was number seven when the advice was written, and number seven has since become a module of
its own.

**A record can now say that its list is the complete one.** Set `requiredModulesAuthoritative` beside
`requiredModules` and the portal requires exactly what the record names — including nothing at all,
when the list is empty. Slot numbers stop mattering, and no one has to know anything about the
built-in list to state what an instance needs.

**And a portal that has not said it now says what it inherited.** On start-up it names every required
module that came from the platform rather than from its own record, so the difference between "what
the record says" and "what the instance requires" is printed rather than inferred.

Nothing changes for a record that does not use the new setting — a deliberate choice, because the
built-in list is the platform's own floor, and treating a partial list as the whole truth would have
silently dropped real requirements from every portal at once.
