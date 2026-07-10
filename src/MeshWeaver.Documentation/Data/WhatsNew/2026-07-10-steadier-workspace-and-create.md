---
Name: Steadier workspace, and “create” makes real pages
Category: What's New
Description: Chats and deletes no longer stall the workspace, and asking to “create” something now makes a proper page.
Icon: Sparkle
---

# Steadier workspace, and “create” makes real pages

We fixed two issues that could make the workspace feel stuck. A chat response that couldn't be written now fails gracefully and ends the round, instead of retrying in a tight loop that slowed everything down. And deleting a node now returns immediately when it succeeds, instead of appearing to hang for up to a minute.

We also sharpened what happens when you ask an assistant to **create** something: it now makes a proper mesh node — a Markdown page by default, or the right specialized node (a Space, agent, and so on) — rather than dropping a stray `.txt` file.
