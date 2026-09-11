---
Name: Recompile keeps edits made during an earlier build
Category: Fix
Description: Recompiling a NodeType now checks the cached assembly's actual inputs, so a source or test edit cannot disappear behind an earlier build that finished later.
Icon: Code
Order: -20260911
---

# Recompile keeps edits made during an earlier build

An edit made while a NodeType was compiling could leave the next compile using the earlier code,
even though its source status appeared current. Cached assemblies now have to match the captured
source, test and configuration inputs before they are reused. Unchanged inputs still use the cache.
