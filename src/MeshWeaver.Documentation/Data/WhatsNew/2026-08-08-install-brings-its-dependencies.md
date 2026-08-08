---
Name: Installing a plugin brings its dependencies with it
Category: What's New
Description: Clicking Install on a plugin that depends on another now installs the dependency first, in declared order, instead of failing with a NodeType path you never asked about.
Icon: Box
---

A plugin can declare what it needs — `requires: ["Store@^1.0.0"]` on its root — and clicking
Install now honours that declaration. The catalog resolves the package's full dependency closure,
skips whatever is already installed, and installs the rest one at a time, dependencies first. One
click, in the right order.

Previously only the unattended first-boot install derived that order. A person clicking Install got
exactly the one package they clicked, so installing a dependent before its dependency failed
outright — and the message named a NodeType path from a package the clicker had never heard of
("NodeType(s) not registered: Training/Tour"), because the installer refuses any node whose type is
not on the mesh yet. Whether an install worked came down to guessing the right order by hand.

A declared cycle is now refused up front with the loop spelled out (`A → B → A`) instead of being
installed in an arbitrary order and failing later somewhere unrecognisable. The first-boot pass
deliberately keeps its old, forgiving behaviour — nobody is present at startup to fix a malformed
repo, and one bad package must not strand a whole instance — so it warns and still installs every
package exactly once.

Dependencies the catalog does not offer stay non-blocking: the instance may simply not be granted
them, so the install proceeds and the installer's own refusal remains the accurate error if the
package really cannot work without one.
