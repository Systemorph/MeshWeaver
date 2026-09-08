---
Name: A short read no longer condemns a healthy node type
Category: Fix
Description: When a portal starts, it looks up the code belonging to every node type it hosts before compiling them. If that lookup came back short, the compiler was handed nothing for some types and reported them as broken — so a healthy portal refused to take traffic for three hours before restarting itself. The lookup now checks its own answer against what the mesh records, and asks again instead of condemning the type.
Icon: Checkmark
Order: -20260908
---

# A short read no longer condemns a healthy node type

Every portal, on startup, looks up the source files belonging to each of its node types and compiles
them. On 2026-09-08 one boot of memex.systemorph.com got a short answer from that lookup: it found
1145 code files where the boots before and after it — three of them running the very same image —
each found more than 1236.

The four node types whose files were in the missing part were handed to the compiler with nothing to
compile, and the compiler said what it honestly saw: types that do not exist, names that are not in
scope. Those messages are indistinguishable from a genuine breakage, so the portal concluded its new
image had broken four node types and did the right thing with that conclusion — it refused to take
traffic, kept the previous pods serving, and stalled the rollout. Nothing broken was ever served.
But nothing was actually wrong, and because nothing was wrong there was also nothing to change that
would clear the verdict. The pod sat out of rotation until its startup budget of three hours ran out
and it was restarted, at which point the same image started cleanly.

The lookup now asks a second question before it accepts an empty answer. Each node type already
records how many source files it has, so "I found none" can be checked against "the mesh says there
are two". When those disagree, the pass reports that it could not establish the source set at all,
and the portal falls back to resolving each type on its own — slower, and correct — instead of
turning a short read into a verdict about the code. A node type that genuinely owns no source files
still records that fact, and still classifies as content rather than as a broken image, exactly as
before.
