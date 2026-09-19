---
Name: A stale listing is now read again before it stops a build
Category: Fix
Description: GitHub sometimes answers the question "which platform builds exist?" with a weeks-old page — with no error, and correct-looking content. The reader that picks a platform build was right to refuse such a page, but it refused on the first read, costing a whole CI cycle to recover. Where the page can be PROVEN old, it now reads it again, briefly, and only refuses if it is still old.
Icon: ArrowSync
Order: -20260919
---

# A stale listing is now read again before it stops a build

Before anything is compiled, a repository has to decide which platform build to compile against. It
asks GitHub for the list of recent platform builds and takes the newest one that is finished and
published.

Occasionally GitHub answers that question with an **old page**. Not a corrupted one and not an
error: an ordered, plausible, entirely well-formed list of runs, weeks out of date, returned with a
success code. Asked again minutes later, the same query from the same credential answers correctly.

Taking that page at face value would be the expensive mistake — it would pick a platform build from
days ago and report it as the newest, so every repository in the fleet would quietly compile and
test against an old platform. So the reader refuses instead, and that refusal is correct and stays.

## What was wrong with refusing immediately

The refusal ended the whole job, and 17 or 18 other jobs behind it. Recovering meant a person
noticing the red and pressing *Re-run all jobs* — which then went green, with nothing changed.

Measured on 2026-09-18, that happened **three times in one day**, and the third time it was `main`
itself that was left without a verdict — the branch whose result every open change in the
repository is measured against.

The refusal's own text said why this was avoidable: *a re-read minutes later was correct*.

## What happens now

Where the staleness can be **proven**, the page is read again — up to three times, on a short
backoff totalling about two minutes — and the job continues the moment a read comes back current.
If every re-read is still old, the same refusal follows, unchanged.

"Proven" is doing real work in that sentence. There are two ways a page can look old, and only one
of them is a fact:

- **It is missing a run that must exist.** A repository knows the platform build its own `main`
  last passed on, so a run at least that new definitely exists. A page without one cannot be
  current — there is no other explanation, and there is a precise condition to read *toward*.
- **Nothing on it is recent.** Platform builds normally appear at least hourly, so a page whose
  newest run is twelve hours old is *probably* stale. But a genuinely quiet platform produces that
  same page every single time, so reading it again would spend two minutes arriving at the same
  answer.

Only the first is re-read. The second refuses on the first read, exactly as before — and so does a
run that has been pinned to one specific platform build by hand, because that is an instruction
given during an incident and must not be made to wait.

Each re-read is reported on its own line, saying how many runs the page held and which was the
newest, so a reader can always tell *"it was read again and it cleared"* from *"the situation never
arose"*.
