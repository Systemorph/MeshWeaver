---
Name: A build handoff that reported success and arrived nowhere
Category: Fix
Description: Builds across every node repository stopped completing. A step reported that it had stored a 1.4 GB build output, and the eight jobs that needed it reported thirteen seconds later that nothing was there. Both were telling the truth — they were writing to and reading from two different shared drives that are addressed by the same name. The step that stored the file could not have noticed, because its success message was assembled entirely from the file it had been handed, never from the copy it had made.
Icon: Box
Order: -20260919
---

# A build handoff that reported success and arrived nowhere

Building a node repository's modules happens across several machines. One of them compiles
everything once and hands the result — a single 1.4 GB archive — to the machines that pack the
individual modules. To keep those large files off the build service's own storage, the handoff can
go through a shared drive that our machines already have mounted.

Switching the whole fleet onto that shared drive stopped every one of those builds. The machine
that wrote the archive reported success. The eight machines that wanted it reported, thirteen
seconds later, that no such file existed. Both reported their environment identically: the same
drive, the same folder, the same build.

## They were not looking at the same drive

Our build machines come in two pools, and each pool is set up separately. Each was given a shared
drive of its own, and both drives are presented to the jobs under the same name. From inside a job
the two are indistinguishable — the folder path is identical, and nothing a job can read tells it
which of the two drives is behind that path.

So the write really did succeed and the read really did find nothing. The compiling machine belongs
to one pool, the packing machines to the other, and the archive was sitting on a drive they cannot
see. This had never worked; the earlier builds that looked like proof it did were using the build
service's own storage, not the shared drive.

## Why the message said nothing useful

The step that stores a file printed its size and checksum, and then said it had written it. Both
numbers were measured from the file it had been given — never from the copy it had just made. It
never looked at the copy at all.

That makes the success message one that cannot be wrong. Whatever happened to the copy, the step
reported success, so the fault could only ever appear later, on another machine, as a missing file
with no explanation attached. A message that cannot be wrong is not a check.

## What changed

**Storing a file now reads the stored copy back.** After writing it, the step confirms the copy
exists, is the right length, has the expected checksum, appears in its own folder listing, and left
no half-written leftovers. It also makes sure the data has reached the drive itself rather than
sitting in the writing machine's memory, which is what lets another machine see it at all. If any of
that fails, the build stops there and names the file — instead of succeeding and letting eight other
jobs fail confusingly a few seconds later.

**A shared drive now has an identity, not just a name.** Each job asks the operating system which
drive is actually behind the folder, and the build records the answer once, at the start. Every read
and write afterwards checks it. A machine standing on a different drive than the rest of the build
now stops immediately, at the first file it touches, and says so in those words — rather than
producing a handoff that nobody can collect. The identity is read from the system rather than
invented and stored, so there is nothing to keep in sync, and two different spellings of the same
folder are still correctly recognised as one drive.

Together these turn the fault into something the build reports about itself, at the moment and on
the machine where it happens.
