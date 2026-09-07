---
Name: A published build now records whose content it holds
Category: Fix
Description: A prepared build recorded the name of the pipeline that produced it instead of the name of the project whose content went into it — so the safeguard that keeps a portal's content and its prepared build in step could never recognise its own subject.
Icon: ArrowSyncCheckmark
Order: -20260907
---

# A published build now records whose content it holds

When this platform prepares content for a portal, it stores the result once and stamps it with two
facts: which commit the content came from, and which project the content belongs to. A portal reads
those stamps to decide one thing — whether the newest content from a project is content it can
actually run, or content it should wait for.

The second stamp was wrong in exactly the case it exists for. Most of the time the pipeline
preparing the content and the project the content belongs to are the same thing, and the stamp was
taken from the pipeline. But the platform's own pipeline also prepares content for a *separate*
project, and there the two are different — so that content was filed under the platform's name
rather than its own.

The consequence was silent and complete: the safeguard looked for a prepared build belonging to that
project, found none filed under its name, and concluded there was nothing to wait for. It let every
update through. A portal could therefore pick up new content minutes after it was written while the
prepared build it needed was still the one from before — and each page then had to be rebuilt from
scratch every time the portal restarted, instead of starting from the prepared build.

The stamp now records the project whose content was prepared, taken from the same place the content
itself is fetched from, so the two can no longer disagree. Both pipelines state it explicitly rather
than relying on a default that happened to be right for one of them.

The check that proves it runs the real publishing script and reads the stamp back off the stored
bytes, in both directions — content prepared for another project records that project, and a
pipeline preparing its own content still records itself. Pointed at the previous version of the
script, the first of those goes red, which is how the fault was confirmed rather than assumed.
