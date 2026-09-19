---
Name: A pod shutting down is no longer an error on the second path either
Category: Fix
Description: Two weeks ago a portal stopped treating one expected condition as a failure — a node waking up on a pod that is already stopping. It turned out the platform had two places that report that, and only one of them was told. The other kept raising the alarm, and because alarms are grouped by the fault rather than by who reported it, the ticket the first fix closed kept reopening. Both now agree.
Icon: Checkmark
Order: -20260919
---

# A pod shutting down is no longer an error on the second path either

When a portal replaces its pods, some nodes are inevitably waking up on a pod that is already on its
way out. The work is abandoned, nothing is written, and the next person to open that node gets it from
a live pod. It is the most ordinary thing that happens during a deployment.

Two weeks ago the platform stopped reporting that as a failure. Before, it could not tell the
difference between *"this pod is going away"* and *"this node's configuration is broken"* — one
message named both possibilities and committed to neither — so every rollout raised an alarm about a
condition the message itself described as expected.

**That fix was correct and incomplete, in a way worth writing down.** A node waking up runs through
two steps, and each has its own place to report a problem. Only one of them was taught the
difference. The other went on reporting every failure at alarm level, including the shutdown.

What makes that more than a missed line is **how alarms are grouped**. They are deliberately grouped
by *the fault*, not by *who reported it* — so that one problem printed by two different parts of the
platform raises one alarm instead of two. That is the right rule, and it has a consequence nobody had
drawn out: **quietening one reporter changes nothing while another still shouts.** The alarm the first
fix closed reopened a fortnight later, reporting the same shutdown from the second step, and looked
for all the world like the fix having come undone.

Both steps now agree: a node abandoned because its pod is tearing down is recorded as the routine
event it is, and says so in a sentence instead of listing possibilities.

**Nothing became quieter than that.** A node that genuinely cannot start — a configuration that
throws, a node that never resolves, a step that times out — still raises the alarm, with the same
wording as before, and with no change at all to what actually happens: the failure is still recorded,
still reported to whoever was waiting, and the node still tries again on the next access. The only
thing that changed is whether a deployment doing its job wakes somebody up.

The general lesson is recorded with the machinery: when an expected condition is downgraded, the
thing that keeps the alarm alive is the reporter nobody thought to look for.
