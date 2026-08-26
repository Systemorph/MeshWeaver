---
Name: Finished answers no longer stick on "Generating response…"
Category: Fix
Description: Under load, a chat message could finish with its answer written but still show "Generating response…" forever — and a streaming answer could freeze on its first few words. Consecutive writes to the same node now build on each other instead of on a view of it that is one write out of date, so the last thing written is what you see.
Icon: Sparkle
Order: -20260826
---

# Finished answers no longer stick on "Generating response…"

When an agent answered you, the message could end up finished and empty of the answer: the bubble
still reading *"Generating response…"*, with nothing to indicate anything had gone wrong. The round
had completed correctly — the answer existed — but the message never showed it. From the outside it
was indistinguishable from an agent that had hung, and waiting or reloading did not help.

The same fault had a quieter form: a streaming answer that stopped growing after its first words
while the rest of the reply was being written. Both got more likely the busier the server was.

## Why it happened

Everything written to a node — each chunk of a streaming reply, then the final answer — goes through
one ordered queue, and each write is sent as a small description of what changed relative to the
version the writer last saw. The queue did not wait for confirmation that a write had been stored
before starting the next one, and the next one read the node's last *confirmed* state. Under load
that state was one write behind, so a write described its change relative to a version that had
already moved on — by its own hand, a moment earlier.

The owner of the node is right to reject a change described against an out-of-date version: that is
what stops two people editing at once from overwriting each other. But here there was only ever one
writer, in order. So the final answer's text was rejected as a conflict with itself, while the rest
of the same write — the "finished" marker, the usage figures — was accepted. One write, two
outcomes, and no error anywhere: hence a finished message with no answer in it.

## What changed

Each write now builds on what the previous write in the same queue actually produced, rather than on
a view of the node that has not caught up yet. The instant the node carries anything newer — a
confirmation, or a change from a genuinely different writer — that newer state takes over again, so
real conflicts between real concurrent editors are still detected and resolved exactly as before.

## What you will notice

A message that says it is finished shows its answer. Streaming replies keep growing to the end,
including on a loaded server.
