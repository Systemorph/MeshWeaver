"""The Python kernel worker: the pure execution core + the SubmitCodeRequest handler over a fake conn."""
from typing import Any

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
        self.responses: list[tuple[Delivery, str, dict[str, Any]]] = []
        self.handler = None

    def serve(self, handler) -> None:
        self.handler = handler

    async def post(self, target: str, message_type: str, message: dict[str, Any]) -> None:
        self.posts.append((target, message_type, message))

    async def respond(self, to: Delivery, message_type: str, message: dict[str, Any]) -> None:
        self.responses.append((to, message_type, message))


def _submit(code: str, activity: str = "ACME/_Activity/1") -> Delivery:
    return Delivery(
        id="req-1", sender="@code/ACME/Source/1", target="py/python-kernel", request_id=None,
        message_type="SubmitCodeRequest",
        message={"$type": "SubmitCodeRequest", "code": code, "activityLogPath": activity},
        raw={},
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
    content = message["change"]["content"]
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
    content = conn.posts[0][2]["change"]["content"]
    assert content["status"] == "Failed"
    assert any("ValueError" in m["message"] for m in content["messages"])


async def test_handle_ignores_non_submit_requests():
    conn = FakeConn()
    other = Delivery(id="x", sender="s", target="py/python-kernel", request_id=None,
                     message_type="SomethingElse", message={"$type": "SomethingElse"}, raw={})
    await CodeWorker(conn).handle(other)
    assert conn.posts == []
    assert conn.responses == []
