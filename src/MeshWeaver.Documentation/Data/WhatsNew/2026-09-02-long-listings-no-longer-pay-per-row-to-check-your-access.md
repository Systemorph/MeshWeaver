---
Name: Long listings no longer pay per row to check your access
Category: Fix
Description: A list of a hundred things used to ask the database a hundred separate questions about who you are before it could show you anything, so the bigger the list the longer the wait. It now asks once per space, whatever the length of the list.
Icon: Filter
Order: -20260902
---

# Long listings no longer pay per row to check your access

Every list the platform shows you is filtered to what you are allowed to see. That part is not
optional and has not changed. What changed is how the filtering was priced.

Access is granted on a *place* — a space, a folder, a partition — and it flows down to everything
inside it. But the platform was asking the question the other way round: for each item in the list,
it looked up the access rules for that item's own address, then for its parent, then its parent's
parent, up to the top. Since every item has its own address, every item added two fresh live
lookups that nothing else could reuse. A four-item list cost thirteen lookups; a thirty-two-item
list cost sixty-nine. Nothing appeared on screen until the last of them came back, so a long list
took visibly longer to show its first row than a short one — and the Plugin Store, where every entry
sits in its own space, was the worst case of all: minutes, as reported.

The rules for every level of a space live *in that space*, so one look-up answers all of them. The
platform now reads them that way: once per space, plus the handful of platform-wide lists it has
always kept warm. The same four-item list costs five lookups, and so does the thirty-two-item one.
The cost follows how many *spaces* a list draws from rather than how many rows it shows, and it no
longer grows with how deeply nested those rows are.

You see the same items you saw before. This is a change to how the answer is fetched, not to what
the answer is: the rules consulted for any given item are exactly the ones that were consulted
before, and a permission you were refused is still refused. One long-standing quirk goes away with
it — administrator grants live in a place that platform-wide searches deliberately skip, which used
to need a special case to be found at all. The new route finds them the ordinary way, so there is no
special case left to go wrong.
