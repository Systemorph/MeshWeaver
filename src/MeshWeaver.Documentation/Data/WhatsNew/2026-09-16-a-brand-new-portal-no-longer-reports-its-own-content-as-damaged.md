---
Name: A brand-new portal no longer reports its own content as damaged
Category: Fix
Description: A freshly provisioned portal is allowed to start even when some of its content cannot be built yet — but it was explaining that by saying those parts had been broken beforehand, which was never true of a portal on its first run. It now says the real reason, and the two systems that read it agree.
Icon: CheckmarkStarburst
Order: -20260916
---

# A brand-new portal no longer reports its own content as damaged

When a portal starts, it builds the content types it is going to serve. If something that used to
build stops building, that is a **regression**, and the new instance deliberately refuses to take
traffic — the previous one keeps serving, and nobody sees a broken page.

A portal on its **very first run** is the one case where that protection has nothing to protect: no
previous instance is serving, so refusing traffic just leaves an application that can never start.
It is allowed to come up and report the trouble instead. That behaviour is correct and is not
changing.

## What was wrong

It was arriving at the right decision by way of a false statement.

Internally each content type carries the note *"this was working before"*. On a first run there is
no "before" — so, to suppress the protection, every type was marked as **not** having been working.
Read back, that says *these parts were already broken when we got here*, about a portal that had
never built anything at all. A fresh instance therefore described its own untouched content as
pre-existing damage.

Two different things had been folded into one note: **was this part working?** and **is there
anything here to protect?** They are not the same question, and on a first run they have opposite
answers.

## Why it mattered beyond the wording

Two separate systems read that note — the readiness check that decides whether an instance takes
traffic, and the go/no-go check that decides whether a rollout continues. They are meant to reach
the same verdict, and they were written to agree by reading the same fact. Once the fact had two
possible meanings, they quietly stopped agreeing: for the same content type on the same first run,
one treated a build failure as blocking and the other did not. Nothing failed, because both were
still doing what their own code said.

## What you get now

The two questions are recorded separately, and only the combination can hold an instance back:

- **Was this part working before?** — asked of the part itself. Something nobody has ever built is
  reported as healthy, because it is not damaged; it is simply new.
- **Is there a previous build to compare against?** — asked of the instance. Answered no only on a
  genuine first run, where every single content type is new.

Both readers now take both facts, so they cannot drift apart again. Nothing is hidden either: a
first-run instance still lists whatever could not be built, under a heading that says there was no
working build to compare against — the sentence that explains a new portal showing as degraded.

## What did not change

An established instance keeps its full strictness. One content type that used to build and no
longer does still holds the new instance back, and a part that was already failing before still
does not — one abandoned type cannot block every future deploy.
