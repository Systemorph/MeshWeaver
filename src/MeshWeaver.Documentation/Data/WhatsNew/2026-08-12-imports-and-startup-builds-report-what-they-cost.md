---
Name: Imports and startup builds report what they cost
Category: Feature
Description: The log lines for a content import and the startup node-type build now include how much memory the operation used, so growth can be attributed instead of guessed.
Icon: Sparkle
Order: -20260812
---

# Imports and startup builds report what they cost

When a portal's memory grows, the question is always which activity caused it — and until now the only
evidence was a chart of total memory for the whole process, read hours later. That is enough to see
that something grew and not enough to say what grew it, which is why one recent investigation had to
eliminate four candidate explanations one at a time from outside the process.

Two operations now report their own cost on the log line they already produced: importing the content
that ships with a release, and the startup build of the node types. Each says how much the managed
heap and the process grew while it ran. Nothing new is logged — the numbers are appended to existing
lines — and measuring is deliberately cheap, so it does not change what it measures.

The two figures are worth reading together: memory that leaves the managed heap without leaving the
process is a different problem from memory the program is still holding, and previously that
distinction was invisible from inside.
