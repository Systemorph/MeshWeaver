---
Name: Controls no longer render as plain text when a module ships a duplicate of a portal assembly
Category: Fix
Description: A module package could carry its own copy of an assembly the portal already ships. The portal then held two builds of one name, kept whichever it loaded first, and the views in the other copy never activated — so pages rendered their controls as raw text. Packages now carry only what the portal does not already have.
Icon: Sparkle
Order: -20260908
---

# Controls no longer render as plain text when a module ships a duplicate of a portal assembly

Some pages came up with their controls rendered as plain text instead of as buttons, cards and
stacks — the raw shape of the control, printed out, where the styled view should have been. The
portal's own health page said what had happened, in a line that read like housekeeping: *one module
is landed but not yet loaded*.

Both statements had the same cause. A plugin package ships its code as a bundle, and a bundle
carries the package's own assembly plus anything it needs that the portal does not already have. The
rule that decided "does the portal already have this?" was a list, written and maintained by hand in
each plugin repository — and a list goes out of date the moment something moves. When it did, a
package began shipping a second copy of an assembly the portal ships too.

The portal cannot use both. Two copies of one assembly name are one identity to it: it keeps the
first one it loads and the second is simply never there. When the copy that lost held the views —
the components that draw a control on screen — the portal has the control but nothing to draw it
with, so it falls back to printing the control instead. Nothing errors, nothing is missing from any
log; a page just renders wrong.

The rule is no longer a list. When a package is built, the build now looks at the portal it is being
built for and **measures** what that portal actually ships — the assemblies alongside it, the record
it publishes of what it was compiled against, and the modules it carries in its own module folder —
and drops anything the portal already has from the package. Nothing the portal does not have is
dropped, so a package that genuinely needs to bring an assembly still brings it.

Measured across the plugin catalog when this was found: 14 of 37 packages were carrying 27 such
duplicate copies. They are gone from the next build of each package, and the check that holds a
portal back from updating onto an inconsistent set of packages — which is what caught this — now has
nothing to hold.
