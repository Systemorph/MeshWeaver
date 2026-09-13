---
Name: A type is no longer judged before its sources arrive
Category: Fix
Description: A node type that activated while the code it draws on was still being imported was told its prebuilt build was out of date, rebuilt from nothing, and left failed. It now waits for its sources instead of drawing a conclusion from their absence.
Icon: Checkmark
Order: -20260913
---

# A type is no longer judged before its sources arrive

When a package is installed, the platform hands each node type a build that was already compiled
elsewhere, and the node type checks it: *are these the sources these bytes were built from?* If the
answer is no, the bytes are treated as out of date and the type is rebuilt from the code this
installation actually holds.

The check compares two fingerprints — one recorded by whoever built the bytes, one computed here
over the live source. It worked, and it asked its question a moment too early.

## What went wrong

Installing does not deliver everything at once. A type whose name sorts early can come alive while
the shared library it draws on is still being written. At that instant it has **no** sources — and
the fingerprint of no sources is still a perfectly ordinary-looking value. So the comparison ran, the
two values differed, and the conclusion drawn was *"the source has moved past this build"*.

It had not moved. There was nothing there yet to have moved.

Everything after that followed honestly from a false premise. The delivered build was set aside, a
fresh build was started from the code on hand — which was none of it — and the compiler reported
exactly what you would expect: a page of errors about types that "could not be found", naming code
that is perfectly correct and was simply not there yet. The type was then marked as failed and
stopped retrying, seconds before its sources landed. Every one of its siblings, with the identical
configuration, installed cleanly once the import had finished.

## What happens now

A source set that has matched nothing is treated as *"not established yet"* rather than as
*"everything was deleted"* — which is what it was being read as. The check does not draw a
conclusion from it, and it does not spend its one chance to refuse. The delivered build keeps
serving, the question stays open, and the moment the sources land the platform answers it: the
fingerprints agree, the build is confirmed, nothing was ever rebuilt and nothing failed.

There is no waiting loop and no retry behind this. The type already watches its own sources; the
judgement simply happens on the arrival that makes it answerable.

The log says so plainly when it happens — that a type was activated ahead of the sources it declares,
and that nothing is wrong with it. Previously the same moment produced a page of compiler errors
about somebody else's code.

A build whose sources genuinely *have* moved is refused exactly as before. That protection is what
stops last week's code running over today's data, and none of it is relaxed here: the difference is
only that a refusal now requires something to have actually been measured.
