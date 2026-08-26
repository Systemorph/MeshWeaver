---
Name: Skills authored as markdown finally work
Category: Fix
Description: A skill shipped as a .md file now arrives as a real skill carrying its instructions, instead of a silently empty one.
Icon: Sparkle
Order: -20260825
---

# Skills authored as markdown finally work

A skill written the way the guidance describes — a `.md` file with `nodeType: Skill` front matter and
the procedure in the body — used to arrive in the mesh as an ordinary markdown page. It never reached
the slash-command menu, and it lost its `/name` and its icon on the way in. Eleven skills were
affected, several of them shipped with the platform's own plugins.

Those files now import as real skills, with the markdown body landing where a skill's instructions
belong. Nothing about how you author a skill changes, and there is nothing to migrate: a skill that
imported wrongly is corrected the next time its package is installed or updated.
