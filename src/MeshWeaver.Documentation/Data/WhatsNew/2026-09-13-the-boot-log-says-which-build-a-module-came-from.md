---
Name: The boot log says which build a module came from
Category: Feature
Description: The startup line for each installed module now names the commit its code was built from and the platform build it was compiled against — so "this is the newest copy on disk" and "this copy has the newest code" stop looking like the same statement.
Icon: Checkmark
Order: -20260913
---

# The boot log says which build a module came from

Every time an installation starts, it writes one line per installed module saying which file it is
about to load. Until now that line described the **file**: where it is, a fingerprint of its bytes,
and when it was written.

That is not the same question as *"is the code in it current?"*, and on one occasion the difference
cost a night. A module was the newest copy on the volume, written minutes earlier, with a fresh
fingerprint — and the behaviour it was serving predated two changes that had already shipped. Both of
the things the line said were true. Neither of them was the thing anyone needed to know. The
conclusion drawn from them — *"we are being served stale files"* — was wrong, and several restarts
were spent on it before a health check showed what had actually happened: the new copy had been
delivered but never adopted, and the older one it was meant to replace kept serving.

## What changes

**The line now also says what the module was built FROM and built AGAINST.** Two new fields:
`built-from`, the commit of the repository that produced the code, and `framework`, the platform
build it was compiled against. Both are what the module's publisher recorded when the module was
packaged — statements about the source, not measurements of the file — so "this is the newest copy"
and "this copy has the newest code" are now two things you can read separately.

**A module that carries no such record says so, in those words.** The line prints `(unrecorded)`
rather than guessing from the version number, the folder name or the fingerprint. A plausible-looking
value derived from something else would be the original problem again, with one more field to be
misled by.

**Where two copies of a module are kept, the line describes the one being loaded.** An installation
keeps the previous version of a module as a fallback for the case where the newest one cannot run
there. Those are different builds from different commits, and the line reports whichever one is
actually about to load — never the other one's details.

## What this does not change

**Nothing decides anything on the new fields.** They are there to be read. Which module version an
installation runs, and whether it updates, are decided exactly as before.

**A module published before this existed still installs, updates and runs normally.** It simply has
no commit recorded, and its line says `(unrecorded)`.

**Publishers start recording it as their build pipelines adopt the option.** The packaging tool
accepts the commit now; each repository that publishes modules passes it when its own pipeline moves
to a version of the tool that supports it. Until then the field reads `(unrecorded)` — honestly,
rather than approximately.
