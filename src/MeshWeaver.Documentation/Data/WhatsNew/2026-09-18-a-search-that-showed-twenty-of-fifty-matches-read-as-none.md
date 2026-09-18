---
nodeType: WhatsNew
Name: A search that showed twenty of fifty matches read as none
Category: Fix
Description: "Guidance for reading CI now names the case where a search finds what you asked for and you still conclude it is absent: the list was cut off before the match. The tell, and the two habits that catch it, are written down."
Icon: Search
Order: -20260918
---

A claim about a repository — *"it never calls that shared step"* — turned out to be wrong in a way
worth writing down, because the search behind it had worked perfectly.

The search did find the line. It was the thirty-sixth match of fifty, and the command had been
written to show only the first twenty, in a file of nearly five thousand lines. So the answer was
produced, and then cut off before the part that mattered, and what remained looked like a complete
result. The conclusion drawn from it was relayed onward as a measurement and acted on.

What makes this one hard to notice is that **a correct search is the dangerous case**. A search with
a mistake in it announces itself — nothing plausible comes back, and you go and look again. A
correct search whose output has been shortened comes back full of real, relevant lines, so the
window reads as the whole answer and there is nothing to prompt a second look.

The guidance on reading CI signals now carries this alongside the other cases where an instrument is
working correctly and is being read as answering a wider question than it does. Two habits catch it:
count the matches before looking at a shortened list, so that a count larger than the window is
visible as such; and when checking that something is *absent*, first search for something you know
is present and confirm that it shows up inside the window you are actually reading. It also records
a second habit from the same episode — when the same question is asked of several things, the check
that decides the answer is the one to run on every one of them, rather than settling the last one
with something cheaper.
