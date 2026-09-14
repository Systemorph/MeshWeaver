---
Name: A portal hands its update to the control instance instead of patching itself
Category: Feature
Description: A portal on the fleet no longer changes its own workloads. It still notices a newer release and still applies its update policy, but instead of patching its own deployment it sends one signed notice to the control instance, which rolls it — unattended when the release is already the pinned one, behind an approval otherwise. The chart stops granting the self-patch right by default, and the Updates tab says where a release went.
Icon: ArrowSync
Order: -20260914
---

# A portal hands its update to the control instance instead of patching itself

Until now every portal on the fleet held a Kubernetes right to patch its own deployment, and used
it: a newer release appeared in the registry, the portal's own updater rolled it. That is one more
credential that can change the cluster, held by every portal — the opposite of the decision that
the only place to alter the cluster is the fleet's own operations lane.

**What changes.** A portal still detects a newer release — the registry watch, the update policy
(`Stable`, `Continuous` with a version pattern, `None`) and the release checks are untouched. What
it does with the release is new: it sends **one signed notice** to the control instance
(`self-update-available`, naming the portal's record, the image it runs and the image it selected),
and the control instance opens the roll. A roll to the release the fleet record already pins runs
unattended; a roll to anything newer waits for an approval in the mesh — so *Continuous* now means
"one approval per release", and moving the record's pin is what makes a roll unattended. A landed
module waiting for its activation restart is handed over the same way, as a restart the control
instance takes unattended.

**What an operator sees.** Settings → Updates now says *handed to the control instance at …* with
the release and the time, instead of *update available* for ever; *Apply available update now*
sends the same notice. The portal's own log says at start-up which of three states it is in —
`self-patch`, `control-lane` (and where it hands to), or `detect-only` naming the configuration key
that would make it a control-lane install. A hand-over the control instance refused is reported as
exactly that, with the pairing to check, and is retried by the next check.

**The chart.** The self-patch role is no longer rendered by default; a redeploy of a namespace
removes it. The same chart value (`selfUpdate.canPatch`) tells the portal not to try the patch, so
the right and the intent to use it can never disagree. Nothing changes on an instance until its
namespace is redeployed with the new chart — an image alone keeps behaving as before. A standalone
Kubernetes install with no control instance can keep the old behaviour by setting the value.

**What it needs on the control side.** The control instance has to turn the notice into a roll;
that half lives in the Hosting plugin and lands separately. Until it does, an instance switched to
the control lane announces its releases and nothing rolls them — visibly, on both ends.
