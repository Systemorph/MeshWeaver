---
Name: The install check says which record it counted
Category: Fix
Description: The boot-time install check now names the install record its numbers were taken over — path, version and map size — so a count taken over a stale record stops reading like one taken over the current one.
Icon: DocumentSearch
Order: -20260913
---

The boot-time install check reports how many nodes a package declares and which of them are missing
from the mesh. It never said **which install record** it read those numbers from — and the record is
the half of the comparison that can be out of date.

That gap cost a day. A check reported `2 of 201 declared node(s) are ABSENT` and named two files;
the install record, read afterwards, declared 193 files and neither of those names. The number was
right about something, and there was no way to tell what: a count taken over the current record and
a count taken over a stale one looked exactly the same.

Every verdict now names the record it was taken over — where it lives, which version of it was read,
when that version was written, and the stamps identifying the package snapshot its file map came
from — and prints the map's own size beside the count it produced, so a record that has gone
backwards shows up in the line itself.

The verdicts that are *not* a shortfall carry it too: "the mesh could not be read" is as
unactionable as a missing node when nobody can say which record went unverified. And when a caller
supplies no record, the line says so in words rather than leaving a blank that reads like an answer.
