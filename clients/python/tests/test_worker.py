"""The Python kernel worker: the pure execution core + the SubmitCodeRequest handler over a fake conn."""
import asyncio
import contextlib
from typing import Any

import pytest

from meshweaver.envelope import Delivery
from meshweaver.worker import CodeWorker, execute_python


# ---- execute_python (pure) ------------------------------------------------

def test_captures_stdout():
    r = execute_python("print('hello from python')")
    assert r.ok
    assert r.stdout.strip() == "hello from python"
    assert r.return_value is None


def test_returns_trailing_expression():
    r = execute_python("x = 6\ny = 7\nx * y")
    assert r.ok
    assert r.return_value == 42


def test_binds_inputs():
    r = execute_python("Inputs['n'] * 10", inputs={"n": 5})
    assert r.return_value == 50


def test_non_json_return_is_stringified():
    r = execute_python("object()")
    assert r.ok
    assert isinstance(r.return_value, str)  # repr fallback, write-back never fails


def test_error_is_captured_not_raised():
    r = execute_python("1 / 0")
    assert not r.ok
    assert "ZeroDivisionError" in r.error
    assert r.return_value is None


# ---- CodeWorker.handle (over a fake connection) ---------------------------

class FakeConn:
    def __init__(self) -> None:
        self.posts: list[tuple[str, str, dict[str, Any]]] = []
        self.post_contexts: list[Any] = []
        self.responses: list[tuple[Delivery, str, dict[str, Any]]] = []
        self.handler = None

    def serve(self, handler) -> None:
        self.handler = handler

    async def post(self, target: str, message_type: str, message: dict[str, Any], access_context: Any = None) -> None:
        self.posts.append((target, message_type, message))
        self.post_contexts.append(access_context)

    async def respond(self, to: Delivery, message_type: str, message: dict[str, Any]) -> None:
        self.responses.append((to, message_type, message))


def _submit(code: str, activity: str = "ACME/_Activity/1", access_context: Any = None) -> Delivery:
    return Delivery(
        id="req-1", sender="@code/ACME/Source/1", target="py/python-kernel", request_id=None,
        message_type="SubmitCodeRequest",
        message={"$type": "SubmitCodeRequest", "code": code, "activityLogPath": activity},
        raw={},
        access_context=access_context,
    )


async def test_worker_registers_handler_on_construction():
    conn = FakeConn()
    CodeWorker(conn)
    assert conn.handler is not None  # serve() wired


async def test_handle_executes_and_writes_back_to_activity():
    conn = FakeConn()
    worker = CodeWorker(conn)
    await worker.handle(_submit("print('hi')\n40 + 2"))
    # Patched the Activity node subscribers watch.
    target, message_type, message = conn.posts[0]
    assert target == "ACME/_Activity/1"
    assert message_type == "PatchDataRequest"
    assert message["reference"] == {"$type": "MeshNodeReference"}
    content = message["patch"]["content"]
    assert content["status"] == "Succeeded"
    assert content["returnValue"] == 42
    assert any("hi" in m["message"] for m in content["messages"])
    # And replied to the requester.
    to, resp_type, resp = conn.responses[0]
    assert resp_type == "SubmitCodeResponse"
    assert resp["status"] == "Succeeded"


async def test_handle_reports_failure_status():
    conn = FakeConn()
    await CodeWorker(conn).handle(_submit("raise ValueError('boom')"))
    content = conn.posts[0][2]["patch"]["content"]
    assert content["status"] == "Failed"
    assert any("ValueError" in m["message"] for m in content["messages"])


async def test_write_back_echoes_the_requesters_access_context():
    # On the TRUSTED loopback endpoint the server passes a carried accessContext through, so the
    # gate's activity write-back must echo the identity of the user whose Code node this run is —
    # the worker acts on the user's behalf, like the in-process C# kernel.
    conn = FakeConn()
    requester = {"$type": "AccessContext", "objectId": "alice", "name": "Alice"}
    await CodeWorker(conn).handle(_submit("1 + 1", access_context=requester))
    assert conn.post_contexts == [requester]


async def test_handle_ignores_non_submit_requests():
    conn = FakeConn()
    other = Delivery(id="x", sender="s", target="py/python-kernel", request_id=None,
                     message_type="SomethingElse", message={"$type": "SomethingElse"}, raw={})
    await CodeWorker(conn).handle(other)
    assert conn.posts == []
    assert conn.responses == []


# ---- serve() reconnect resilience (the co-deployed gate mode) ---------------------------------------

class _GateConn:
    """A connected participant whose stream stays open until close() — drives serve()'s wait loop."""
    address = "py/python-kernel"

    def __init__(self):
        self._closed = asyncio.Event()

    def serve(self, handler):  # CodeWorker registers its inbound handler here
        pass

    async def wait_closed(self):
        await self._closed.wait()

    async def close(self):
        self._closed.set()


async def test_serve_reconnects_past_connect_failures():
    # The gate starts before the portal binds the trusted endpoint: connect fails twice, then
    # succeeds. reconnect=True retries instead of crashing (the crash-restart we saw in the pod).
    import meshweaver.worker as w

    attempts = {"n": 0}
    conn = _GateConn()

    async def fake_connect(url, token=None, address=None):
        attempts["n"] += 1
        if attempts["n"] < 3:
            raise ConnectionError("portal not ready")
        return conn

    orig_connect, orig_sleep = w._connect, w._sleep_or_stop
    w._connect = fake_connect
    w._sleep_or_stop = lambda stop, seconds: asyncio.sleep(0)  # no real backoff in the test
    try:
        task = asyncio.ensure_future(w.serve("http://127.0.0.1:8082", reconnect=True, retry_seconds=0))
        for _ in range(500):  # let it retry past the two failures and reach the serving state
            if attempts["n"] >= 3:
                break
            await asyncio.sleep(0)
        assert attempts["n"] >= 3  # it did NOT crash on the failures — it retried and connected
        task.cancel()
        with contextlib.suppress(asyncio.CancelledError):
            await task
    finally:
        w._connect, w._sleep_or_stop = orig_connect, orig_sleep


async def test_serve_without_reconnect_raises_on_connect_failure():
    # Default (script/test) behaviour is unchanged: a connect failure propagates, no retry loop.
    import meshweaver.worker as w
    orig = w._connect

    async def boom(url, token=None, address=None):
        raise ConnectionError("no portal")

    w._connect = boom
    try:
        with pytest.raises(ConnectionError):
            await w.serve("http://127.0.0.1:8082")  # reconnect defaults to False
    finally:
        w._connect = orig
