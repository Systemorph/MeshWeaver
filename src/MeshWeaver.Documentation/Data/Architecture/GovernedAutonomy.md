---
Name: Governed Autonomy — Do, File, Govern, Ask
Category: Architecture
Description: >-
  How an instruction becomes work. Four outcomes — do it, file it, govern it, ask — and the test that
  picks between them, which is reversibility rather than importance. What each owes back, why the
  default is DO, and the two ways of getting the line wrong.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M12 3v4"/><circle cx="12" cy="9" r="2"/><path d="M5 21v-3a3 3 0 0 1 3-3h8a3 3 0 0 1 3 3v3"/><path d="M8 15V9m8 6V9"/></svg>
---

# Governed Autonomy — Do, File, Govern, Ask

**An instruction arrives. There are four honest responses, and exactly one test picks between them.**

This is not a proposal; it is what working together already looks like when it goes well. Writing it
down matters because the discipline is harder than the idea, and because the failure modes are quiet.

## The four outcomes

| | When | What it owes back |
|---|---|---|
| **Do it** | Reversible, and inside the work | Evidence — run ids, counts, the instrument reading |
| **File it** | Real, but not this task, and not blocking | A triage item with what was and was not established |
| **Govern it** | Changes a running system, or is hard to undo | A prepared action with a preview, for one approval |
| **Ask** | The decision is genuinely the other party's | A recommendation with its reasoning — not a menu |

### Do it — the default, and it should stay the default

Write the code, write the tests, open the pull request, answer the review, merge, write the
documentation. No permission is asked because none is needed: the work is reviewable and revertible.

What is owed back is **evidence, not adjectives**. "19/19 node types compile, 173 tests pass, run
34…" is a report. "Done" and "should be fine" are not, and neither is a green tick nobody read the
log behind. A claim about a deployed system is only as good as the artifact it was read from — a
stale checkout, a cached point-read and a truncated query each produce confident nonsense.

🚨 **Asking permission for reversible work is not caution — it is unfinished work handed back.** The
cost lands on the person who has to re-acquire the context in order to say yes.

### File it — the finding you did not come for

A real defect discovered on the way that does not block the task goes to triage, and the task
continues. Full rule, including what a filed finding owes and why turning aside costs twice:
[Incidental Findings](../IncidentalFindings).

The part that belongs here: **triage decides the repository and the priority, not the finder.** You
have seen one defect, not the queue it belongs in.

### Govern it — irreversible, so it gets a witness

Rolling an instance onto a new image. Retyping live partition roots. Deleting a Space. Dropping data.
Anything whose inverse is expensive or absent.

The shape is always the same: **prepare the action with a preview of exactly what it will do, and let
a person approve it.** An approval that arrives with the plan in front of it costs one click and
produces an audit trail as a by-product. The platform already has the vehicles — an
`Essentials/OperationRequest`, a `Hosting/InstanceAction` with its plan step — so this is choosing the
right one, not building ceremony.

Two rules inside this branch, both learned the hard way:

- **The preview is not the result.** Read the outcome back from the node afterwards and report what
  it says, never what was expected.
- **A governed vehicle does not make an unverified change safe.** If the thing being applied has not
  been checked — an unverified release candidate, a migration whose before-image was never captured —
  the approval is a signature on an unknown.

### Ask — only where the answer is not derivable

Naming. Which members a dimension starts with. Whether a category carries deals. Priorities and
trade-offs with no correct answer.

Bring **a recommendation and the evidence for it**, not a survey of options. A question of the form
"here are four possibilities, what do you think?" pushes the work back. A question of the form "I
propose X, because the module already uses that word for something else — confirm?" is answered in
seconds.

Everything derivable from the code, the docs or the data is **not** a question. Go and read it.

## The test: reversibility, not importance

**The gate is how hard it is to undo, never how big or how interesting it is.**

- A large refactor that is fully revertible → **do it**.
- A one-line change to a live deployment record → **govern it**.

Getting this backwards is the failure, and it fails in both directions. Treating reversible work as
dangerous produces a stream of permission requests and nothing finished. Treating irreversible work
as routine is how data is lost — and it is the direction where being wrong is unrecoverable, which is
why the asymmetry is deliberate.

When genuinely unsure, ask which it is. That question is cheap; the wrong guess on the irreversible
side is not.

## The two quiet failures

**Turning aside.** An instruction is being carried out, something more interesting appears, and the
session ends with both things half-done and neither described. Scope creep is not thoroughness; it is
abandoning a commitment for a more appealing one.

**Reporting the expectation.** Saying a thing is fixed because the symptom disappeared from an
indirect signal, rather than because the direct one was read. If the proof has not arrived yet, the
honest report is "the fix is in, the proof is pending, here is what would disprove it".

## Related

- [Incidental Findings](../IncidentalFindings) — the *file it* branch in full
- [Specifying Software](../SpecifyingSoftware) — what a well-posed instruction looks like from the other side
