---
Name: A short source read now explains itself
Category: Fix
Description: One boot resolved 91 fewer source files than its neighbours on the same portal and the same image, and nothing recorded enough to say why. The batched source discovery now reports, per query, how many result chunks it folded, what each contributed, and the largest gap between them — which is exactly what separates "the fold gave up too early" from "the providers returned less".
Icon: Search
Order: -20260909
---

Before it compiles anything, the platform pulls every source file in the mesh in one batched pass.
On 2026-09-08 one boot of a portal pulled **1145** files where the boots on either side of it —
three of them running the identical image — pulled 1236, 1237 and 1241. Content only grew across
that window, so the short one was a truncation.

The symptom has since stopped. The reason has not been found, and a fact that stops being visible
stops being investigated.

## Why it could not be answered

The pass folds the query's answer as it streams in, and decides the answer is complete after **one
second of silence**. It has no alternative: the result protocol carries no "this is the last of it"
marker, so a quiet window is the only completion signal any reader of it can use. A gap between
chunks wider than that window ends the fold early and hands the compiler a short list — silently.

But an equally good explanation is that the providers simply returned less, in which case the
window is innocent and the search belongs somewhere else entirely. Nothing recorded which.

## What it records now

Every source-discovery query reports, on completion:

- how many result chunks were folded, and how many items each carried;
- the **largest gap between consecutive chunks**, as a percentage of the completion window;
- the settled file count.

That is the discriminator, and both readings are printed on every pass:

- a largest gap approaching the window ⇒ the completion rule ended the fold early, and the line
  says so at warning level;
- all gaps small ⇒ the completion rule is exonerated and the shortfall is upstream, in what the
  providers actually returned.

The wait for the *first* chunk is deliberately not counted — that is the query's latency, not a gap
between chunks, and counting it would make every cold start accuse the fold.

## What it deliberately does not do

It does not widen the window, add a retry, or change what a short pass concludes — a short pass
already costs the batch rather than a rollout. Making a symptom rarer while its mechanism is
unmeasured is not a fix, and the fix waits on this number.
