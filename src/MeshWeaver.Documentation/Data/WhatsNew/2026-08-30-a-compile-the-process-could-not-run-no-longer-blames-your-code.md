---
Name: A compile the process could not run no longer blames your code
Category: Fix
Description: When Roslyn's emit breaks process-wide, every later compile threw — and each one was recorded as "your code is broken", which also stopped anything from ever retrying it. Those aborts are now recorded as not-evaluated, so the type recovers on the next healthy process instead of staying stuck until someone presses Compile.
Icon: Bug
Order: -20260830
---

# A compile the process could not run no longer blames your code

Very occasionally a portal reaches a state where the C# compiler can no longer **emit** an assembly
at all. Every NodeType compiled after that point failed — and each one was written down as
`Error`, *"your code did not compile"*, even though nothing had looked at the code.

That verdict was sticky. A failed compile also records **which inputs it was formed from** (the
framework, the installed modules, the type's own sources), and the automatic retry deliberately
declines when none of those have moved — because re-running an identical compile would produce an
identical failure. For a fault that had nothing to do with the inputs, that reasoning was simply
wrong: the type sat at `Error` reporting an error its source never caused, and **nothing retried
it** — not a restart, not a redeploy — until someone opened it and pressed **Compile**.

## What changed

When a compile aborts inside the emit, the compiler now re-runs a tiny, known-good control
compilation on the spot. If that one cannot emit either, the failure is recorded as
**not evaluated** rather than as a verdict:

- the NodeType is left **Unavailable** — *"the compile state could not be determined; nothing is
  known to be wrong with the source"* — instead of `Error`;
- no verdict and no input fingerprint are written, so the type is **re-driven automatically** and a
  later, healthy process compiles it normally;
- the log says so in one line, naming the condition and stating that every later compile in that
  process will fail the same way — so the failures that follow are read as one event instead of a
  dozen unrelated ones.

If that control compilation **succeeds**, the fault really is specific to the code being compiled,
and it is still recorded as `Error` exactly as before. Only a measured, process-wide emit failure
takes the new path — an infrastructure fault that has *not* been shown to be process-wide is still
a full verdict, so nothing that should be caught can slip through as "not evaluated".

## What this does not change

The underlying emit failure is a fault below the framework, in the .NET runtime, and this does not
fix it — a portal in that state still cannot compile anything until it restarts. What it fixes is
the damage it used to leave behind: NodeTypes permanently marked broken, on evidence that was never
gathered.
