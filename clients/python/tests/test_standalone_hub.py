"""The standalone Python hub: handler dispatch by message type (the C# WithHandler<T> mirror),
error propagation instead of wedging, and the load-from-mesh / save-back-to-mesh loop — all driven
through recording fakes, the same duck-typing the other example tests use."""
from typing import Any

from meshweaver.envelope import Delivery
from meshweaver.examples.standalone_hub import (
    DemoMesh,
    NamespaceStatsHub,
    PyHub,
    RecordingConnection,
    run_demo,
)


def _delivery(message_type: str, message: dict[str, Any] | None = None) -> Delivery:
    return Delivery(id="d1", sender="py/caller", target="py/stats", request_id=None,
                    message_type=message_type, message={"$type": message_type, **(message or {})}, raw={})


# ---- PyHub: the hub programming model ---------------------------------------------------------------

async def test_registered_handler_answers_with_a_correlated_response():
    conn = RecordingConnection()
    hub = PyHub(conn)

    async def on_ping(delivery: Delivery):
        return "PongResponse", {"echo": delivery.message.get("text")}

    hub.register("PingRequest", on_ping)
    await hub.handle(_delivery("PingRequest", {"text": "hello"}))
    assert conn.responses == [("PongResponse", {"echo": "hello"})]


async def test_unregistered_message_types_are_ignored():
    conn = RecordingConnection()
    PyHub(conn)
    await conn._handler(_delivery("SomeoneElsesRequest"))
    assert conn.responses == []


async def test_raising_handler_answers_error_response_never_wedges():
    conn = RecordingConnection()
    hub = PyHub(conn)

    async def broken(delivery: Delivery):
        raise ValueError("bad input")

    hub.register("PingRequest", broken)
    await hub.handle(_delivery("PingRequest"))
    message_type, payload = conn.responses[0]
    assert message_type == "ErrorResponse"
    assert "ValueError: bad input" in payload["error"]


# ---- NamespaceStatsHub: load from the mesh, serve, save back ----------------------------------------

async def test_load_reads_the_namespace_into_hub_state_and_stats_summarise_it():
    hub = NamespaceStatsHub(RecordingConnection(), DemoMesh(), "PythonDemo")
    stats = await hub.load()
    assert stats["nodeCount"] == 3
    assert stats["byNodeType"] == {"Markdown": 1, "NodeType": 2}
    assert stats["contentWords"] > 0


async def test_stats_request_is_served_from_held_state():
    conn = RecordingConnection()
    hub = NamespaceStatsHub(conn, DemoMesh(), "PythonDemo")
    await hub.load()
    message_type, stats = await conn.deliver("NamespaceStatsRequest")
    assert message_type == "NamespaceStatsResponse"
    assert stats["namespace"] == "PythonDemo"
    assert stats["nodeCount"] == 3


async def test_reload_rereads_the_mesh():
    conn = RecordingConnection()
    mesh = DemoMesh()
    hub = NamespaceStatsHub(conn, mesh, "PythonDemo")
    await hub.load()
    mesh.nodes["PythonDemo/New"] = {"path": "PythonDemo/New", "nodeType": "Markdown",
                                    "content": {"content": "fresh"}}
    _, stats = await conn.deliver("ReloadRequest")
    assert stats["nodeCount"] == 4


async def test_save_report_writes_the_knowledge_back_to_the_mesh():
    mesh = DemoMesh()
    hub = NamespaceStatsHub(RecordingConnection(), mesh, "PythonDemo")
    await hub.load()
    path = await hub.save_report()
    assert path == "PythonDemo/PythonHubReport"
    (report,) = mesh.upserts
    assert report["namespace"] == "PythonDemo"
    assert report["id"] == "PythonHubReport"
    body = report["content"]["content"]
    assert "**Nodes:** 3" in body
    assert "| Markdown | 1 |" in body


async def test_run_demo_is_the_full_loop():
    result = await run_demo()
    assert result["stats"]["nodeCount"] == 3
    assert result["report_path"] == "PythonDemo/PythonHubReport"
    assert result["report"]["nodeType"] == "Markdown"
