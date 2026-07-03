"""Reading *files kept in mesh content* — the textual payload of a node, fence-aware.

A "file in content" is an ordinary mesh node whose body carries the file text, typically a Markdown
node with the data inside a fenced code block (renders readably in the portal, stays hand-editable).
These helpers extract that text from the shapes a node read yields: a
:class:`~meshweaver.types.MeshNode`, any object with a ``content`` attribute, or a plain node dict —
with the content itself either a raw string or a ``MarkdownContent``-style dict.

Used by the examples: the pandas node reads a CSV file (``examples/pandas_node.py``), the fine-tuning
example reads a JSONL training set (``examples/finetune.py``).
"""
from __future__ import annotations

from typing import Any

#: Content-dict fields that may carry the node's text, in probing order (camel + Pascal casing).
_TEXT_FIELDS = ("content", "Content", "csv", "Csv", "text", "Text")


def fenced_or_text(text: str) -> str:
    """The payload inside ``text``: the first fenced code block if one is present, else the text itself.

    Prose around the fence (headings, explanations) is ignored — so a node can document its own data.
    A node that stores the bare payload with no fence works too."""
    lines = text.splitlines()
    start = next((i for i, line in enumerate(lines) if line.lstrip().startswith("```")), None)
    if start is None:
        return text.strip()
    end = next((i for i in range(start + 1, len(lines)) if lines[i].lstrip().startswith("```")), len(lines))
    return "\n".join(lines[start + 1:end]).strip()


def text_from_node(node: Any) -> str:
    """Extract the file text a mesh node keeps in its content (fence-aware).

    Raises :class:`ValueError` when the node carries no textual content."""
    content = getattr(node, "content", None) if not isinstance(node, dict) else (node.get("content") or node.get("Content"))
    if isinstance(content, str):
        return fenced_or_text(content)
    if isinstance(content, dict):
        for field in _TEXT_FIELDS:
            text = content.get(field)
            if isinstance(text, str):
                return fenced_or_text(text)
    raise ValueError("node carries no textual content")
