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
        if activity_path:
            await self._c.post(
                target=activity_path,
                message_type="PatchDataRequest",  # WIRE: confirm partial-update request type
                message={"path": activity_path, "change": {"content": {
                    "status": status, "messages": messages, "returnValue": result.return_value,  # WIRE: ActivityLog fields
                }}},
            )
        # And reply to the requester for request/response callers (e.g. the .NET dispatch awaiting a result).
        await self._c.respond(delivery, "SubmitCodeResponse", {  # WIRE: SubmitCodeResponse shape
            "status": status, "returnValue": result.return_value, "output": result.stdout, "error": result.error,
        })


async def serve(url: str, token: Optional[str] = None, address: str = DEFAULT_WORKER_ADDRESS) -> None:
    """Connect as a stable ``py/*`` worker and run until cancelled, executing Python Code-node submissions."""
    conn = await _connect(url, token=token, address=address)
    CodeWorker(conn)
    print(f"meshweaver python worker listening as {conn.address} -> {url}")
    try:
        await asyncio.Event().wait()  # run forever; inbound requests drive the worker
    finally:
        await conn.close()


def main() -> None:
    p = argparse.ArgumentParser(prog="meshweaver.worker", description="Run a Python kernel on the mesh.")
    p.add_argument("--url", required=True, help="portal gRPC endpoint, e.g. https://atioz.meshweaver.cloud")
    p.add_argument("--token", default=None, help="MeshWeaver API token (validated server-side)")
    p.add_argument("--address", default=DEFAULT_WORKER_ADDRESS, help="stable worker address the kernel targets")
    args = p.parse_args()
    asyncio.run(serve(args.url, token=args.token, address=args.address))


if __name__ == "__main__":
    main()
