---
Name: Package installs keep the content you actually authored
Category: Fix
Description: A package file with a leading byte-order mark and a dynamically-compiled content type could install a materialised value instead of the file's own content, and rewrite that node on every later install. Both are fixed at the root — the installer's authored-content re-read now tolerates exactly what the primary parse already tolerates.
Icon: FileCheckmark
Order: -20260826
---

# Package installs keep the content you actually authored

Installing a content package (a node-repo plugin, a course, a NodeType's instances) writes what the
file says. For most files that was always true. For one narrow but real case it was not: an instance
file whose content type is a NodeType that has already compiled.

## What was happening

Once a NodeType compiles, its content type becomes a name the mesh can resolve — but only by name.
The same short name can exist in more than one package, so the installer's own parse of such a file
does not trust the typed value it gets back: it re-reads the file's `content` property as written and
installs that instead, specifically so one package's compiled defaults can never leak into another
package's node.

That re-read used stricter rules than the parse that produced the typed value in the first place. A
file that opened with a UTF-8 byte-order mark — invisible in an editor, and common enough that one
sample package ships several instance files this way — made the re-read fail. When it failed, the
installer fell back to the materialised value after all: the one thing this whole mechanism exists to
avoid, logged as a warning naming exactly that risk.

That materialised value could then differ from the authored file in ways that had nothing to do with
cross-package contamination — a numeric field with a matching default, present in the file, absent
from the re-serialised value. The next install's unchanged-check compared the stored node against the
authored file, found a difference that was never really there, and rewrote the node again. Every
install after that repeated the same rewrite.

## What changed

The installer's authored-content re-read now shares every tolerance the primary parse already
applies — the same byte-order-mark stripping, the same comment and trailing-comma leniency — instead
of falling back to stricter defaults. A file that parsed once now re-reads the same way, every time.

## What you will notice

A package install writes the node your file actually declares, and a second install of an unchanged
package writes nothing at all.
