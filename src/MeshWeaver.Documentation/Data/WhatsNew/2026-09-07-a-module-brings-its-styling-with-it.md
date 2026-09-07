---
Name: A module now brings its styling and scripts with it
Category: Fix
Description: Test environments built by the shared build lanes installed a module's code but left its stylesheets and scripts behind, so pages that used the module rendered as an empty box. They are now delivered together, and a delivery that loses any of them stops rather than continuing quietly.
Icon: ShieldCheckmark
Order: -20260907
---

# A module now brings its styling and scripts with it

A module can bring more than code: a module that adds something you *see* — an editor, a map, a
chart — also brings the stylesheet and script that make it look and behave the way it does. Those
travel with the module in their own compartment, separate from its code.

The shared build lanes that stand up a temporary environment to check a change were unpacking the
code compartment and not the other one. The module installed cleanly, the environment reported
itself healthy, and then any page that actually *used* the module rendered the surrounding text and
an empty box where the component belonged.

## Why it stayed hidden

Nothing about it looked wrong. The module was there. It loaded. Nothing failed, nothing was logged
above the quietest level, and the environments that were checked most often did not display
anything, so nobody was looking at a page. Live portals were never affected — they install modules
by a different route that was always complete.

It surfaced only where a check actually *opened a page*: an interactive exercise showed its brief
and then, where the workbench should be, a bare placeholder — for three days, in every run, on
every course.

## What changes

**Both compartments are delivered, together.** A module's stylesheets and scripts land beside its
code, in the exact arrangement the portal expects to find them in.

**A partial delivery now stops.** The step counts what the module *ships* against what actually
*arrived*, per file, and fails naming the first thing missing. Previously "nothing arrived" and
"there was nothing to bring" produced the same silent success — and most modules genuinely bring
nothing, which is precisely what made the empty result look normal.

**Each run says what it moved.** The log now reads, for example, *30 modules, 6 of them carrying
styling and scripts, 294 files delivered* — so a zero is visible as a zero instead of being
indistinguishable from a healthy run.
