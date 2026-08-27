---
Name: Stop now actually stops a running tool call
Category: Fix
Description: Pressing Stop mid-tool-call unwinds the round instead of leaving it running, and a tool that fails now answers the agent instead of throwing.
Icon: Bug
Order: -20260827
---

# Stop now actually stops a running tool call

Pressing **Stop** while an agent was in the middle of a mesh operation — reading a node, searching, patching, moving, or running a code check — did not stop it. The button ended the round on screen while the operation kept running underneath, so the work carried on invisibly and the thread could take a long time to settle.

Every one of those tools now listens for the stop signal: `get`, `search`, `create`, `update`, `patch`, `edit_content`, `delete`, `move`, `copy`, `get_diagnostics`, `recycle`, `navigate_to`, `run_tests`, the four code-intelligence tools, and the agent's own working-files tools. Stop unwinds them straight away, and the work is torn down rather than merely abandoned.

Two smaller things came with it:

- **A tool that fails now answers.** A failure used to surface as a raw error; the agent now receives it as an ordinary result it can read, explain, and work around.
- **A tool that comes back with nothing now says so** — previously that case produced no answer at all, which is what left some rounds waiting indefinitely.
