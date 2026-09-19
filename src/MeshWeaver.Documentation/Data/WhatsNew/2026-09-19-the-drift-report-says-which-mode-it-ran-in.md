---
Name: The chart-drift report says which mode it ran in, so two environments' counts can be compared
Category: Fix
Description: The check that asks whether a deployment matches the chart describing it recognises the patch an environment applies after its install — but only when that environment has committed one, and it never said which of the two it had done. A count of divergences was therefore not comparable between environments, and nobody could tell whether expected differences were among them. The run now states it, in both cases.
Icon: Checkmark
Order: -20260919
---

# The chart-drift report says which mode it ran in

One scheduled check asks a question nothing else in the platform asks: **does the deployment actually
run what the chart describes?** It renders each environment's own committed overlay, reads the live
objects, and reports every field where the two disagree — classified, because the classes mean very
different things. One of them is live non-determinism that can decide a value differently on each
start; another is a value that renders in the chart, is read by everyone reviewing it, and is used by
no running process.

Some environments apply a patch to their deployment *after* the install finishes. The check knows
about that: hand it the patch and the additions it makes count as **declared** rather than as drift.
Hand it nothing and those additions are reported — which is the deliberate choice, and the right one.
Reporting a difference somebody expected is a nuisance; hiding one nobody expected is the failure the
check exists to prevent.

**What was missing is that the run never said which of those two it had just done.** The branch that
found no patch file did its work in silence. So a report of thirty-one divergences left two questions
unanswerable from the report itself: whether any of the thirty-one were the expected post-install
additions, and whether that count meant the same thing as the count from the environment next to it.
An operator comparing two environments was comparing two numbers produced under rules they could not
see — and the same report already fails loudly, by name, when the *other* input it needs is absent.

**Both branches now say what they did.** A declared patch is named. An absent one is reported as a
notice that says the additions will appear below as drift, that this is deliberate rather than a
fallback, and — the part that actually matters — that this environment's count is **not** comparable
with one from an environment that does declare a patch.

Nothing about what the check enforces has changed. It still runs unconditionally, still refuses to
render against chart defaults, still treats an unreadable cluster as a failure rather than as
"no drift", and still reports exactly the same divergences it did before. The difference is that the
report can now be read without knowing which files happened to be present when it ran.
