---
Name: Model nodes can be deleted again
Category: Fix
Description: A node whose id contains a slash — which every AI model node's id does — could be read but never deleted; the delete reported "Node not found" for a node you could open in the same breath. It now deletes.
Icon: Delete
Order: -20260825
---

# Model nodes can be deleted again

Deleting a node whose **id contains a slash** failed with *"Node not found"* — about a node the very
same tools could open, read and edit. The misleading part was the error: "not found" sends you
looking for a missing node or a permission problem, and neither was the matter.

This was not a corner case. Every language-model node is named by its provider's wire id —
`z-ai/glm-5.3`, `anthropic/claude-opus-5`, `openai/gpt-5.2` — so **no model node could be removed
through the API or the assistant's tools at all**. Retiring a model, an ordinary operator action,
had no programmatic route left; only the node's own Delete page still worked.

The storage layer had been taking a node's path apart by splitting it at the last slash, which puts
half of a vendor-prefixed id into the wrong place and matches no stored row. Every read, existence
check, batch read and delete now addresses a node by its full path instead, so a slash in an id is
simply part of the id — the way create, read and edit have always treated it.

Version history for such nodes is fixed by the same change.
