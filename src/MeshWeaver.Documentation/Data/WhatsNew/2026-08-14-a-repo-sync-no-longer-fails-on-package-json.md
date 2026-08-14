---
Name: A repo sync no longer fails over an ordinary JSON file
Category: Fix
Description: Syncing a source repository reported a failed import because of files like package.json, which were never meant to be content in the first place.
Icon: DocumentBulletList
Order: -20260814
---

# A repo sync no longer fails over an ordinary JSON file

Syncing a source repository brings its files into the mesh. Some of those files describe content;
most are just part of the project — an npm manifest, a compiler configuration, a lock file.

Every JSON file was being read as though it described a piece of content. When one of them did not
fit — an npm `package.json` records its version as `0.1.0`, where content records a version as a
number — the sync reported the file as broken and finished as **failed**. The file was fine; it was
simply never content. Any repository holding one showed a failed sync on every run, and the genuine
problems that report is meant to surface were buried alongside it.

The quieter case mattered more. A JSON file with nothing in common with a content file did not fail
at all — it was imported as an **empty** entry, silently. So the two possible outcomes for an
ordinary project file were a false alarm or an invisible piece of clutter.

A sync now recognises which JSON files describe content and leaves the rest alone, exactly as it
already does for other project files. A file that genuinely is content but is malformed is still
reported — that report now means what it says.
