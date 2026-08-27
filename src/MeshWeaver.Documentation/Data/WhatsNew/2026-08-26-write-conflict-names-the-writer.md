---
Name: A write conflict now says whose row won
Category: Fix
Description: The write-conflict record and its warning now identify the row that won, so you can tell which component wrote it.
Icon: Sparkle
Order: -20260826
---

# A write conflict now says whose row won

When two components write the same node and one of them loses, the platform keeps the newer row,
merges what it can, and records what it had to drop. That record used to say *what* was dropped but
nothing about *who* had written the row that won — so tracking the other writer down meant guessing.

Both the warning and the durable record now describe the winning row, including where its content
came from. A row copied into place from somewhere else in the mesh names its source directly, which
is usually enough to identify the component that wrote it on sight.
