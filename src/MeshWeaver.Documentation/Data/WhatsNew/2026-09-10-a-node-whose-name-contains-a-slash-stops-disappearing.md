---
Name: Nodes whose id contains a slash stop disappearing when you edit them
Category: Fix
Description: Editing a node whose id contains a slash — every language model, whose id is the provider's own identifier — quietly filed a second copy of it instead of changing the original. Both copies answered to the same address, and deleting either removed both. Edits now change the node you edited.
Icon: Warning
Order: -20260910
---

# Nodes whose id contains a slash stop disappearing when you edit them

Most nodes have a simple name at the end of their address: a page called `PricingTool` sitting in a
folder called `ACME/Projects`. Some do not. A language model's name is the identifier its provider
publishes — `z-ai/glm-5.3`, `anthropic/claude-opus-5`, `openai/gpt-5.2` — and that name has a slash
in the middle of it. The platform has always stored those exactly as written.

Editing one through the assistant did something else. Before saving, the platform "tidied" the name
by cutting it at its last slash and moving the first part into the folder — turning the model
`z-ai/glm-5.3` in folder `Provider/OpenRouter` into a model `glm-5.3` in a folder
`Provider/OpenRouter/z-ai`. The full address reads the same either way, so nothing looked wrong. But
the platform files nodes under the name-and-folder pair, not the address, so the edit was not
recognised as a change to the existing node: it filed a **second** node beside the first.

From then on the two were indistinguishable from outside. Both answered to the same address, so
reading it returned one of them — not reliably the same one. Each carried its own separate history,
so a node with a long edit history appeared to have none. And because removing a node works by
address, deleting either one removed both.

That is what happened to a model on the production portal on 9 September: two edits filed two extra
copies, and the following morning the model was gone from the model list entirely. It was restored
by hand, and this fix removes the cause.

The tidying step is gone. A node's id is now stored exactly as you give it, slashes included, and an
edit changes the node you edited. The assistant's guidance changed to match: it used to be told that
an id must never contain a slash, which was simply not true, and following that instruction was one
way to produce the duplicate by hand. It is now told to keep an existing node's id and folder exactly
as it read them.

Nothing changes for ordinary nodes. An id with no slash in it is stored the way it always was.
