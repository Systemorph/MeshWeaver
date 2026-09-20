---
Name: Incidental Findings — File It and Move On
Category: Architecture
Description: >-
  What to do with the defect you did not come for. Debugging one thing exposes another; the rule is
  to file an issue with the evidence you already hold and return to the task, because a finding that
  does not block you is a finding you must not follow. Where it applies, the three tests that decide,
  what a filed issue owes, and the two failure modes at either end.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M4 21V5a2 2 0 0 1 2-2h9l5 5v13"/><path d="M15 3v5h5"/><path d="M9 13h6M9 17h3"/></svg>
---

# Incidental Findings — File It and Move On

**Debugging one thing exposes another.** You are working out why a feature fails, and on the way you
find that some error was swallowed, a log line lies, a gate was never armed, a comment is four months
stale. It is a real defect. It is not the one you came for.

**The rule: file an issue carrying the evidence you already hold, and go back to the task.**

Not because the finding is unimportant — because **you are not blocked by it**. You are on the happy
path of a different problem, and the cost of turning aside is paid twice: the task you were on gets
abandoned mid-air, and the defect you turned to gets investigated with half the context and none of
the intent of whoever owns it.

## The three tests

Ask them in this order. The first *yes* decides.

1. **Does it block the task in hand?** If the thing genuinely stops you finishing — a gate refuses
   your change, a type will not compile, the data you must write cannot be written — then it is not
   incidental. Fix it, or fix enough of it to proceed, and say so.
2. **Is it actively harmful right now?** Data being corrupted, a credential exposed, a wrong answer
   being served to someone who will act on it. Stop and say so immediately, to a person. An issue is
   too slow a channel for something that is losing value while it is open.
3. **Otherwise it is incidental.** File it and return.

Most findings are the third kind, and the third kind is where judgement usually goes wrong — **in the
direction of doing too much**, not too little.

## What "file it" owes

An issue filed on the way past is only worth the evidence in it. **You will never again be as close
to this defect as you are right now**, and the person who picks it up starts from cold. So the issue
carries what you already have in front of you and costs you nothing to write down:

- **what you observed**, as instrument readings, with the exact identifiers — node paths, field
  values, timestamps, run ids, log lines quoted rather than characterised;
- **how you got there**, so it can be reproduced without re-deriving your route;
- **what you did NOT establish**, explicitly. An issue that overstates its own certainty sends the
  next person down the branch you had already half-excluded;
- **why it did not block you**, which is what tells a triager where it sits against everything else.

Then link it from wherever the work lands — the pull request, the design page — so the trail from
symptom to ticket survives the session.

🚨 **Do not file an issue you have not checked is real.** A stale checkout, a truncated query, a
cached read: each produces a confident finding about a defect that does not exist, and a wrong issue
costs more than no issue, because someone else pays to disprove it. Verify against the deployed
artifact, not a working copy, before you write it down.

## The two failure modes

**Turning aside** is the common one. The session ends with the incidental defect half-fixed, the
original task untouched, and no issue filed for either — so the next session inherits two unfinished
things and a description of neither. Scope creep is not thoroughness; it is the abandonment of a
commitment in favour of a more interesting one.

**Swallowing it** is the quieter one. The finding goes into a chat reply, a commit message body, or
nothing at all, and dies with the session. The next person to touch that code meets the same defect
from cold. A finding that lives only in a terminal is a finding nobody made — which is the same
argument as [conserving work products](../DocsFollowTheFunctionality), applied to defects rather than
designs.

## 🚨 An agent files into BUG TRIAGE, never a plain ticket

**Fleet-wide rule.** Do not open a GitHub issue yourself. File the finding as a `Feedback/Feedback`
node on the **control instance** (memex.systemorph.com); it reaches the **triage agent** and may
become a GitHub issue *from there*, in the owning repository.

**Why it is not merely a different button.** Triage decides the repository, the priority, and whether
the finding becomes a ticket at all. You are not placed to decide any of those — you have seen one
defect, not the queue it belongs in. A ticket an agent opens directly bypasses the pool and lands in
nobody's flow, which looks discharged and behaves exactly like swallowing it. The pooling is the
design: **one inbox per portal, never one queue per repository** (maintainer, 2026-09-12: *"we must
start pooling such connections, e.g. by portal"*).

**Agents skip the Draft stage.** The `/feedback` flow files a `Draft` and shows the author a preview
with a Submit button, because a human must be able to vet words being sent in their name. An agent
reporting its own finding has nothing to preview and usually no chat to preview it in, so it files
**`status: New`** — submitted, not yet triaged — and says so plainly rather than claiming a human
sent it.

## It feeds TRIAGE, not a pile

**Filing is not the end of the obligation — the finding has to enter the same queue as everything
else.** An issue that exists but is in nobody's flow is the quiet failure mode wearing a ticket
number: it looks discharged and behaves exactly like swallowing it.

The estate already pools this per portal rather than per repository: signed `ci-failure`, `ci-green`
and `feedback` events land in the control instance's one inbox and become a `Hosting/TriageItem`
under `Hosting/Triage/{kind}/{id}` plus ONE thread with the **triage** agent. Maintainer, 2026-09-12:
*"triaging has to be done by systemorph-com ⇒ communicate via mcp, open thread with triage agent. we
must start pooling such connections, e.g. by portal."*

So: **route the finding into triage**, and let triage decide priority and owner. What you must not do
is hold it as a private judgement about what matters — the whole reason you file rather than chase is
that *you are not the one placing it against the other work*.

## Where the issue goes



The repo that OWNS the code, not the repo you happen to be standing in. If you cannot tell, file it
where you found it and say what you are unsure about — a triager moving a ticket is cheap; a ticket
nobody filed is not.

Findings about a *running instance* rather than a repo — a stalled deployment, a held sync — belong
in an issue too, with the instrument readings that a later reader cannot reconstruct because the
instrument has since moved on.

## Related

- [Docs Follow The Functionality](../DocsFollowTheFunctionality) — the same instinct for designs and
  investigations: the durable form is a committed page, never a thread
- [Shared Rule Blocks](../SharedRuleBlocks) — how this rule reaches every repo's `AGENTS.md` identically
- [Why the Fleet Stopped Rolling Itself](../SelfUpdateFreeze) — a worked example of the evidence an
  incidental finding should carry, and of what a truncated query does to one
