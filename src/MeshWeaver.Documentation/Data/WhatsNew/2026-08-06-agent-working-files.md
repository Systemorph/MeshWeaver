---
Name: Agents can keep working files, and they live in the mesh
Category: What's New
Description: An agent can now write notes, plans and intermediate results to a durable working area — stored as ordinary mesh content, so you can read it, and it survives the conversation.
Icon: DocumentBulletList
---

An agent working on something long — researching, drafting, reconciling a set of numbers — can now
keep **working files**: notes, plans and intermediate results it writes down as it goes and reads
back later, instead of carrying everything in the conversation.

What makes this different from a scratchpad is where the files live. They are ordinary mesh
content, in a working area belonging to the conversation. That means you can open them, they keep
their version history, they obey the same permissions as everything else you own, and they are
still there tomorrow. Nothing is hidden inside the agent.

Agents opt in, so this changes nothing for the ones that have no use for it. An agent that does
declare it can read, write, list, search and delete within its own working area — and only there:
a working file can never reach out into the rest of your content.

Under the hood this is our implementation of Microsoft Agent Framework's file-store abstraction.
The practical consequence is that capabilities built against that framework can run on MeshWeaver
and write into the mesh, rather than onto a disk that disappears with the process. The same
adaptation now also serves your Skills to that framework, so a skill authored anywhere in a Space
is found — no longer only the ones filed in one exact folder.
