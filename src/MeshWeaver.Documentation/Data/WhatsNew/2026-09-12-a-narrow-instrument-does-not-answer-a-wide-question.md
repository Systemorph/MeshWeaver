---
Name: A narrow instrument does not answer a wide question
Category: Feature
Description: Reading CI Signals now names the failure mode most of its own rules are instances of - a correct instrument whose narrow answer gets generalised into a wide claim. Four measured cases from one session, the test to apply before publishing a number, and the tell for each family.
Icon: Sparkle
Order: -20260912
---

# A narrow instrument does not answer a wide question

[Reading CI Signals](/Doc/Architecture/ReadingCiSignals) collects rules for what a check's colour
actually means. Most of them turn out to be instances of a single shape that the page had never
named, and naming it is worth a section because the instances keep arriving in new costumes.

The shape: **a narrow instrument returns a correct answer, and the reader generalises it into a
claim the instrument was never measuring.** There is no bug to find and no error to notice — the
measurement is right and the reading is wrong, which is precisely why care does not catch this class
and a control does.

Five cases were measured in one session, while triaging two long-running issues. Four are CI
instruments; the fifth deliberately is not, because the shape is about how an answer is read:

- `pendingWork=0` on **one** stale-callback record, read as *"the pool is idle"* — which killed a
  live hypothesis. Maximum `pendingWork` in the same log is 606, and 2,501 across the run set.
- A count filtered to one gate stage, published as an occurrence count: 8 where the real figure was
  15, because seven siblings failed at a different checkpoint on the same mechanism.
- `gh api … 2>/dev/null` over eight repositories reporting every one clean, when every call was in
  fact being refused and the discarded stderr was the only place that said so.
- A quota endpoint reporting `5000/5000 remaining` while every call was refused — an honest meter,
  answering about the primary limit when the refusals were the secondary one.
- A GitHub App's **declared** permissions read as what a token can do. The App declares `issues` and
  `workflows`; the installation that mints the token carries neither.

The section gives the test to apply before a number becomes a verdict — *name the question the
instrument actually answers, then say why that is the same as the question you asked* — plus the
specific tell for each family: state the maximum and the count rather than the sample, state the
filter beside the number, check the exit code rather than suppressing it, and read the refusal
rather than the meter.
