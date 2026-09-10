---
Name: Your installation now says which version of a module it is really running
Category: Fix
Description: A module can be present in two places at once, and only one of them can actually run. Your installation used to report the one it asked for; now it reports the one it is running, and stops asking you to restart for something a restart cannot change.
Icon: Checkmark
Order: -20260910
---

# Your installation now says which version of a module it is really running

A module can exist in **two places at once** on your installation: the copy that ships inside the
platform image, and the copy the registry delivered. That is deliberate — the built-in copy is what
lets a module work at all on an installation with no registry to serve one.

But only **one** of them can actually run. Which one wins is decided the moment the module is first
needed, and until now nothing checked the answer. Your installation recorded the version it *asked
for* and carried on. If that was not the version it got, everything downstream inherited the wrong
answer, and nothing anywhere said so.

That happened on a live installation. It showed up as **"restart to activate"** that never went
away: the restart ran, the same two copies were still there, the same one won, and the message came
straight back. There was nothing in any log to explain it, and no way to tell from the outside
whether an update had taken effect or not.

## What changes

**The module list now shows the version that is running, not the one that was requested.** Where
the two differ you see both — *"running this one; that one was requested and never started"* —
together with the reason. This is the same row your installation already uses for a module that is
running an older version than the newest one installed, so nothing new has to be learned to read it.

**"Restart required" is no longer shown when a restart cannot help.** As with a module held back for
a newer platform, a prompt no restart can clear is worse than no prompt: it hides a real state
behind an action that does nothing. The state is now named instead.

**An update is no longer written off because it never got a turn.** Your installation keeps a note
of module versions it has tried and found unrunnable, so it does not keep re-downloading them. A
version that never actually started is a different thing from one that started and failed — it has
not been judged at all, and it is no longer recorded as if it had. Without this, a perfectly good
update could be skipped for good on the strength of a test that never ran.

**When a module genuinely cannot load because another copy holds its place, the message says
where.** Previously it named only the module. It now also names the file that is occupying the
name — which is the copy your installation is actually running.

## What this does not change

**Nothing about which copy wins.** That is a property of how the platform loads code, not a setting,
and this change does not alter it. What changed is that your installation can now *tell you* — and
tells its own bookkeeping the truth as well.

**Nothing is refused that used to work.** The module still loads, still registers, still serves its
features. A readiness check stays healthy on it, and a module listed as required still counts as
present. The only difference is that the version is now reported accurately.
