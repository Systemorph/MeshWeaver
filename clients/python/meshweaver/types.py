"""Minimal mesh types the SDK surfaces in-language."""
from __future__ import annotations

from dataclasses import dataclass, field
from typing import Any, Optional


@dataclass
class MeshNode:
    """A mesh node as seen by a participant. ``content`` is the node's typed payload."""

    path: Optional[str] = None
    name: Optional[str] = None
    node_type: Optional[str] = None
    content: dict[str, Any] = field(default_factory=dict)
    raw: dict[str, Any] = field(default_factory=dict)

    @classmethod
    def from_change(cls, change: dict[str, Any]) -> "MeshNode":
        """Build from a DataChangedEvent/node payload, tolerant of property casing."""
        def g(*keys: str) -> Any:
            for k in keys:
                if k in change:
                    return change[k]
            return None

        return cls(
            path=g("path", "Path"),
            name=g("name", "Name"),
            node_type=g("nodeType", "NodeType"),
            content=g("content", "Content") or {},
            raw=change,
        )
