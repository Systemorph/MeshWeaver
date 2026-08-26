---
Name: A refused build stops retrying itself
Category: Fix
Description: On a mesh that requires prebuilt assemblies, a type refused for lack of a bundle used to burn one automatic retry attempt it never needed — a second trip through the same refusal, for nothing. The refusal now records what it was decided under, so the automatic retry recognises it was already answered.
Icon: Sparkle
Order: -20260826
---

# A refused build stops retrying itself

`Modules:RequirePrebuilt` parks a type instead of compiling it when no prebuilt assembly has landed
for it — a named, bounded refusal instead of a silent Roslyn pass. The framework also carries a
separate, general recovery: a type sitting at a build failure with nothing recorded about what it was
tried under gets exactly one automatic retry, so a fix that ships later can still reach it without
anyone pressing a button.

The two mechanisms shared a gap. A require-prebuilt refusal never recorded what it was decided under,
so the recovery mechanism read every refusal as "never attempted" — and drove its one automatic retry
on a type that had, in fact, just been correctly and deliberately refused. The retry landed back on
the same refusal a moment later, spending part of a budget meant for genuine recoveries on a refusal
that needed none.

## What changed

A require-prebuilt refusal now records the inputs — the framework build, the installed modules, the
source set — it was decided under, in the same write that settles it. The recovery mechanism compares
against that record and correctly finds nothing to retry, because nothing about the refusal has
changed since it was made.

## What you will notice

A type refused for lack of a bundle settles once and stays settled — no extra round through the
compile pipeline, and the automatic-retry budget is left for the failures that actually need it.
