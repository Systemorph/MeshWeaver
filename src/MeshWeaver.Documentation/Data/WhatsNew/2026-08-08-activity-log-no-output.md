---
Name: Finished runs with no output now say so
Category: What's New
Description: An activity that finishes without producing log output now states that explicitly instead of showing a stale "Running…" row.
Icon: Sparkle
---

# Finished runs with no output now say so

When a script run finished without writing anything to its log — common for code
cells whose result is a rendered control rather than log lines — the activity
panel showed "✓ Done" next to a row that still read "Running…", leaving you to
guess whether the run had worked.

The log panel now checks the run's actual status: a running activity still shows
"Running…", and a finished one with an empty log states "This run produced no
output." — in your language.
