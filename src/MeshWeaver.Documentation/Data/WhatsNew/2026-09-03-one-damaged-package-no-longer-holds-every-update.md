---
Name: One damaged package no longer holds every update
Category: Fix
Description: Before your installation updates itself, it reads the prepared packages published for the new build. A single package file that could not be opened made that whole reading fail — so your portal stopped offering updates, with a reason nobody could act on. The reading now skips just the damaged file and judges the rest.
Icon: ShieldCheckmark
Order: -20260903
---

# One damaged package no longer holds every update

Before your installation moves to a new platform build, it looks at the packages that have been
prepared for that build and checks that they fit together (see *An update is held when its packages
do not fit together*). To do that it opens each prepared package and reads what it was built against.

If one of those files could not be opened — a partial upload, a corrupt archive, a file that was
never a package at all — the whole reading gave up. Your installation then treated the entire build
as unreadable: no update was offered, and the **Updates** settings tab showed a hold that named the
build but nothing you could do about it. One damaged file, and every installation reading that
published set was stuck.

**The reading now fails one file at a time.** A package that cannot be opened is noted in the log
and counts only as "present" — there is nothing in it to compare — while every other package is
still read and judged as before. An update that is genuinely inconsistent is still held, naming the
package and the mismatch; an update that is fine is offered, even when a stray unreadable file sits
beside it.
