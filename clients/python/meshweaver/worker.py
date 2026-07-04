"""A Python *kernel* on the mesh — the execution side of the .NET<->Python bridge.

The .NET kernel runs C# Code nodes in-process via Roslyn. A Code node whose ``Language == "python"`` has
no in-process runtime there, so the mesh routes its ``SubmitCodeRequest`` to a connected ``py/*`` worker
(this module). The worker executes the Python, captures stdout + the value of the trailing expression
(REPL-style, mirroring Roslyn's ``ScriptState.ReturnValue``), and writes the result back onto the
Activity node every subscriber already watches — so a Python Code node surfaces output exactly like a C#
one.

    python -m meshweaver.worker --url https://atioz.meshweaver.cloud --token mw_... --address py/python-kernel

Register a stable ``--address`` so the .NET kernel can target the worker. ``execute_python`` is a pure
function (no mesh) — that's what the tests pin; the I/O around it is a thin shell.

🔬 WIRE: the ActivityLog content fields and the SubmitCodeResponse shape are the mesh's contracts — pin
them against a live capture. The execution core is correct and tested.
"""
from __future__ import annotations

import argparse
import ast
import asyncio
import contextlib
import io
import json
import traceback
from dataclasses import dataclass
from typing import Any, Optional

from . import envelope
from .connection import MeshConnection, connect as _connect

DEFAULT_WORKER_ADDRESS = "py/python-kernel"


@dataclass(frozen=True)
class ExecResult:
    """The outcome of running a snippet: captured stdout, the trailing expression's value, a traceback."""

    stdout: str
    return_value: Any  # best-effort JSON-able
    error: Optional[str]  # formatted traceback if the snippet raised, else None

    @property
    def ok(self) -> bool:
        return self.error is None


def execute_python(code: str, inputs: Optional[dict[str, Any]] = None) -> ExecResult:
    """Run ``code`` in a fresh namespace, capturing stdout and the value of a trailing expression.

    Pure: no mesh, no I/O beyond the captured stdout — this is the testable execution core. ``inputs`` is
    exposed to the snippet as ``Inputs`` (the same name the C# kernel binds). A trailing bare expression
    becomes the return value (REPL semantics); any exception is caught and formatted, never raised."""
    namespace: dict[str, Any] = {"__name__": "__mesh__", "Inputs": dict(inputs or {})}
    out = io.StringIO()
    return_value: Any = None
    error: Optional[str] = None
    try:
        tree = ast.parse(code, mode="exec")
        trailing: Optional[ast.Expression] = None
        if tree.body and isinstance(tree.body[-1], ast.Expr):
            trailing = ast.Expression(tree.body.pop().value)  # peel the last bare expression
        with contextlib.redirect_stdout(out):
            exec(compile(tree, "<mesh-code>", "exec"), namespace)  # noqa: S102 — that's the worker's job
            if trailing is not None:
                return_value = eval(compile(trailing, "<mesh-code>", "eval"), namespace)  # noqa: S307
    except BaseException:  # surface EVERYTHING as output; a worker never wedges on a bad snippet
        error = traceback.format_exc()
    return ExecResult(stdout=out.getvalue(), return_value=_jsonable(return_value), error=error)


def _jsonable(value: Any) -> Any:
    """Best-effort: keep JSON-serializable values as-is, stringify the rest (so write-back never fails)."""
    if value is None:
        return None
    try:
        json.dumps(value)
        return value
    except (TypeError, ValueError):
        return repr(value)


class CodeWorker:
    """Handles ``SubmitCodeRequest`` deliveries: execute the Python, write the result to the Activity node."""

    def __init__(self, connection: MeshConnection):
        self._c = connection
        connection.serve(self.handle)

    async def handle(self, delivery: envelope.Delivery) -> None:
        if delivery.message_type != "SubmitCodeRequest":
            return  # not ours — ignore quietly (other request types may share the worker later)
        msg = delivery.message
        code = msg.get("code") or msg.get("Code") or ""
        activity_path = msg.get("activityLogPath") or msg.get("ActivityLogPath")
        inputs = msg.get("inputs") or msg.get("Inputs") or {}
        result = execute_python(code, inputs)
        await self._write_back(delivery, activity_path, result)

    async def _write_back(self, delivery: envelope.Delivery, activity_path: Optional[str], result: ExecResult) -> None:
        status = "Succeeded" if result.ok else "Failed"
        messages = [{"message": m} for m in (result.stdout, result.error) if m]  # WIRE: LogMessage shape
        # Mesh-native: patch the Activity node subscribers already watch (same surface as the C# kernel).
        # PatchDataRequest is (reference, patch): a MeshNodeReference targeted at the node's own hub,
        # with the RFC 7396 merge patch under `patch` (the pinned C# contract — PatchDataRequest.cs).
        # The requester's accessContext is echoed so, on the trusted endpoint, the write runs under
        # the identity of the user whose Code node this run is — like the in-process C# kernel.
        if activity_path:
            await self._c.post(
                target=activity_path,
                message_type="PatchDataRequest",
                message={"reference": {"$type": "MeshNodeReference"}, "patch": {"content": {
                    "status": status, "messages": messages, "returnValue": result.return_value,  # WIRE: ActivityLog fields
                }}},
                access_context=delivery.access_context,
            )
        # And reply to the requester for request/response callers (e.g. the .NET dispatch awaiting a result).
        await self._c.respond(delivery, "SubmitCodeResponse", {  # WIRE: SubmitCodeResponse shape
            "status": status, "returnValue": result.return_value, "output": result.stdout, "error": result.error,
        })


async def serve(url: str, token: Optional[str] = None, address: str = DEFAULT_WORKER_ADDRESS,
                *, reconnect: bool = False, retry_seconds: float = 3.0) -> None:
    """Connect as a stable ``py/*`` worker and run until cancelled, executing Python Code-node submissions.

    ``reconnect`` (the co-deployed **gate** mode): if the connection can't be established — the portal
    isn't up yet — or it drops later — the portal restarted on a deploy / liveness probe — wait
    ``retry_seconds`` and reconnect, instead of exiting. A gate outlives many portal restarts; the
    default (``False``) keeps the original single-shot behaviour for scripts and tests."""
    stop = asyncio.Event()
    _install_stop_signals(stop)
    while not stop.is_set():
        try:
            conn = await _connect(url, token=token, address=address)
        except Exception as ex:  # connect/ack failed — portal not ready, DNS, TLS, …
            if not reconnect:
                raise
            print(f"meshweaver python worker: connect to {url} failed "
                  f"({type(ex).__name__}: {ex}); retrying in {retry_seconds}s", flush=True)
            await _sleep_or_stop(stop, retry_seconds)
            continue
        CodeWorker(conn)
        print(f"meshweaver python worker listening as {conn.address} -> {url}", flush=True)
        try:
            # Run until asked to stop OR the connection drops (wait_closed returns).
            closed = asyncio.ensure_future(conn.wait_closed())
            stopping = asyncio.ensure_future(stop.wait())
            await asyncio.wait({closed, stopping}, return_when=asyncio.FIRST_COMPLETED)
            for task in (closed, stopping):
                task.cancel()
            # Await both so neither becomes an unobserved-exception warning (a cancelled task, or an
            # error surfaced by wait_closed, is captured here rather than left dangling).
            await asyncio.gather(closed, stopping, return_exceptions=True)
        finally:
            await conn.close()
        if not reconnect:
            break
        if not stop.is_set():
            print(f"meshweaver python worker: connection to {url} ended; "
                  f"reconnecting in {retry_seconds}s", flush=True)
            await _sleep_or_stop(stop, retry_seconds)


def _install_stop_signals(stop: "asyncio.Event") -> None:
    """SIGTERM / SIGINT → set the stop event, so a reconnect loop exits cleanly on pod termination.
    Best-effort: platforms without add_signal_handler (Windows) just fall back to default handling."""
    import signal
    loop = asyncio.get_running_loop()
    for sig in (signal.SIGTERM, signal.SIGINT):
        try:
            loop.add_signal_handler(sig, stop.set)
        except (NotImplementedError, ValueError, RuntimeError):
            pass


async def _sleep_or_stop(stop: "asyncio.Event", seconds: float) -> None:
    """Sleep ``seconds`` unless the stop event fires first (so shutdown isn't delayed by a backoff)."""
    try:
        await asyncio.wait_for(stop.wait(), timeout=seconds)
    except asyncio.TimeoutError:
        pass


def main() -> None:
    p = argparse.ArgumentParser(prog="meshweaver.worker", description="Run a Python kernel on the mesh.")
    p.add_argument("--url", required=True, help="portal gRPC endpoint, e.g. https://atioz.meshweaver.cloud")
    p.add_argument("--token", default=None, help="MeshWeaver API token (validated server-side)")
    p.add_argument("--address", default=DEFAULT_WORKER_ADDRESS, help="stable worker address the kernel targets")
    p.add_argument("--reconnect", action="store_true",
                   help="reconnect on connect failure / drop (the co-deployed gate mode) instead of exiting")
    p.add_argument("--retry-seconds", type=float, default=3.0, help="backoff between reconnects")
    args = p.parse_args()
    asyncio.run(serve(args.url, token=args.token, address=args.address,
                      reconnect=args.reconnect, retry_seconds=args.retry_seconds))


if __name__ == "__main__":
    main()
