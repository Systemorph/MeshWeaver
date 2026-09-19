---
Name: A review that happened no longer reads as a review that did not
Category: Fix
Description: The merge check that waits for the automatic reviewer decided whether a review had landed by looking for one particular phrase in its text. When the reviewer changed its wording, six pull requests that had genuinely been reviewed all reported that no review had arrived, and because the check had just become required they could not merge at all. It now recognises a review by who posted it, so a change of wording cannot hide one again.
Icon: Bug
Order: -20260918
---

The automatic reviewer posts a review; a merge check reads it and holds the pull request until every
finding it raised has a reply. Deciding **whether a review had arrived at all** was done by searching
its text for the heading `Pull request overview`.

On 2026-09-18 the reviewer began posting a shorter note — a verdict line and a sentence or two — with
no such heading. The phrase was gone, so six pull requests that had each been reviewed minutes
earlier all reported that the review had not landed. The day before, the check had been promoted to a
required one, so that report stopped being a note a reader could see past and became a block with no
way around it: replying to the findings did not help, because the check was waiting for a review it
could not see.

The check now recognises a review by **who posted it** rather than by how it is worded, and reads the
text only to tell a review apart from the reviewer declining to review — which it still does. A
future change of wording cannot hide a review again.
