---
Name: Model picker works in the React frontend
Category: Fix
Description: The React chat model dropdown was always empty; it now lists the models your deployment offers.
Icon: Sparkle
Order: -20260810
---

# Model picker works in the React frontend

The model dropdown in the React chat never showed anything. It asked the mesh for
a kind of node that does not exist, so the answer was always empty and the picker
fell back to its "nothing to choose from" state — even on deployments with several
models configured and working.

It now asks for the same node type the main portal uses, so the dropdown lists the
models available to you and remembers your pick for the next round.
