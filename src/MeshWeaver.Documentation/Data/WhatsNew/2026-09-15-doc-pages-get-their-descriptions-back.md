---
Name: Doc pages get their descriptions back
Category: Fix
Description: >-
  Thirty-three documentation pages shipped with no summary — their front matter was refused or
  silently truncated by YAML, and nothing reported it. The descriptions are back, and a guard now
  runs the real parser over the whole doc tree.
Icon: DocumentText
Order: -20260915
---

# Doc pages get their descriptions back

A documentation page carries its one-line summary in YAML front matter. That summary is what you
read on a catalog card, in a table of contents, and in search results.

**Thirty-three pages had none**, and the page itself looked perfectly fine.

## Two different ways to lose a line of text

**Twenty pages** had front matter a YAML parser refuses outright — almost always a value containing
`": "`, which YAML reads as the start of a nested mapping:

```yaml
Description: … and the shape most of these share: a narrow instrument answering correctly
```

The import does not fail. It falls back to a rescue that recovers the name, category, icon, state
and order — and not the description. So the page lands, renders and is findable by title, with an
empty summary everywhere a summary is shown.

**Thirteen more** parsed perfectly and were simply cut short. An unquoted `#` opens a YAML comment,
so a description ending in a issue reference lost everything from the `#` onward:

```yaml
Description: … and the repair path both decisions had to leave open — issue #2993.
#                                                                      ↑ everything from here was dropped
```

Both halves are fixed. Long values are now folded block scalars, which need no escaping and read
better at this length.

## Why it went unnoticed for weeks

The test watching What's New entries checked that each one carries a `Description:` — by matching
the text with a regular expression, the same way the rescue does. A line the regex finds and the
parser refuses satisfied it. Twelve entries were green in that test and description-less in the
mesh at the same time.

A new guard runs the **real** YAML parser across every front-matter block in the documentation tree,
and a second one catches the truncating `#`. Both are the checks that already existed for the
repository's skill files, pointed at the tree that was missing them.
