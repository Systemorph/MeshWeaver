---
Name: Agent tools always answer, and Stop now stops them
Category: Fix
Description: Version history, skills, plans, comments, edits and content search now answer every time and unwind when you press Stop.
Icon: Sparkle
Order: -20260822
---

# Agent tools always answer, and Stop now stops them

Eight agent tools could leave a conversation waiting forever. When the mesh finished a read without
returning anything — a node that had gone away, a store that answered nothing — the tool simply never
replied, and the agent sat there with no error and nothing in the log.

Pressing Stop did not help: those tools never looked at the cancellation the Stop button raises, so
the round kept running until something else timed it out.

Both are fixed. Every one of these tools now gives an answer in every case, including the "nothing
came back" case, which now reads as a plain "not found" instead of silence. Pressing Stop unwinds the
tool call immediately and stops the work it started. The tools affected are version history (list,
read, restore, restore-to-a-point-in-time), loading a skill, storing a plan, adding a comment,
suggesting an edit, uploading content, and searching or reading indexed content chunks.
