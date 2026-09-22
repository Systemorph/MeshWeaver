---
Name: The resolver that picks a platform could answer from a stale attempt
Category: Fix
Description: Every repository in the fleet decides which platform build it compiles and tests against by reading what its own main branch last passed on. Five faults in that reader are fixed. The one that mattered most let a re-run that published an unrelated note fall back to an older attempt and adopt its answer — quietly pinning every open change to a platform build that was already superseded, under a log line insisting the newer attempt had said nothing.
Icon: Rewind
Order: -20260919
---

# The resolver that picks a platform could answer from a stale attempt

Before a repository builds anything, it has to decide *which* platform build to build against. It
answers that by looking at its own main branch: what is the newest platform build main has already
passed its full test suite on? Building on anything newer risks reddening every open change at once
for a reason no author could have caused, so this reader is the thing that stands between a bad
platform build and everybody's afternoon.

Five faults in it are fixed here. All five were found by the automatic code reviewer on changes that
had already merged, in five different repositories — and because every repository keeps its own copy
of this reader, several of them were the same fault reported five times over.

## Answering from an older attempt that was never asked

When a run is re-run after a flake, some of its steps are not re-run at all: they are carried over,
and the record for the carried-over step comes back empty. The reader knows this, and is allowed to
look at the previous attempt to recover what that step originally published.

The permission was meant to be narrow — *the record came back empty, so look further back*. What was
actually written was broader: *the record contains nothing I recognise, so look further back*. Those
are not the same thing. A step that publishes any other note at all — an ordinary warning, for
instance — lands that note on the same record. The reader saw a record it did not recognise, decided
the answer had been lost, walked back to an earlier attempt, and adopted that attempt's answer
instead.

The result is a platform build that is real but superseded, chosen in preference to the newer one
the run had actually settled on — and reported in the log with a sentence stating that the newer
attempt had published nothing. It had. The reader had simply not been looking for what it said.

Now the walk stops at the first attempt that answers anything at all. Whether that answer names a
platform build is a separate question, and one the reader already had two distinct sentences for.
Only a genuinely empty record permits looking further back.

## Looking back without a limit

That same walk had no stopping point. A run re-run a dozen times cost two extra lookups per attempt,
every time it was examined — and the runs most likely to have been re-run many times are the ones
from the incident that is still going on. The reader would spend its request budget precisely when
it could least afford to.

The walk now stops after four attempts and says so, rather than reporting a truncated search as a
finished one.

## Failing with a stack trace instead of an explanation

The first thing this reader does is ask for a list of main's recent runs. Every later read already
handled the case of GitHub not answering — noting it, skipping that run, and carrying on. This first
one did not. If GitHub failed to answer it, the whole job ended in a stack trace: no explanation, no
summary, and nothing to tell the reader whether main had genuinely passed nothing or GitHub had
simply not replied. Those two have opposite remedies. Now the failure is reported as itself, and
says which one it is.

## Telling an operator to fix something that was not broken

When no platform build could be established, the reader summarised what it had seen. Runs that
published a note nobody could parse were counted together with runs that published no note at all,
under a sentence saying they had published nothing — three lines below a note correctly stating that
one of them *had* published something. The operator was sent to fix main when the actual fault was a
single malformed message. The two are now counted and described separately.

## Blaming the credential for everything

Finally, when the image registry refused a request, the error said the same thing regardless of what
had actually happened: that a credential was probably wrong. So an operator handed a *"the registry
is busy, try again"* response went and rotated a credential that was never involved. During an
incident that is the most expensive kind of wrong an error message can be, because it is the kind
that gets acted on.

The message now matches the response. And responses that mean *"not right now"* — the registry
rate-limiting, or a node restarting underneath it — are retried a few times before giving up, which
is what already happened for a dropped connection. Only genuine refusals are treated as final.
