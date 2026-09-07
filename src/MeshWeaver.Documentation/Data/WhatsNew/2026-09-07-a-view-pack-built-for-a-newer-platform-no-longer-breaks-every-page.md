---
Name: A view pack built for a newer platform no longer breaks every page
Category: Fix
Description: An installed module whose code needs a newer platform than your installation runs used to load anyway and then fail on every page that touched it. Now it is refused before it loads, only that module is missing, and the card says why.
Icon: Checkmark
Order: -20260907
---

# A view pack built for a newer platform no longer breaks every page

If your installation had picked up a module — a view pack, an app's code — that was **built against
a newer platform than the one you are running**, the module loaded anyway. Nothing checked whether
the code it referenced was actually there. The failure only appeared later, when someone opened a
page: every render that touched that module threw an error, on every page, for every person, until
somebody read a server log and worked out which module was responsible.

That happened on a live portal. Every code cell showed *"This area failed to render"* for a full
day. The module reported itself as installed and healthy the whole time.

## What changes

**A module is now checked against the platform you are actually running — before it loads.** Not
against a version number the module's author wrote down, but against the real question: does this
installation have the code this module was built against? That answer is already recorded in the
module's own files, so it can be measured exactly rather than assumed.

**A module that fails the check is set aside, and nothing else is affected.** It does not load, it
contributes nothing, and every other module and every page keeps working exactly as before. The
difference is between *"one view pack is missing"* and *"everything on the portal is broken"*.

**The package card tells you, in your language.** Instead of a feature that is silently absent, the
card says the version you have was built for a newer platform and will start working when your
installation updates. You are not left guessing why something you installed did nothing.

**"Restart required" is no longer shown when a restart cannot help.** A set-aside module used to be
reported as waiting for a restart — a prompt that no restart could ever clear, because the restart
would reach the same conclusion. It is now reported as what it is: waiting for the platform.

**An installation that cannot answer the question refuses rather than guesses.** If the module's
files cannot be read at all, that is *"I could not determine whether this works"*, and it is treated
exactly like *"this does not work"* — never quietly as *"this is fine"*.

## What this does not change

**Installing a module built for a different platform is still allowed and still normal.** Modules
are meant to be installable across platform versions; only the ones this installation genuinely
cannot run are held back, and they start working by themselves at the update that makes them
runnable — no re-install, nothing to clean up.

**A registry still carries modules it cannot run itself.** An installation acting as a registry for
others goes on stocking and serving modules built for newer platforms. It simply does not load them
into itself.

**One kind of mismatch is still invisible until it happens.** The check compares the *types* a
module uses. A change to a method's arguments on a type that still exists is not visible to it —
that case is still caught when the module registers itself, which sets aside that one module in the
same way.
