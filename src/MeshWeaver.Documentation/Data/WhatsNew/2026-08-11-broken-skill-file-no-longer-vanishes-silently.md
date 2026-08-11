---
Name: A broken skill file now says so instead of vanishing
Category: Fix
Description: A skill whose front matter cannot be parsed is reported by name in the logs, instead of silently disappearing from the slash-command menu.
Icon: Sparkle
Order: -20260811
---

If a skill's YAML front matter was invalid — most often an unquoted `:` inside a description — the
skill was skipped while the portal started up perfectly. Nothing was logged, nothing turned red, and
the only symptom was that the slash command had quietly disappeared from the menu.

Skipping is still the right behaviour: one bad file must never stop the portal from starting. But the
skip is no longer silent. The file is now named in the log, together with the reason and the usual
cause, so a broken skill takes seconds to find instead of being invisible.

The same guarantee already applied to agents; skills now match it.
