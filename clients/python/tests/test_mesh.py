"""Mesh ops compose correctly over the connection primitives — driven by a duck-typed fake connection
(no gRPC channel needed), the Python analog of the web client's in-memory-transport tests."""
from typing import Any, AsyncIterator

from meshweaver.envelope import Delivery
from meshweaver.mesh import Mesh


def _delivery(message_type: str, message: dict[str, Any]) -> Delivery:
    return Delivery(id=None, sender=None, target=None, request_id=None, message_type=message_type, message=message, raw={})


class FakeConn:
    """Records observe/post calls and answers them the way the mesh hub would."""

    def __init__(self) -> None:
        self.observed: list[tuple[str, str, dict[str, Any]]] = []
        self.posted: list[tuple[str, str, dict[str, Any]]] = []

    async def observe(self, target: str, message_type: str, message: dict[str, Any], *args: Any, **kw: Any) -> Delivery:
        self.observed.append((target, message_type, message))
        if message_type == "QueryRequest":
            return _delivery("QueryResponse", {"results": [{"path": "ACME/Stories/1", "name": "S1"}]})
        if message_type == "CreateNodeRequest":
            return _delivery("CreateNodeResponse", {"path": message["node"].get("path")})
        return _delivery(message_type.replace("Request", "Response"), {"status": "ok"})

    async def post(self, *, target: str, message_type: str, message: dict[str, Any]) -> None:
        self.posted.append((target, message_type, message))

    async def subscribe(self, *, target: str, stream_id: str, subscribe_type: str, subscribe_msg: dict[str, Any]) -> AsyncIterator[Delivery]:
        path = subscribe_msg["reference"]["path"]
        yield _delivery("DataChangedEvent", {"path": path, "content": {"done": False}})


async def test_search_returns_results():
    mesh = Mesh(FakeConn())
    results = await mesh.search("nodeType:Story namespace:ACME")
    assert results[0]["path"] == "ACME/Stories/1"


async def test_get_reads_first_node_state():
    node = await Mesh(FakeConn()).get("ACME/Stories/42")
    assert node.path == "ACME/Stories/42"
    assert node.content["done"] is False


async def test_patch_posts_partial_update():
    conn = FakeConn()
    await Mesh(conn).patch("ACME/X", {"content": {"done": True}})
    target, message_type, message = conn.posted[0]
    assert target == "ACME/X"
    assert message_type == "PatchDataRequest"
    assert message["change"] == {"content": {"done": True}}


async def test_create_returns_response_and_lifecycle_ops_route_to_mesh():
    conn = FakeConn()
    mesh = Mesh(conn)
    resp = await mesh.create({"path": "ACME/New", "nodeType": "Story"})
    assert resp["path"] == "ACME/New"
    await mesh.delete("ACME/Old")
    await mesh.move("ACME/A", "ACME/B")
    await mesh.copy("ACME/A", "ACME/C")
    types = [mt for _, mt, _ in conn.observed]
    assert {"CreateNodeRequest", "DeleteNodeRequest", "MoveNodeRequest", "CopyNodeRequest"} <= set(types)


async def test_execute_flips_requested_status():
    conn = FakeConn()
    await Mesh(conn).execute("ACME/Jobs/import")
    target, message_type, message = conn.posted[0]
    assert message_type == "PatchDataRequest"
    assert message["change"]["requestedStatus"] == "Running"
