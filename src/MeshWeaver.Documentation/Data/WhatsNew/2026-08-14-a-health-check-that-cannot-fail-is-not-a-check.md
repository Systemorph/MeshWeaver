---
Name: A health check that could not fail
Category: Fix
Description: Asking for a code type's problems when the type does not exist reported a clean bill of health. The pre-release sweep that relies on it could pass having checked nothing.
Icon: ShieldError
Order: -20260814
---

# A health check that could not fail

Before a release, every code type in the mesh is swept for compile problems. A type left broken
refuses to start, and takes the pages that use it down with it — so the sweep is a gate, not a
formality.

The tool it runs on answered **"clean, no problems"** in three situations where it had not looked at
anything at all:

- the type had been **renamed**, so the path in the sweep list no longer pointed anywhere;
- the path was **mistyped**;
- the type lives in a partition the answering replica does not hold, or its owner **did not respond**
  in time.

In each case the honest answer is *"I could not check this"*, and in each case it said the same
thing a genuinely healthy type says: an empty list of problems. A sweep over a stale list could
report every entry green having verified none of them — and the more wrong the list, the greener the
result.

Asking about a type now returns **what happened as well as what was found**: it compiled, or the
path is not there, or the node is not the kind of thing that compiles, or it could not be reached —
with the path named in the reason so a sweep says *which* entry it could not check. Only the first
of those can be clean.

The distinction was never missing from the platform; the read underneath already separated "not
there" from "did not answer". It was being discarded one layer up.
