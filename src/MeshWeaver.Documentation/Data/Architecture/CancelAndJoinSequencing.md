---
Name: Cancel and Join Are Two Questions
Category: Architecture
Description: A deadline that asks work to stop and a deadline that waits for it to have stopped must not share one clock — the pattern behind a class of flake that reports cooperative work as abandoned.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="9"/><path d="M12 7v5l3 2"/><path d="M4.5 4.5l15 15"/></svg>
---

# Cancel and Join Are Two Questions

Wherever this estate bounds work it does not own — a test case on its own thread, a leaf in
[Controlled I/O Pooling](../ControlledIoPooling), a hub being torn down — the same two deadlines
appear:

| Question | Instrument |
|---|---|
| Did the work finish inside its budget? | a clock, or a wait with a timeout |
| Having been ASKED to stop, did it actually stop? | a join on real termination |

**They must be answered in sequence, on two clocks. A single deadline that both trips the
cancellation and gives up waiting is a race, and it is the work's own unwind that loses it.**

## The failure shape

The runner arms the token to fire a little before it stops waiting, so the two do not fire at the
same instant:

```csharp
// ❌ two deadlines, one clock — the unwind gets only `lead`
budget.CancelAfter(timeout - lead);
if (!thread.Join(timeout))
    return "did not return within Ns — a hung case; the thread is abandoned";
```

`lead` looks like a safety margin. It is in fact **the entire allowance for terminating**: waking
from the wait, throwing, unwinding through reflection, running the case's own `finally`s, disposing
a `[ThreadStatic]` context, and the OS reaping the thread all have to fit inside it. When the
machine is loaded — which is exactly when the budget expires at all — they do not, and the work is
reported with the *opposite* verdict to the truth: cooperative work is declared abandoned.

The two verdicts are not interchangeable. "Ended on its token" means the thread is gone; "abandoned"
means the process carried a live thread on. A caller that treats them alike has stopped measuring
the thing it exists to measure.

## What made the allowance shrink to nothing

The instructive part of the measured instance (`StaticTestRunner`, issue #3442) is that nobody chose
a 200 ms unwind allowance. It was inherited, when the completion signal was made **stricter**:

| | signal | what "done" meant |
|---|---|---|
| before | an event `Set()` from the case's `finally` | the body finished unwinding |
| after (#2792) | `Thread.Join(timeout)` | the thread actually TERMINATED |

`Join` is the better primitive — an event fired from a `finally` signals before the thread has
terminated, whereas `Join` returns only on real termination, and that is what gives a field written
on the worker its happens-before. But swapping it in moved the finish line *later* without moving
the deadline, and the slack that had covered thread teardown silently became zero.

🚨 **When you tighten what a signal means, re-derive every deadline measured against it.** A
strictly better primitive can strictly worsen a race.

## The shape that is correct

Measure the budget. Then ask. Then allow the unwind a window of its own, which begins at the ask:

```csharp
// ✅ two questions, in sequence
var overBudget = false;
if (!thread.Join(timeout))                 // 1. is the budget spent? MEASURED, not predicted
{
    overBudget = true;
    AskToStop(budget, name, capture);      // 2. only now — see "the ask goes on its own thread"
    if (!thread.Join(UnwindGrace))         // 3. having been asked, did it stop?
        return Abandoned(...);             //    …no: the thread is genuinely hung
}
```

Three properties follow, and each of them is the point:

- **The ask happens-before the window.** Nothing has to be predicted about how long unwinding takes
  relative to a budget it has nothing to do with.
- **`overBudget` is exact.** "This runner asked, and the work answered with an
  `OperationCanceledException`" is a fact known here, not inferred from
  `IsCancellationRequested` racing a thread that may not have observed anything.
- **The tuning knob disappears.** There is no `lead` to size. `UnwindGrace` is an *allowance*, not
  headroom: widening it would not have fixed the race, and it is not sized to make anything pass.

## The ask goes on its own thread

`CancellationTokenSource.Cancel()` runs the token's registrations **synchronously on whoever calls
it**, and those registrations belong to the work being cancelled — a `Task` continuation, an Rx
subscription's disposal, a wait's cleanup. Calling it from the supervising thread lets the
supervised code run on, or park, the very thread whose job is to declare that work abandoned.

That is not hypothetical here: `IoPool.Drain` cancels off the caller's thread for exactly this
reason and reports a named residual when the cancel itself never returns (see
[Controlled I/O Pooling](../ControlledIoPooling) → *"A residual with NO site is the CANCEL join"*).
A dedicated thread is also immune to thread-pool starvation, which is the condition the machine is
in whenever this path is reached.

```csharp
new Thread(() =>
{
    try { budget.Cancel(); }
    catch (Exception ex) { capture($"the cancellation callback threw: {Innermost(ex)}"); }
})
{ IsBackground = true, Name = $"cancel:{name}" }.Start();
```

The `catch` is **surfacing, not swallowing** — `Cancel()` aggregates whatever the registrations
threw, and an exception left unhandled on a bare `Thread` takes the process down.

🚨 **Do not then dispose the `CancellationTokenSource`.** On the path where an ask was issued, the
canceller may still be inside `Cancel()` and the abandoned worker still holds the token; disposing
under either is a use-after-dispose, which in this estate surfaces as an
[exit-139 nobody can reproduce](../DebuggingNativeCrashes). Dispose only on the path where the work
finished inside its budget and no ask was ever made.

## Reproducing a race like this deterministically

A race you cannot provoke is a race you have not understood, and re-running until it fails is not an
experiment. Here the mechanism names the stimulus directly: the window is too small for the unwind,
so **make the unwind cost time on purpose** and the failure becomes deterministic.

```csharp
public static void EndsOnItsBudgetButTakesTimeToDie()
{
    try
    {
        TestContext.Current.CancellationToken.WaitHandle.WaitOne();
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
    }
    finally
    {
        Thread.Sleep(500);   // stands in for the scheduling delay a loaded runner imposes
    }
}
```

A 500 ms unwind against a 200 ms allowance fails every time; against a whole allowance it passes
with seconds to spare, so the same assertion is both the falsification arm and the shipped
regression test. The `Thread.Sleep` is the **stimulus**, not a wait for a condition — the distinction
that separates it from the sleeps [Writing Tests](../WritingTests) forbids.

## Checklist

Reach for this page whenever a cancellation and a wait are aimed at the same work:

- Is the deadline that trips the token the *same* deadline the waiter gives up on? Sequence them.
- Does anything measure "how long the work has to react" as a fraction of its budget? That number
  is an artefact; give the reaction its own allowance.
- Did the completion signal recently get stricter (an event → a join, a callback → a termination)?
  Re-derive the deadlines measured against it.
- Is `Cancel()` called from the thread that must survive to report the outcome? Move it.
- Is the token source disposed on a path where something might still hold it?

## See also

- [Controlled I/O Pooling](../ControlledIoPooling) — the drain's cancel join, and the residual it
  names when the cancel itself does not return
- [In-Mesh Build and Test](../InMeshBuildAndTest) — the static lane whose case budget this pattern
  came from
- [Writing Tests](../WritingTests) — waiting on conditions rather than on the clock
- [Removing Hand-Woven Gates](../RemovingHandWovenGates) — why `Thread.Join`, and not an event, is
  the primitive for "did that thread finish"
