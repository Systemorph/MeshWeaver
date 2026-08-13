---
Name: Sample-space views stop rendering blank
Category: Fix
Description: Articles, report cards, task and product views in the sample spaces could come back empty — a title with no body, a card with no summary. They now read their content the tolerant way the platform already provides.
Icon: Sparkle
Order: -20260813
---

# Sample-space views stop rendering blank

Views across the ACME, Northwind, Cornerstone, FutuRe and Python demo spaces could render as an
empty shell: an article with its title and nothing under it, a report card with no summary, no
author line and no picture, a task or product page that just said the data was not available.

The content was there the whole time. What went wrong was how each view asked for it.

A stored item can arrive in one of several shapes. Usually it comes back as the exact kind of thing
the view expects. But an item that was imported, or written through the API with a plain body, or
saved before its kind existed, arrives as raw stored data with nothing to say what kind it is — and
so does an item belonging to a package that the part of the platform doing the reading does not
know about. There is a third case too: every time a package's code is rebuilt, its records get a
fresh identity, so yesterday's record and today's are no longer recognisably the same thing.

Each of these views checked for exactly one of those shapes and gave up on the rest. Giving up
produced nothing at all — no error, no warning, no line in any log. The page simply rendered empty,
which reads to the person looking at it as "there is nothing here" rather than "this could not be
read".

Every one of those reads now goes through the platform's tolerant accessor, which recognises all
three shapes and reports what it could not convert instead of swallowing it.

## Why so many places at once

The article view was the same extractor copied word-for-word into three different sample spaces, so
one defect shipped three times. That copy is gone: all three now call a single shared reader in the
platform itself — the same one the PDF, Word and HTML exports use, so a fix reaches every reader at
once and there is no longer a copy to drift.
