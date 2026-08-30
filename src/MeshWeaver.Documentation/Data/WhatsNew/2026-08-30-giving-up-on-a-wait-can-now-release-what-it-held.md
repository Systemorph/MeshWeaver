---
Name: Giving up on a wait can now release what it held
Category: Fix
Description: Cancelling a reactive wait settled the caller but left the work running, so a caller that had already given up kept its I/O-pool permit for the full duration of work nobody was waiting for — invisibly, with nothing faulting or logging.
Icon: PlugDisconnected
Order: -20260830
---

# Giving up on a wait can now release what it held

The framework's one sanctioned bridge from a reactive signal to a `Task` deliberately keeps its
subscription attached after the wait ends. That is not an accident: it is what lets a fault arriving
*late* still be reported instead of vanishing as an unobserved exception, and it is the reason the
bridge exists rather than Rx's own `ToTask()`.

But it quietly changed what **cancellation** means for every call site converted from `.ToTask(ct)`,
which did dispose. Rx cancels a pooled operation's token when the subscription is disposed — so
under the new default, a caller that had already given up kept its I/O-pool permit for as long as
the abandoned work took to finish.

That failure has no symptom of its own. Nothing faults, nothing logs, no error appears anywhere: the
slot is simply not available to the next caller. Under load that is the shape that turns into a
stall, and there is nothing to grep for.

Waiting now takes an explicit position on it. The default is unchanged — a late fault is still
reported, everywhere, and that stays the default deliberately. A caller whose wait owns a bounded
resource can opt in, and cancelling then reaches the source and releases it.

The two places in the platform that pass a real cancellation token — both building a data source's
initial store, which fans out across every type source through the I/O pool — now opt in. Cancelling
that initialization used to leave the fan-out running against a store nobody would ever read.
