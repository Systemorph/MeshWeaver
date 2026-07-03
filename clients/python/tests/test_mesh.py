"""Mesh ops against the PINNED portal contracts: the REST verb surface (``/api/mesh/*`` — the
transport-mirror of MeshOperations) and the gRPC synchronization protocol for live node streams
(SubscribeRequest → DataChangedEvent Full/Patch → UnsubscribeRequest). Driven by duck-typed fakes;
the same shapes are confirmed live in the portal smoke run (see README status)."""
import json
from typing import Any, AsyncIterator

import pytest

from meshweaver.envelope import Delivery
from meshweaver.mesh import Mesh, MeshError, apply_json_patch, merge_patch


class FakeRest:
    """Records every verb call and answers with canned portal responses."""

    def __init__(self) -> None:
        self.calls: list[tuple[str, dict[str, Any]]] = []
        self.responses: dict[str, str] = {}

    async def post(self, verb: str, body: dict[str, Any]) -> str:
        self.calls.append((verb, body))
        return self.responses.get(verb, '{"status":"ok"}')


class FakeConn:
    """The gRPC-connection half: yields a scripted change stream, records posts."""

    address = "py/test"

    def __init__(self, deliveries: list[Delivery]) -> None:
        self.deliveries = deliveries
        self.posted: list[tuple[str, str, dict[str, Any]]] = []
        self.subscribed: list[tuple[str, str, dict[str, Any]]] = []

    async def post(self, target: str, message_type: str, message: dict[str, Any], access_context: Any = None) -> None:
        self.posted.append((target, message_type, message))

    async def subscribe(self, *, target: str, stream_id: str, subscribe_type: str,
                        subscribe_msg: dict[str, Any]) -> AsyncIterator[Delivery]:
        self.subscribed.append((target, subscribe_type, {**subscribe_msg, "streamId": stream_id}))
        for d in self.deliveries:
            yield d

    async def close(self) -> None:
        pass


def _change(change_type: str, change: Any, version: int = 1, stream_id: str = "s1") -> Delivery:
    return Delivery(
        id="d1", sender="ACME/X", target="py/test", request_id=None,
        message_type="DataChangedEvent",
        message={"$type": "DataChangedEvent", "streamId": stream_id, "version": version,
                 "change": change, "changeType": change_type, "changedBy": None},
        raw={})


# ---- the client-side folds --------------------------------------------------------------------------

def test_merge_patch_is_rfc7396():
    base = {"a": 1, "b": {"x": 1, "y": 2}, "c": 3}
    assert merge_patch(base, {"b": {"y": None, "z": 9}, "c": 4}) == {"a": 1, "b": {"x": 1, "z": 9}, "c": 4}
    assert merge_patch(base, "scalar") == "scalar"


def test_apply_json_patch_is_rfc6902():
    base = {"content": {"content": "old", "keep": 1}, "version": 5}
    # The exact op shape captured on the live wire (2026-07-03).
    ops = [{"op": "replace", "path": "/content/content", "value": "new"},
           {"op": "replace", "path": "/version", "value": 6},
           {"op": "add", "path": "/description", "value": "d"},
           {"op": "remove", "path": "/content/keep"}]
    assert apply_json_patch(base, ops) == {
        "content": {"content": "new"}, "version": 6, "description": "d"}
    # The input is NOT mutated (folds must be pure — the previous state may still be referenced).
    assert base["content"]["keep"] == 1


# ---- REST verbs (the /api/mesh surface) -------------------------------------------------------------

async def test_search_posts_the_search_verb_and_unwraps_results():
    rest = FakeRest()
    rest.responses["search"] = json.dumps(
        {"count": 1, "limit": 50, "truncated": False,
         "results": [{"path": "ACME/Stories/1", "name": "S1", "nodeType": "Story"}]})
    results = await Mesh(rest=rest).search("nodeType:Story", base_path="ACME")
    assert results[0]["path"] == "ACME/Stories/1"
    verb, body = rest.calls[0]
    assert verb == "search"
    assert body == {"query": "nodeType:Story", "basePath": "ACME", "limit": 50}


async def test_query_nodes_returns_full_payloads():
    rest = FakeRest()
    rest.responses["query-nodes"] = json.dumps(
        {"count": 1, "results": [{"path": "ACME/X", "content": {"done": False}}]})
    results = await Mesh(rest=rest).query_nodes("path:ACME/*")
    assert results[0]["content"] == {"done": False}


async def test_get_uses_rest_when_configured():
    rest = FakeRest()
    rest.responses["get"] = json.dumps({"path": "ACME/X", "name": "X", "content": {"done": False}})
    node = await Mesh(rest=rest).get("ACME/X")
    assert node.path == "ACME/X"
    assert node.content["done"] is False


async def test_patch_serializes_fields_as_a_json_string():
    rest = FakeRest()
    await Mesh(rest=rest).patch("ACME/X", {"content": {"done": True}})
    verb, body = rest.calls[0]
    assert verb == "patch"
    assert body["path"] == "ACME/X"
    assert json.loads(body["fields"]) == {"content": {"done": True}}


async def test_lifecycle_verbs_route_to_their_endpoints():
    rest = FakeRest()
    mesh = Mesh(rest=rest)
    await mesh.create({"path": "ACME/New"})
    await mesh.update([{"path": "ACME/New"}])
    await mesh.delete("ACME/Old")
    await mesh.move("ACME/A", "ACME/B")
    await mesh.copy("ACME/A", "Elsewhere")
    verbs = [v for v, _ in rest.calls]
    assert verbs == ["create", "update", "delete", "move", "copy"]
    assert json.loads(rest.calls[2][1]["paths"]) == ["ACME/Old"]   # delete takes a JSON array
    assert rest.calls[3][1] == {"sourcePath": "ACME/A", "targetPath": "ACME/B"}
    assert rest.calls[4][1] == {"sourcePath": "ACME/A", "targetNamespace": "Elsewhere"}


async def test_create_or_update_falls_back_to_update_on_already_exists():
    class ExistsRest(FakeRest):
        async def post(self, verb: str, body: dict[str, Any]) -> str:
            await super().post(verb, body)
            if verb == "create":
                return "Error: node at ACME/X already exists."
            return "Updated ACME/X"

    rest = ExistsRest()
    result = await Mesh(rest=rest).create_or_update({"path": "ACME/X"})
    assert result == "Updated ACME/X"
    assert [v for v, _ in rest.calls] == ["create", "update"]


async def test_error_sentinels_raise_mesh_error():
    rest = FakeRest()
    rest.responses["get"] = "Not found: ACME/Missing"
    with pytest.raises(MeshError, match="Not found"):
        await Mesh(rest=rest).get("ACME/Missing")


async def test_execute_flips_the_control_plane_trigger():
    rest = FakeRest()
    await Mesh(rest=rest).execute("ACME/Jobs/import")
    verb, body = rest.calls[0]
    assert verb == "patch"
    assert json.loads(body["fields"]) == {"content": {"requestedStatus": "Running"}}


# ---- watch: the synchronization protocol over the participant connection ----------------------------

async def test_watch_speaks_the_sync_protocol_and_folds_changes():
    conn = FakeConn([
        _change("Full", {"path": "ACME/X", "name": "X", "content": {"done": False, "n": 1}}, version=1),
        _change("Full", {"path": "ACME/X", "name": "X", "content": {"done": False, "n": 1}}, version=1),
        # Patch frames are RFC 6902 op arrays on the live wire; duplicates share the version.
        _change("Patch", [{"op": "replace", "path": "/content/done", "value": True}], version=2),
        _change("Patch", [{"op": "replace", "path": "/content/done", "value": True}], version=2),
    ])
    seen = []
    async for node in Mesh(conn).watch("ACME/X"):
        seen.append(node)

    # SubscribeRequest went to the node's own address with a MeshNodeReference + our subscriber address.
    target, subscribe_type, msg = conn.subscribed[0]
    assert target == "ACME/X"
    assert subscribe_type == "SubscribeRequest"
    assert msg["reference"] == {"$type": "MeshNodeReference"}
    assert msg["subscriber"] == "py/test"
    assert msg["streamId"]

    # Full replaces, Patch op-folds — content.n survives; duplicated frames dedup by version.
    assert [n.content for n in seen] == [{"done": False, "n": 1}, {"done": True, "n": 1}]

    # The stream is released on exit.
    target, message_type, message = conn.posted[-1]
    assert (target, message_type) == ("ACME/X", "UnsubscribeRequest")
    assert message["streamId"] == msg["streamId"]


async def test_get_without_rest_takes_one_snapshot_off_the_stream():
    conn = FakeConn([_change("Full", {"path": "ACME/X", "content": {"done": False}})])
    node = await Mesh(conn).get("ACME/X")
    assert node.path == "ACME/X"
    # Even a single-snapshot get unsubscribes cleanly.
    assert conn.posted[-1][1] == "UnsubscribeRequest"
