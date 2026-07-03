"""Ergonomic mesh operations — the in-language port of ``MeshWeaver.AI.MeshOperations``.

Two transports, matching what the portal actually offers a remote participant:

* **REST** (``POST {portal}/api/mesh/<verb>``, ``Authorization: Bearer mw_…``) — the portal's
  transport-mirror of the MCP tools (``MeshApiEndpoints`` → ``MeshOperations``): ``get`` / ``search``
  / ``query_nodes`` / ``create`` / ``update`` / ``patch`` / ``delete`` / ``move`` / ``copy``. One
  shared core backs REST, MCP and this SDK, so the semantics cannot drift.
* **gRPC** (the participant connection) — what REST cannot do: being addressable on the mesh, and
  **live node streams**: ``watch(path)`` speaks the synchronization protocol (``SubscribeRequest`` →
  ``DataChangedEvent`` Full + RFC 7396 merge patches → ``UnsubscribeRequest``), the same wire the
  Blazor / MAUI clients bind to.

Shapes are pinned against the C# contracts (``Messages.cs``, ``PatchDataRequest.cs``,
``MeshApiEndpoints.cs``) and confirmed live against a running portal — see ``tests/test_mesh.py``.
"""
from __future__ import annotations

import asyncio
import contextlib
import json
import ssl
import urllib.error
import urllib.request
import uuid
from typing import Any, AsyncIterator, Optional

from .connection import MeshConnection, connect as _connect
from .types import MeshNode


class MeshError(RuntimeError):
    """An operation the portal answered with an ``Error: …`` / ``Not found: …`` sentinel."""


def merge_patch(target: Any, patch: Any) -> Any:
    """RFC 7396 JSON merge patch — the shape ``PatchDataRequest`` writes travel as."""
    if not isinstance(patch, dict):
        return patch
    result = dict(target) if isinstance(target, dict) else {}
    for key, value in patch.items():
        if value is None:
            result.pop(key, None)
        else:
            result[key] = merge_patch(result.get(key), value)
    return result


def apply_json_patch(target: Any, ops: list[dict[str, Any]]) -> Any:
    """RFC 6902 JSON Patch — the client-side fold for ``DataChangedEvent`` Patch frames.

    The sync protocol's incremental changes are op ARRAYS (``[{"op":"replace","path":"/content/…",…}]``
    — confirmed on the live wire), not merge objects. Supports the ops the owner emits
    (add / replace / remove) over object paths; list indices are handled for completeness."""
    import copy

    result = copy.deepcopy(target) if isinstance(target, (dict, list)) else {}
    for op in ops:
        kind = op.get("op")
        pointer = [p.replace("~1", "/").replace("~0", "~") for p in str(op.get("path", "")).split("/")[1:]]
        if not pointer:
            if kind in ("add", "replace"):
                result = op.get("value")
            continue
        parent = result
        for key in pointer[:-1]:
            if isinstance(parent, list):
                parent = parent[int(key)]
            elif isinstance(parent, dict):
                parent = parent.setdefault(key, {})
        leaf = pointer[-1]
        if isinstance(parent, list):
            index = len(parent) if leaf == "-" else int(leaf)
            if kind == "remove":
                del parent[index]
            elif kind == "add":
                parent.insert(index, op.get("value"))
            else:
                parent[index] = op.get("value")
        elif isinstance(parent, dict):
            if kind == "remove":
                parent.pop(leaf, None)
            else:
                parent[leaf] = op.get("value")
    return result


class RestClient:
    """Thin async client for the portal's ``/api/mesh`` verb endpoints (stdlib-only)."""

    def __init__(self, url: str, token: Optional[str] = None, insecure: bool = False):
        self._base = url.rstrip("/") + "/api/mesh"
        self._token = token
        self._ssl = ssl._create_unverified_context() if insecure else None  # noqa: S323 — local/dev portals

    async def post(self, verb: str, body: dict[str, Any]) -> str:
        return await asyncio.to_thread(self._post_sync, verb, body)

    def _post_sync(self, verb: str, body: dict[str, Any]) -> str:
        headers = {"Content-Type": "application/json"}
        if self._token:
            headers["Authorization"] = f"Bearer {self._token}"
        request = urllib.request.Request(f"{self._base}/{verb}", data=json.dumps(body).encode(), headers=headers)
        try:
            with urllib.request.urlopen(request, context=self._ssl) as response:  # noqa: S310 — caller-supplied portal URL
                return response.read().decode()
        except urllib.error.HTTPError as ex:
            raise MeshError(f"{verb} -> HTTP {ex.code}: {ex.read().decode(errors='replace')[:500]}") from None


def _checked(result: str) -> str:
    """Raise :class:`MeshError` on the MeshOperations error sentinels (``Error: …``,
    ``Error creating node: …``, ``Not found: …``, ``Invalid JSON: …``)."""
    if result.startswith(("Error", "Not found:", "Invalid JSON")):
        raise MeshError(result)
    return result


class Mesh:
    """High-level mesh handle: REST ops + (optionally) the gRPC participant connection."""

    def __init__(self, connection: Optional[Any] = None, url: Optional[str] = None,
                 token: Optional[str] = None, rest: Optional[Any] = None, insecure: bool = False):
        self._c = connection
        self._rest = rest or (RestClient(url, token, insecure=insecure) if url else None)

    @classmethod
    async def connect(cls, url: str, token: Optional[str] = None, address: Optional[str] = None,
                      grpc_url: Optional[str] = None, insecure: bool = False,
                      root_certificates: Optional[bytes] = None) -> "Mesh":
        """Connect both transports: REST at ``url`` and the gRPC participant at ``grpc_url``
        (defaults to ``url`` — the portal ingress routes ``meshweaver.v1.Mesh/Open`` natively).
        ``root_certificates`` overrides the gRPC trust roots for self-signed local portals."""
        return cls(await _connect(grpc_url or url, token=token, address=address,
                                  root_certificates=root_certificates),
                   url=url, token=token, insecure=insecure)

    async def close(self) -> None:
        if self._c is not None:
            await self._c.close()

    async def __aenter__(self) -> "Mesh":
        return self

    async def __aexit__(self, *exc: Any) -> None:
        await self.close()

    def _require_rest(self) -> Any:
        if self._rest is None:
            raise MeshError("this operation needs the REST transport — construct Mesh with url=/token=")
        return self._rest

    # ---- reads -----------------------------------------------------------

    async def search(self, query: str, base_path: Optional[str] = None, limit: int = 50) -> list[dict[str, Any]]:
        """Free-text / structured mesh query → summary hits ``[{path, name, nodeType}, …]``."""
        result = _checked(await self._require_rest().post(
            "search", {"query": query, "basePath": base_path, "limit": limit}))
        return json.loads(result).get("results") or []

    async def query_nodes(self, query: str, limit: int = 50) -> list[dict[str, Any]]:
        """Mesh query → FULL node payloads (content included) — one round trip, no per-hit get."""
        result = _checked(await self._require_rest().post("query-nodes", {"query": query, "limit": limit}))
        return json.loads(result).get("results") or []

    async def get(self, path: str) -> MeshNode:
        """Read a single node. REST when configured; otherwise one snapshot off the node's live
        stream over the participant connection (:meth:`watch`)."""
        if self._rest is not None:
            result = _checked(await self._require_rest().post("get", {"path": path}))
            return MeshNode.from_change(json.loads(result))
        # aclosing is mandatory: returning out of a bare `async for` leaves the generator open,
        # so watch's finally (the UnsubscribeRequest) would never run — a leaked owner subscription.
        async with contextlib.aclosing(self.watch(path)) as stream:
            async for node in stream:
                return node
        raise MeshError(f"stream for {path} closed before first state")

    async def watch(self, path: str) -> AsyncIterator[MeshNode]:
        """Subscribe to a node's LIVE state over the participant connection — yields the current
        node on every change. Wire (confirmed live): post ``SubscribeRequest(streamId,
        MeshNodeReference)`` to the node's own address; the owner streams ``DataChangedEvent``\\ s —
        ``Full`` carries the whole node, ``Patch`` carries an RFC 6902 JSON-Patch op array — each
        stamped with a monotonically increasing ``version`` (frames can arrive duplicated; the
        version dedups). ``UnsubscribeRequest`` on exit releases the owner's subscription."""
        if self._c is None:
            raise MeshError("watch needs the gRPC participant connection — use Mesh.connect(...)")
        stream_id = uuid.uuid4().hex
        state: Any = None
        last_version = -1
        try:
            async for delivery in self._c.subscribe(
                target=path,
                stream_id=stream_id,
                subscribe_type="SubscribeRequest",
                subscribe_msg={
                    "reference": {"$type": "MeshNodeReference"},   # the target hub IS the node
                    "subscriber": self._c.address,                 # Address serializes as its path string
                },
            ):
                if delivery.message_type != "DataChangedEvent":
                    continue
                version = int(delivery.message.get("version") or 0)
                if version <= last_version:
                    continue                                        # duplicate / out-of-order frame
                last_version = version
                change = delivery.message.get("change")
                change_type = str(delivery.message.get("changeType") or "").lower()
                if change_type == "full":
                    state = change
                elif isinstance(change, list):
                    state = apply_json_patch(state, change)
                else:
                    state = merge_patch(state, change)
                if isinstance(state, dict):
                    yield MeshNode.from_change(state)
        finally:
            await self._c.post(path, "UnsubscribeRequest", {"streamId": stream_id})

    # ---- writes (REST; the caller's token identity is server-stamped) ----

    async def patch(self, path: str, fields: dict[str, Any]) -> str:
        """Field-level partial update (content deep-merges, RFC 7396) — the canonical mutation."""
        return _checked(await self._require_rest().post(
            "patch", {"path": path, "fields": json.dumps(fields)}))

    async def create(self, node: dict[str, Any]) -> str:
        """Create a node (first writer wins — an existing node is reported, not overwritten)."""
        return _checked(await self._require_rest().post("create", {"node": json.dumps(node)}))

    async def update(self, nodes: list[dict[str, Any]]) -> str:
        """Full-replacement update of one or more EXISTING nodes."""
        return _checked(await self._require_rest().post("update", {"nodes": json.dumps(nodes)}))

    async def create_or_update(self, node: dict[str, Any]) -> str:
        """Idempotent upsert: create, falling back to full-replacement update when it already exists."""
        try:
            return await self.create(node)
        except MeshError as ex:
            if "already exists" not in str(ex).lower():
                raise
            return await self.update([node])

    async def delete(self, *paths: str) -> str:
        """Delete one or more nodes (and their descendants). The verb takes a JSON ARRAY of paths."""
        return _checked(await self._require_rest().post("delete", {"paths": json.dumps(list(paths))}))

    async def move(self, source: str, target: str) -> str:
        """Move a node (and its satellites) from one path to another."""
        return _checked(await self._require_rest().post(
            "move", {"sourcePath": source, "targetPath": target}))

    async def copy(self, source: str, target_namespace: str) -> str:
        """Copy a node into another namespace."""
        return _checked(await self._require_rest().post(
            "copy", {"sourcePath": source, "targetNamespace": target_namespace}))

    async def execute(self, path: str) -> str:
        """Run an executable Code/activity node by flipping its control-plane trigger to Running
        (operations-as-scripts — the owning hub's watcher reacts). Use ``watch(path)`` to follow Status."""
        return await self.patch(path, {"content": {"requestedStatus": "Running"}})
