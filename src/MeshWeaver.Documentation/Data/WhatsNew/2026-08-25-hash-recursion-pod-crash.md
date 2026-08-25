---
Name: Portal no longer crashes on a self-referencing data graph
Category: Fix
Description: Fixed an unbounded hash-code recursion that could kill a portal instance outright, dropping every open page on it.
Icon: Sparkle
Order: -20260825
---

# Portal no longer crashes on a self-referencing data graph

Two places in the data layer computed an object's hash code by walking everything it
contained. When the data pointed back at itself — which happens routinely once a
synchronization stream or an entity store is involved — that walk never finished and the
portal process died on the spot, taking every page open on that instance with it.

Streams and their configuration now compare and hash by identity, which is what they always
meant: two live streams are two different streams, never two copies of the same value. Stores
and collections hash from their keys and sizes instead of from the arbitrary objects they
carry, so the cost no longer grows with the size of your workspace either.

A related crash is fixed at the same time: an empty store used to throw when something asked
for its hash code.
