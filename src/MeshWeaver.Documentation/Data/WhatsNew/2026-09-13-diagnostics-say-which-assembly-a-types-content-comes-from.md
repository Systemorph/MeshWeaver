---
Name: Diagnostics say which assembly a type's content comes from
Category: Feature
Description: A NodeType's diagnostics now name the assembly its content type actually resolved to in the running installation — its file, its fingerprint, and whether it was compiled here — so "the record says this build" and "this is the build answering" stop looking like the same statement.
Icon: Search
Order: -20260913
---

# Diagnostics say which assembly a type's content comes from

Ask an installation for a type's diagnostics and it tells you whether the last compile succeeded, when,
and a fingerprint of the bytes that compile produced. Every one of those is a note somebody's build
left behind. None of them answers the question you are usually holding: **when this installation reads
a node of this type right now, which code is doing the reading?**

Most of the time those are the same thing, which is exactly why the gap is expensive. On one occasion
an installation reported the newest copy of a module, with the newest timestamp, serving types that
predated two finished pieces of work. Everything on the report said "newest" — truthfully, about the
file. It took a night to find that the newest copy had never actually been put into service, and the
older one was still answering. From the outside, "the shelf is stale" and "the newest thing on the
shelf was never taken down" looked identical.

So the diagnostics now carry one more thing, and it is read from the code that is loaded rather than
from any record about it:

- the **name** of the type the installation resolves this NodeType's content to,
- the **file** that type came from — or a plain statement that it has no file, because it was compiled
  here rather than shipped,
- its **fingerprint**, the one the compiler stamps into the code itself, and
- whether it was **compiled in this installation** or arrived as part of one.

That last pair is what separates the two cases that used to look the same. A type compiled here has no
file and is marked as such; a type that shipped with a module has a file and is not. When both are
present at once — which happens whenever a piece of code is being replaced — you can now see which of
them answered.

And when nothing answers, it says so in words rather than by leaving the field out. "No type is
registered for this NodeType here — its content stays unreadable and its views render empty" and "this
installation has no type registry at all, so nothing was measured" are two different situations, and
reading a blank as "fine" is the mistake this exists to prevent.

Nothing decides anything differently because of it. It is a reading, and its whole value is that it is
a reading you could not take before.
