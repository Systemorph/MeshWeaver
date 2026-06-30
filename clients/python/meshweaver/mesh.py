"""Ergonomic mesh operations — the in-language port of ``MeshWeaver.AI.MeshOperations``.

Each method is a thin composition of the :class:`MeshConnection` primitives (``observe`` / ``post`` /
``subscribe``). The request *type names* and *target addresses* below are the mesh's own message
contracts; they are annotated ``# WIRE:`` where they must be confirmed against the running mesh
(capture a sample from the C# round-trip test). The transport underneath is already correct.
"""
from __future__ import annotations

import uuid
from typing import Any, AsyncIterator, Optional

from .connection import MeshConnection, connect as _connect
from .types import MeshNode


class Mesh:
    """High-level mesh handle. Wraps a :class:`MeshConnection`."""

    def __init__(self, connection: MeshConnection, mesh_address: str = "mesh/main"):
        self._c = connection
        self._mesh = mesh_address  # WIRE: confirm the portal's mesh-service address

    @classmethod
    async def connect(cls, url: str, token: Optional[str] = None, **kw: Any) -> "Mesh":
        return cls(await _connect(url, token=token), **kw)

    async def close(self) -> None:
        await self._c.close()

    async def __aenter__(self) -> "Mesh":
        return self

    async def __aexit__(self, *exc: Any) -> None:
        await self.close()

    # ---- reads -----------------------------------------------------------

    async def search(self, query: str, base_path: Optional[str] = None, limit: int = 50) -> list[dict[str, Any]]:
        """Free-text / structured mesh query (routes to vector or SQL server-side)."""
        resp = await self._c.observe(
            self._mesh,
            "QueryRequest",  # WIRE: confirm IMeshService query request type
            {"query": query, "basePath": base_path, "limit": limit},
        )
        return resp.message.get("results") or resp.message.get("Results") or []

    async def get(self, path: str) -> MeshNode:
        """Read a single node's current state (one snapshot off its live stream)."""
        async for delivery in self.watch(path):
            return MeshNode.from_change(delivery.message)
        raise RuntimeError("stream closed before first state")

    async def watch(self, path: str) -> AsyncIterator[MeshNode]:
        """Subscribe to a node's live state — yields on every change (Full, then merge-patches)."""
        stream_id = uuid.uuid4().hex
        async for delivery in self._c.subscribe(
            target=path,
            stream_id=stream_id,
            subscribe_type="SubscribeRequest",  # WIRE: confirm reference shape for a node path
            subscribe_msg={"reference": {"path": path}},
        ):
            yield MeshNode.from_change(delivery.message)

    # ---- writes (carry the caller's identity, server-stamped) ------------

    async def patch(self, path: str, fields: dict[str, Any]) -> None:
        """Field-level partial update (content deep-merges, RFC 7396) — the canonical mutation."""
        await self._c.post(
            target=path,
            message_type="PatchDataRequest",  # WIRE: confirm partial-update request type
            message={"path": path, "change": fields},
        )
