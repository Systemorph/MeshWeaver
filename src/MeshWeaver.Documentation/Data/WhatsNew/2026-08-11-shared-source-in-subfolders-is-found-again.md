---
Name: Shared source files in subfolders are found again
Category: Fix
Description: A content type that borrows code from another area kept in a subfolder had those files silently left out when the platform prepared an update, so the type reported an error naming a symbol that exists — and the update refused to roll out.
Icon: Sparkle
Order: -20260811
---

# Shared source files in subfolders are found again

A content type can borrow source files from somewhere else on the mesh — a shared library of
fixtures in another area, say. When the platform prepares an update it gathers every type's source
files in one sweep, and that sweep asked the mesh for "everything below any *Source* folder".

It did not get everything below. It got the files sitting **directly** in each *Source* folder, and
nothing from a subfolder underneath — the "and everything below" part of the request was quietly
dropped for a search that spans areas. So a type whose shared library lives one folder deeper was
prepared with that library missing, and reported an error naming a class that is right there on the
mesh: *the name 'MtplClaimFixtures' does not exist*.

This was the most misleading shape a failure can take. The type had plenty of its **own** files, so
nothing looked empty or absent; the only symptom was a compile error that reads exactly like broken
content. The update safety-check then did its job and refused the roll-out — correctly, on
evidence that was wrong.

Two things follow from the fix. Those types prepare and compile again, so the update proceeds. And,
more generally, any search that asks for a folder's whole subtree **across areas** now really
returns the subtree: previously it returned only that one level, silently, with no error to tell
you the answer was narrower than the question.
