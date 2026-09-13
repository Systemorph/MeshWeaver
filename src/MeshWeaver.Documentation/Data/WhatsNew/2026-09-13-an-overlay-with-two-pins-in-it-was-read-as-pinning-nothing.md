---
Name: An overlay with two pins in it was read as pinning nothing
Category: Fix
Description: Image protection refused a live installation because its images moved to the fleet's own registry, and said the reason was that its configuration pinned no image at all - which was false. It now names the other registry, refuses only when that is undeclared, and counts a declared one on its own line instead of holding every other installation's protection hostage.
Icon: ShieldLock
Order: -20260913
---

# An overlay with two pins in it was read as pinning nothing

The nightly job that protects images from deletion works out what the fleet is still using and then
locks exactly that. It refuses rather than protecting less — an installation it cannot account for
stops the whole run, on purpose, because quietly protecting fewer things is how the last outage
happened.

On 13 September it refused, and the reason it gave was wrong:

> installation `build` runs core c84c6c0 and its configuration pins no image at all, so there is no
> repository in which to protect what it runs.

That configuration pins **two** images. They are simply in a different registry — the fleet's own,
rather than the one this job locks. The reader is a false sentence pointing at a component that is
working perfectly, and the fix it suggests is to go and repair a matcher that has no defect.

## Why a wrong reason is worse than a refusal

The refusal itself was correct: nothing in that job can protect images it cannot reach. But because
the job refuses as a whole, one installation moving registry stopped the protection of every other
installation — including the one that *is* exposed to the deletion this job exists to guard against.
And the condition for turning cleanup back on is literally "this job is green", so a mislabelled red
was sitting between the fleet and resuming cleanup at all.

## What changed

Images in another registry are now **found and named**. They are still never locked — nothing here
can write to somebody else's registry — but the situations that used to look identical no longer
do:

- **Pinned here** — protected, as before.
- **Any registry nobody has written down** — still a refusal, but it names the registry and says
  exactly what to record. Skipping silently would make an unprotected installation look like a
  protected one.
- **Pinned only in a registry recorded as ours-but-unreachable** — accepted, and printed on its own
  line in the run's summary, which says protection there is **unverified** rather than claiming
  somebody else is handling it.
- **Pinned both here and there** — a refusal. Half of what it runs would be protected and half not,
  and the run would report success.
- **Pinning nothing at all, anywhere** — still a refusal, and the message now says "in any
  registry", so it means what it says.

The thing written down is the **registry**, not the installation. That is what makes it possible to
say the list is complete: a note attached to one installation answers "is this one out of scope" and
can never answer "is every registry we pull from accounted for" — an installation using its recorded
registry *and* a second unrecorded one would pass such a note with half its images unnamed.

Two entries cover the whole fleet today, and they were measured rather than guessed. One of the two
"references" that turned up during that measurement was a **line of prose** in a comment describing
what an image is built from — so comments are now stripped before anything is extracted, on both
paths. With an unrecorded registry being a refusal, a match inside a comment would stop the job over
a sentence.

## What is not answered

This says those images are out of *this* job's reach. It does not claim anything is keeping them.
What retains the fleet's own registry — now the default for new installations — is an open question,
and it is the same question this mechanism answers for the old one, asked again about a second
store.
