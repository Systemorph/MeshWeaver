---
Name: A compile that hangs now fails instead of spinning forever
Category: What's New
Description: Every stage of a NodeType compile is now time-bounded, so a stuck compile ends with an error naming the stage that hung — and the compile tool tells you when one is already running.
Icon: Sparkle
---

# A compile that hangs now fails instead of spinning forever

A NodeType compile runs through several stages — reading your sources, restoring
any NuGet packages they reference, running the compiler, loading the result,
publishing it. If one of those stages never answered (an unreachable package
feed, a wedged storage endpoint), the NodeType stayed at "Compiling" forever:
no error, no result, and pressing Compile again did nothing, because the status
itself is what stops two compiles running at once.

Every stage now has a deadline. A stage that stops answering ends the compile
with an error that names it, so you can see what actually went wrong and retry
once it is fixed. Publishing the compiled assembly is the one exception by
design: it never fails a compile, so a slow storage endpoint now finishes with a
note on the compile log instead of holding the whole build.

The `compile` and `get_diagnostics` tools also stopped guessing. Asking to
compile a NodeType whose compile is already running answers immediately —
"already compiling", with the running activity to watch and how long it has been
going — instead of waiting out a minute and reporting a state it never checked.
`get_diagnostics` reports a compile in progress as exactly that, rather than
claiming the NodeType has no definition.
