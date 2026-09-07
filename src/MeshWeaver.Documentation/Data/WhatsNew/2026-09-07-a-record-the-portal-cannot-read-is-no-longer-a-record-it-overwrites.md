---
Name: A record the portal cannot read is no longer a record it overwrites
Category: Fix
Description: Five places wrote a blank record whenever they could not read the one that was already there — including your own account, on a click that was only meant to hide a page. They now refuse the write and say so, leaving the record intact.
Icon: Checkmark
Order: -20260907
---

# A record the portal cannot read is no longer a record it overwrites

Reading is deliberately forgiving. If a stored record cannot be understood — because it was written
by a different version, or arrives in a shape the reader does not recognise — the portal shows a
safe default rather than an error page. A settings tab that shows defaults is better than one that
will not open at all.

That forgiveness is exactly wrong when the next thing that happens is a **write**. Five places
read a record, silently got the blank default because they could not read it, changed one field on
that blank, and saved it back. Everything the record used to hold was gone — and nothing was
logged, because from the inside it looked like saving a change to an empty record.

## Where it could happen

- **Your own account.** Marking a page hidden for presentation mode read your user record and, if
  it could not read it, wrote a fresh empty one — replacing everything else on it.
- **A repository's sync settings**, while repairing the address of a repository that had been
  renamed.
- **The build coordination record**, in three places: registering to build, granting a build, and
  committing that grant to the durable lock every part of the system reads to decide who is
  building. A blank there means "nobody is building this", so a second full build could start on
  work already under way.
- **A script or notebook's run log**, on every flush while it runs. The fields that would have been
  lost are precisely the ones the log does not re-write each time: which run it is, who started it,
  when it began — and a cancellation someone had just requested.

## What changes

**A record that cannot be read is refused, loudly, and left exactly as it was.** The write does not
happen, and the refusal names the record, what it was trying to do, and what it declined to
overwrite — so it is something you can act on instead of a field that quietly goes missing weeks
later.

**A record that genuinely is not there yet still gets created**, exactly as before. "There is
nothing here" and "I could not read what is here" were the same answer; they are now two different
answers, and only the first one leads to a write.

## Why it will not come back

A build check now scans the whole codebase for the shape that caused it and fails if a single one
reappears — with no exemption list, because the correction is mechanical and changes nothing about
how the code behaves when the record *is* readable.
