---
Name: A module's version is read without tripping over its neighbours
Category: Fix
Description: Reading one attribute off a module assembly made the runtime resolve every OTHER attribute on it too, so a module with a dependency the process could not load died with a reflection stack that named neither the module nor what was missing. The value is now read straight out of the assembly's metadata.
Icon: Bug
Order: -20260918
---

# A module's version is read without tripping over its neighbours

Every installed module reports a version stamp. It is one line of metadata, and the platform reads
it constantly: the version is the floor a compiled page records against the module it was built
with, so the installation can tell "this was built against something at least this new" from "this
has to be rebuilt".

Reading it used to go through .NET's reflection, and reflection answers a bigger question than the
one being asked. To find *one* attribute on an assembly, it has to look up the type behind **every**
attribute on that assembly — including ones nobody asked about. If any of those types live in a file
the installation does not have, the read does not come back with "no version"; it fails outright.

## What it looked like

The build tool refused to run with a nine-frame stack trace from inside .NET itself, ending in
`Could not load file or assembly 'System.ClientModel'`. Nothing in it said which module was being
read, which argument had introduced it, or that the missing file belonged to an attribute that had
nothing to do with the version. It read as "the tool is broken".

The same read runs on a live portal over every installed module, so one module shipping with a
private dependency was all it would have taken to meet this there too.

## What changed

The version is now read straight out of the assembly's own metadata, which looks up no types at all.
It is the same attribute and the same string it always was — it simply no longer depends on the rest
of the assembly's attributes being resolvable.

Two smaller things came with it:

- **A module that cannot be composed now says so by name.** The message names the file, what went
  wrong with it, and that a module's dependencies have to be beside it. Before, only two of the
  possible failures were translated and the rest escaped as raw stack traces.
- **The build tool's `compile` step reports that as a refusal, not a crash** — one sentence and exit
  code 1, the same shape as every other reason it declines to bake.

## What this means for you

**Nothing to do.** No version anywhere is computed differently; the same modules report the same
stamps. What is gone is a failure mode where a perfectly good module with an unusual dependency
could take a build — or a portal's compile — down with an error about a file nobody had mentioned.
