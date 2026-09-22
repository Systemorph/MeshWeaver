---
Name: The hourly delivery alarm now fires on failures, not on normal waiting
Category: Fix
Description: An automated ticket saying every install was stuck on its old image had been filed 109 times, and not one of them recorded a delivery that had failed — it fired while a new build was simply on its way, or while nothing at all was wrong; it now waits for evidence that a publish was attempted and did not finish.
Icon: Warning
Order: -20260918
---

# The hourly delivery alarm now fires on failures, not on normal waiting

Every hour, an automated check asks whether the newest code on the main branch has its full set of
deployment images published. When the answer was no, it opened a ticket reading:

> **CD: main `<sha>` has an incomplete image set** — main's HEAD is missing part of its deployment
> image set, so every self-updating install stays on the previous image.

It had opened that ticket **109 times**, at up to fifteen a day, each one closing itself half an
hour later. Read as written, each was a delivery outage. **None of them was.**

Two entirely ordinary situations were being reported as failures.

**A build that is simply on its way.** There is a gap of several minutes between a change passing
its tests and the delivery build even being *created*, and then roughly forty more while it runs.
During that whole window the images genuinely are not published yet — which is not a fault, it is
what waiting looks like. Of the twenty-five most recent tickets, **nineteen were opened before any
delivery build for that change had been created at all.** Not one was opened after a build had
finished without publishing.

**A detail that can never be up to date.** The published portal image also records which commit of
the companion plugins repository it was built from. That repository receives forty-odd merges a day
— about one every half hour — while a portal build takes about forty-five minutes. So the recorded
detail is out of date before the build that writes it has finished, every single time, by
arithmetic. Twenty-eight of the tickets were this and only this: every image present, every install
able to update, and a ticket announcing the opposite. One change sat on the main branch for four
hours on 2026-09-17 and was rebuilt three times, each rebuild opening and closing its own ticket
over an identical source tree.

Both now behave differently. The check distinguishes *"an image an install needs is missing"* from
*"the images are all there and one recorded detail is behind"*, and only the first can be called an
incomplete set. The refresh for the second still happens exactly as before — it simply says so in
the build log instead of raising an alarm about installs that were never held back.

And before the ticket is opened at all, the automation now looks for positive evidence that a
publish was **attempted**: every delivery build stages its layers under a marker tag before it
publishes anything, so that marker is the fingerprint of a build that started and did not finish.
Without one, nothing has failed — the hourly check is simply the thing publishing the change, and
it now says that plainly.

Nothing about *what* gets built or published changed. A delivery that really does die halfway still
opens the same ticket, still records each attempt against the same three-attempt budget, and still
gets the same `cd-unhealed` label when it runs out — verified by a test that drives exactly that
case and watches the ticket being created.

The full measurement, including the two hypotheses that were tested and ruled out (nothing was
deleting images; the repair really did repair), is in **Doc → Architecture → The CD Ledger Records
a Failure, Not a Cadence**.
