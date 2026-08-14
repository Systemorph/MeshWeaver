---
Name: What a NodeType compile costs, per type
Category: Feature
Description: Startup now reports what each dynamic NodeType's compile cost in wall-clock time and how large its compile unit was, and the warm-up summary names the five costliest types — so a compile unit that is growing is visible long before it reaches a deadline.
Icon: TopSpeed
Order: -20260813
---

# What a NodeType compile costs, per type

A dynamic NodeType's compile used to produce exactly one signal: a startup deadline that was
either met or not. That is enough to tell you something went wrong and nothing at all about what.
When one compile unit grew large enough to start brushing that deadline, the only question anyone
could actually answer was "should the deadline be bigger" — which is the one answer that makes the
next occurrence quieter instead of rarer.

Startup now reports, for every type it builds, how long that build took and how much source went
into it:

```
BatchBake: Store/Plugin → Compiled — 3.4 s over 72 file(s), 1399 KB — managed +21 MB, working set +38 MB
```

Both numbers matter, and they matter together: a type whose duration grows while its unit does not
is a different problem from one where both grow, and until now neither was visible. The warm-up's
closing summary names the five costliest types, so "where did startup's time go" is one line rather
than an investigation. And the start of a compile is now recorded on the type itself on every path,
so the duration can be read back from the mesh later, not only from a log that may have rotated.

None of this adds log volume — the figures are appended to lines that already existed, next to the
memory measurement they belong beside.

The first thing it showed was that compilation was not where the time went. Timed across a whole
plugin repository, the largest compile unit — 1.4 MB of C# in one type — costs about 26 ms more
than the smallest one, which is a two-kilobyte file. Splitting a large unit up would not have
helped, and would have cost more; that is now something anyone can check in a log line instead of
having to take on trust.
