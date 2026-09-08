---
Name: The install check no longer reports a package's assets as missing pages
Category: Fix
Description: The check that verifies an installed package is whole counted every file the package ships — images, app source, licence files — as a page the install owed you, and reported the ones that are not pages as missing on every restart. It now counts exactly what an install writes, and every line says which population it counted.
Icon: ClipboardCheckmark
Order: -20260908
---

# The install check no longer reports a package's assets as missing pages

Since last week every start checks each installed package against what its install record says it
should hold, and reports anything absent. That check was counting the wrong things.

A package ships more than pages. Alongside the Markdown, the code and the typed content that become
nodes, it carries images, app source, licence files, configuration — files the install correctly
never turns into anything. The check did not know that: it counted **every** file the package
declares. So `Chess`, which ships one React Native view under `gui/`, was reported as *"1 of 35
declared node(s) are ABSENT: [Chess/gui/rn/chess]"* — at error level, on every replica, on every
restart, for a file no install has ever written. Worse, the same verdict made the catalog run a
full reinstall of the package each time it was visited, which could never make the count reach
zero.

The two sides now ask the same question. Whether a file becomes a page is the installer's rule, and
the check uses that rule rather than a second copy of half of it. A package whose files are *all*
assets now reports "declares no page to compare against" instead of reporting every one of them
missing.

**And every line says what it counted.** A count taken over the wrong set of things reads exactly
like a correct one, which is why nothing gave this away for a week. Each verdict now carries and
prints its population — how many files the record declares, how many of those are not pages and
why, and how many distinct pages were compared:

```text
Chess → 'Chess': every declared node is present.
Counted over: 36 file(s) declared, 2 of them not node files
(README/manifest/content assets, or an extension no parser claims) → 34 distinct node path(s) compared.
```

A genuinely missing page is still reported exactly as before — the change narrows what is counted,
not what counts as missing.

See [Install Completeness](/Doc/Architecture/InstallCompleteness) for the contract and the five
verdicts.
