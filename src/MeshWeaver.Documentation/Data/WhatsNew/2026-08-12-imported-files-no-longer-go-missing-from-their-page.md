---
Name: Imported files no longer go missing from their page
Category: Fix
Description: An import that created pages now also attaches their files, instead of silently leaving every attachment behind.
Icon: Sparkle
Order: -20260812
---

# Imported files no longer go missing from their page

An import could finish looking successful — every page created — while none of the files that
belong to those pages were attached. On a large demo import, all 412 pages landed and all 409
groups of documents were rejected, so the pages opened with nothing on them.

The cause was that the framework lost track of who was performing the import once the work moved
between threads, and a file write with no known author is refused rather than attributed to the
wrong person. Page writes already remembered the author; file writes did not.

Both now record the caller up front, so imports attach their files as the person who ran them. If
you were affected, simply run the import again — it is idempotent and will attach the missing files.
