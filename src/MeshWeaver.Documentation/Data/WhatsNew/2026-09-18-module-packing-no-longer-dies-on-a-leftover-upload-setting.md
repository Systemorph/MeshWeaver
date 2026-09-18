---
Name: Module packing no longer dies on a leftover upload setting
Category: Fix
Description: Packing a node repository's modules failed on every run that used the object store, after the step had already done its work. A setting belonging to the artifact upload it replaced had been left behind inside the shell script, where the shell tried to run it as a command. Because packing is where a repository's modules are built, every job downstream reported that no module had produced a receipt, and the gate that composes those modules failed rather than skipped — so the cause was several steps away from the first thing anyone saw.
Icon: Box
Order: -20260918
---

# Module packing no longer dies on a leftover upload setting

Node repositories build their modules through a shared packing lane. One step in it hands the
platform's assemblies to the rest of the matrix, and it can do that two ways: through the ordinary
artifact upload, or — on the lanes configured for it — by writing to an object store instead.

The object-store version was written by converting the upload step into a small shell script. The
conversion left one line behind. `compression-level: 0` is a setting the artifact upload understands;
inside a shell script it is just a word with a colon after it, and the shell reports that no such
command exists. The script runs with `set -euo pipefail`, so that report is fatal.

The result was a step that did its whole job and then failed anyway: the archive was written, stored
and its address reported, and the step still exited non-zero on the line after. Nothing in the error
named compression, uploading, or the step's actual purpose.

## Why the first symptom was somewhere else entirely

Packing is where the modules are built, so a failure there is silent in its own right and loud
everywhere downstream. The job that checks the results reported that dozens of selected module
bundles had produced no receipt and were never built. The gate that composes those modules reported
that it could not measure anything, and failed rather than skipping — correctly, because a gate that
skips when its input is missing is indistinguishable from one that passed.

So the visible failures were a count of missing modules and a gate refusing to guess, several steps
away from a stray line in an unrelated upload setting.

## What changed

The leftover setting is removed. The comment that travelled with it explained why no compression was
wanted; it now says that plainly, describing the archive the script actually creates, so the next
person converting a step does not reintroduce the line it was justifying.

The rest of the step is untouched: the archive is still uncompressed on purpose, because the entry
carrying the assemblies is already compressed and packing it again would cost upload time for
nothing. The small digest file stored beside it compresses to nothing either way.
