---
Name: A plugin can bring its own engine, and it arrives with the plugin
Category: Fix
Description: A plugin that needs a compiled library — a database engine, an image codec — could name it, and the library was quietly left behind on the way to your installation. The plugin then installed cleanly, started, and failed the first time it actually used that library. The library now travels with the plugin, end to end, and a plugin that would have shipped without one is refused at the door instead.
Icon: Box
Order: -20260915
---

# A plugin can bring its own engine, and it arrives with the plugin

Some plugins depend on a **compiled library** rather than on ordinary plugin code — a database
engine, an image codec, a compression library. These are not written in the same language as the
rest of the platform; they are machine code, built separately for each kind of machine, and a plugin
that needs one cannot do its job without it.

Until now such a plugin could *name* the library it needed, and the library was **dropped on the way
to you**. Not refused, not reported: dropped. The plugin was published, appeared in the catalog,
installed, and started — and the first time it tried to open a database it failed with a message
naming a file that was never delivered. Nothing earlier in the chain had said anything, because
nothing earlier in the chain had looked.

It worked at all only where the *host* happened to already carry the same library for its own
reasons. That is exactly the arrangement the platform called a defect for ordinary plugin code back
in September — *a plugin brings its own dependencies* — and it was still how compiled libraries got
where they were going, when they got there at all.

**They now travel with the plugin.** From the moment the plugin is built, through publication, onto
the registry's shelf, out again to every installation that asks for it, and onto disk in the place
the running system looks for it. That is four separate hand-overs, and the library used to fall out
at the second.

## What this changes for you

- **A plugin that needs a compiled library works on your installation** without your host having to
  happen to have it, and without anyone installing anything by hand.
- **A plugin that cannot deliver one is refused when it is published**, with a message naming the
  problem, rather than installed and left to fail later on your machine. The one failure this could
  not previously produce was a useful one.
- **Plugins built inside the platform's own build container can now declare these libraries at
  all.** They could not before: the build refused the dependency by name, which is why one plugin
  had to carry a workaround and rely on its host. That workaround can go.
- **Updates behave.** Two versions of a plugin that differ only in the compiled library they carry
  are now correctly two different versions. Before, they could resolve to the same one — and the
  second would quietly keep running the first one's library.

## What has not changed

A plugin built inside the build container carries the library for **one kind of machine** — the one
that container is built for — because that is the only machine it can see. The platform's own
publishing route carries every kind the library ships for. If a plugin is missing the right build
for your machine, it now says so in its build log instead of being silent about it.

And a library declared in a place the running system would never look for is still only **warned**
about at build time, not refused. Turning that warning into a refusal is a separate change, and it
waits on evidence that no plugin in the fleet is relying on it — arming it on faith would stop
publishing for everyone rather than stopping the drop.
