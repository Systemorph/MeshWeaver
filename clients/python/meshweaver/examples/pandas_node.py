"""A Python-backed mesh node that HOLDS and CONTROLS a live pandas DataFrame.

This is the interactive successor to ``Doc/Architecture/CallingPython`` (the participant-model prose).
Where the kernel worker (:mod:`meshweaver.worker`) runs a Python snippet in a *fresh* namespace and throws
it away, a :class:`PandasNode` OWNS an in-process :class:`pandas.DataFrame` as real, long-lived Python
state and lets the mesh drive it:

* **create / replace / append / reset** — mutate the held frame,
* **groupby / describe / rolling** — analyse it (a pandas showcase),
* **render** — serialise its *current* state back as a real :class:`DataGridControl` UiControl (the exact
  wire JSON ``Controls.DataGrid(rows).WithColumn(...)`` produces on the C# side), so the existing Blazor
  and React grid renderers show a **live, sortable grid — never markdown or an HTML string**.

Two interaction surfaces, both against the SAME live frame:

1. Typed ``PandasCommand`` messages (``load`` / ``append`` / ``reset`` / ``render`` / ``groupby`` /
   ``describe`` / ``rolling``). The mesh routes them to this participant's address by target — the
   message type is opaque to the mesh (it round-trips as ``RawJson``), so no server-side registration
   is needed.
2. ``SubmitCodeRequest`` — pandas code executed against a **persistent** namespace where the frame lives
   as ``df``. Unlike :func:`meshweaver.worker.execute_python`, the namespace SURVIVES across calls, so
   ``df["margin"] = df["sales"] - df["cost"]`` in one submission is visible to the next. That persistent
   ``df`` IS "an object created and controlled in Python".

The canonical data source is **a file kept in mesh content**: a ``load`` command carrying a ``path``
(e.g. ``PythonDemo/SalesData``) makes the participant read that node over the mesh, parse the CSV it
holds with :func:`pandas.read_csv`, and replace the held frame — mesh → Python. Every analytical view
then flows back through the hub as a ``DataGridControl`` — Python → mesh → C# GUI.

Run the self-contained showcase (no mesh needed) — prints the DataGrid JSON the GUI would render::

    python -m meshweaver.examples.pandas_node --demo

Or serve as a real mesh participant that other participants / agents drive over gRPC::

    python -m meshweaver.examples.pandas_node --url https://memex.meshweaver.cloud --token mw_... --address py/pandas
"""
from __future__ import annotations

import argparse
import ast
import asyncio
import contextlib
import io
import json
import traceback
from typing import Any, Optional

import pandas as pd
import pandas.api.types as pdt

from .. import envelope
from ..connection import connect as _connect
from ..content import fenced_or_text, text_from_node
from ..mesh import Mesh
from ..worker import _jsonable

DEFAULT_ADDRESS = "py/pandas"

# Message-type discriminators. PandasCommand/PandasAck/PandasError are this node's own protocol —
# unregistered on the mesh, so they round-trip as RawJson and route purely by target address.
COMMAND_TYPE = "PandasCommand"
ACK_TYPE = "PandasAck"
ERROR_TYPE = "PandasError"
# GRID_TYPE is the mesh's own registered UiControl: the response message IS a DataGridControl, so a
# receiving hub deserialises it into MeshWeaver.Layout.DataGrid.DataGridControl and the GUI renders it.
GRID_TYPE = "DataGridControl"


# --- pandas -> DataGridControl (the UiControl wire contract) ---------------------------------------

def _column(name: str, dtype: Any) -> dict[str, Any]:
    """One grid column derived from a DataFrame column's dtype.

    The ``$type`` is the discriminator the mesh's TypeRegistry resolves to a
    ``PropertyColumnControl<T>`` — a constructed generic serialises as ``Name`1[Arg]`` (the CLR
    ``Type.Name`` of the open generic + the registered short name of the type argument; see
    ``TypeRegistry.FormatType``). Numeric columns carry a .NET format string so the grid right-aligns
    and thousands-separates them exactly as a hand-written C# column would."""
    if pdt.is_bool_dtype(dtype):
        clr, fmt = "Boolean", None
    elif pdt.is_integer_dtype(dtype):
        clr, fmt = "Int64", "N0"
    elif pdt.is_float_dtype(dtype):
        clr, fmt = "Double", "N2"
    elif pdt.is_datetime64_any_dtype(dtype):
        clr, fmt = "DateTime", "yyyy-MM-dd"
    else:
        clr, fmt = "String", None
    col: dict[str, Any] = {
        "$type": f"PropertyColumnControl`1[{clr}]",
        "property": name,
        "title": name.replace("_", " ").strip().title(),
    }
    if fmt is not None:
        col["format"] = fmt
    return col


def frame_to_datagrid(df: pd.DataFrame) -> dict[str, Any]:
    """Serialise a DataFrame to the ``DataGridControl`` UiControl JSON the MeshWeaver GUI renders.

    * ``columns`` are derived from the frame's dtypes (typed ``PropertyColumnControl<T>`` + numeric format),
    * ``data`` are the frame's records, made JSON-safe by pandas (NaN → null, numpy scalars → JSON numbers,
      timestamps → ISO strings).

    The returned dict is the payload minus its ``$type`` (the caller stamps ``DataGridControl`` via
    :meth:`MeshConnection.respond`). It is the SAME shape ``Controls.DataGrid(rows).WithColumn(
    new PropertyColumnControl<double>{ Property = "sales" }.WithFormat("N2"))`` produces on the C# side."""
    columns = [_column(str(name), dtype) for name, dtype in df.dtypes.items()]
    data = json.loads(df.to_json(orient="records", date_format="iso"))
    return {"data": data, "columns": columns}


# --- a file kept in content -> DataFrame (mesh -> Python) ------------------------------------------

def csv_from_markdown(text: str) -> str:
    """The CSV inside ``text``: the first fenced code block if one is present, else the text itself.

    A CSV file kept in content is typically a Markdown node whose body wraps the data in a
    ```` ```csv ```` fence (renders as a code block in the portal, stays hand-editable). Prose around
    the fence is ignored; a node that stores the bare CSV works too."""
    return fenced_or_text(text)


def csv_from_node(node: Any) -> str:
    """Extract the CSV text a mesh node keeps in its content — see :func:`meshweaver.content.text_from_node`."""
    try:
        return text_from_node(node)
    except ValueError:
        raise ValueError("node carries no textual content to read CSV from") from None


def frame_from_node(node: Any) -> pd.DataFrame:
    """Parse the CSV a node keeps in content into a DataFrame — the mesh → pandas edge."""
    return pd.read_csv(io.StringIO(csv_from_node(node)))


# --- the participant that owns the frame -----------------------------------------------------------

class PandasNode:
    """A mesh participant that OWNS an in-process pandas DataFrame and controls it over the mesh.

    Construct with a connected :class:`MeshConnection` (or any object exposing ``serve`` / ``respond`` /
    ``post`` — that's what the tests and the ``--demo`` driver pass). The frame is real Python state on
    ``self``; :meth:`handle` reacts to inbound deliveries and mutates / queries / renders it.

    ``node_reader`` is the mesh-read edge for ``load`` commands that carry a ``path`` (a file kept in
    content): an async callable ``path -> node``. On a real :class:`MeshConnection` it defaults to
    :meth:`meshweaver.mesh.Mesh.get`; tests inject a fake."""

    def __init__(self, connection: Any, frame: Optional[pd.DataFrame] = None, node_reader: Any = None):
        self._c = connection
        self._df = pd.DataFrame() if frame is None else frame.copy()
        self._ns: dict[str, Any] = {"pd": pd}  # persistent namespace for the SubmitCodeRequest surface
        if node_reader is None and hasattr(connection, "subscribe"):
            node_reader = Mesh(connection).get  # a real participant reads nodes off their live streams
        self._read_node = node_reader
        connection.serve(self.handle)

    @property
    def dataframe(self) -> pd.DataFrame:
        """The live frame — the object controlled in Python."""
        return self._df

    async def handle(self, delivery: "envelope.Delivery") -> None:
        """Dispatch an inbound delivery to the matching surface; ignore anything that isn't ours."""
        if delivery.message_type == COMMAND_TYPE:
            await self._handle_command(delivery)
        elif delivery.message_type == "SubmitCodeRequest":
            await self._handle_code(delivery)
        # else: not ours — another participant may share this address later.

    # ---- typed command surface --------------------------------------------------------------------

    async def _handle_command(self, delivery: "envelope.Delivery") -> None:
        msg = delivery.message
        command = str(msg.get("command") or msg.get("Command") or "").lower()
        path = msg.get("path") or msg.get("Path")
        try:
            if command == "load" and path:
                kind, payload = "ack", await self._load_from_content(str(path))
            else:
                kind, payload = self._apply(command, msg)
        except Exception as ex:  # a bad command never wedges the node — it replies with an error
            await self._c.respond(delivery, ERROR_TYPE, {"command": command, "error": f"{type(ex).__name__}: {ex}"})
            return
        await self._c.respond(delivery, GRID_TYPE if kind == "grid" else ACK_TYPE, payload)

    async def _load_from_content(self, path: str) -> dict[str, Any]:
        """mesh → Python: read the CSV file kept in content at ``path``, replace the held frame with it."""
        if self._read_node is None:
            raise RuntimeError("this node has no mesh connection to read content nodes from")
        node = await self._read_node(path)
        self._df = frame_from_node(node)
        return {**self._summary(), "source": path}

    def _apply(self, command: str, msg: dict[str, Any]) -> tuple[str, dict[str, Any]]:
        """Apply one command to the held frame, returning ``("grid"|"ack", payload)``.

        ``load`` / ``append`` / ``reset`` MUTATE the held frame; ``render`` / ``groupby`` / ``describe`` /
        ``rolling`` produce a DataGrid view (the analytical ones read the frame without mutating it)."""
        if command == "load":
            self._df = pd.DataFrame(msg.get("data") or [])
            return "ack", self._summary()
        if command == "append":
            rows = pd.DataFrame(msg.get("rows") or [])
            self._df = pd.concat([self._df, rows], ignore_index=True)
            return "ack", self._summary()
        if command == "reset":
            self._df = pd.DataFrame()
            return "ack", self._summary()
        if command == "render":
            return "grid", frame_to_datagrid(self._df)
        if command == "describe":
            desc = self._df.describe().reset_index().rename(columns={"index": "statistic"})
            return "grid", frame_to_datagrid(desc)
        if command == "groupby":
            by = msg.get("by")
            agg = msg.get("agg") or "sum"
            grouped = self._df.groupby(by).agg(agg, numeric_only=True).reset_index()
            return "grid", frame_to_datagrid(grouped)
        if command == "rolling":
            col = msg.get("column")
            window = int(msg.get("window") or 3)
            view = self._df.copy()
            view[f"{col}_rolling_mean"] = view[col].rolling(window=window).mean()
            return "grid", frame_to_datagrid(view)
        raise ValueError(f"unknown command '{command}'")

    def _summary(self) -> dict[str, Any]:
        return {"rowCount": int(len(self._df)), "columns": [str(c) for c in self._df.columns]}

    # ---- code surface (state persists across calls) -----------------------------------------------

    async def _handle_code(self, delivery: "envelope.Delivery") -> None:
        """Run a Python snippet against the PERSISTENT namespace, then reply + patch the Activity node.

        The held frame is exposed as ``df`` before the run and re-captured after, so a snippet that
        reassigns or mutates ``df`` changes the node's live state for every later call. This is the
        difference from :func:`meshweaver.worker.execute_python`, which uses a throwaway namespace."""
        msg = delivery.message
        code = msg.get("code") or msg.get("Code") or ""
        activity_path = msg.get("activityLogPath") or msg.get("ActivityLogPath")
        result = self._run_code(code)
        status = "Succeeded" if result["error"] is None else "Failed"
        if activity_path:
            messages = [{"message": m} for m in (result["output"], result["error"]) if m]
            # PatchDataRequest = (reference, patch) targeted at the node's own hub (PatchDataRequest.cs);
            # the requester's context is echoed (honoured on the trusted endpoint).
            await self._c.post(
                target=activity_path,
                message_type="PatchDataRequest",
                message={"reference": {"$type": "MeshNodeReference"}, "patch": {"content": {
                    "status": status, "messages": messages, "returnValue": result["returnValue"],
                }}},
                access_context=delivery.access_context,
            )
        await self._c.respond(delivery, "SubmitCodeResponse", {
            "status": status, "returnValue": result["returnValue"], "output": result["output"],
            "error": result["error"], "rowCount": int(len(self._df)),
        })

    def _run_code(self, code: str) -> dict[str, Any]:
        self._ns["df"] = self._df  # expose the held frame
        out = io.StringIO()
        return_value: Any = None
        error: Optional[str] = None
        try:
            tree = ast.parse(code, mode="exec")
            trailing = None
            if tree.body and isinstance(tree.body[-1], ast.Expr):
                trailing = ast.Expression(tree.body.pop().value)  # REPL: peel the trailing expression
            with contextlib.redirect_stdout(out):
                exec(compile(tree, "<pandas-node>", "exec"), self._ns)  # noqa: S102
                if trailing is not None:
                    return_value = eval(compile(trailing, "<pandas-node>", "eval"), self._ns)  # noqa: S307
        except BaseException:  # surface everything as output; a node never wedges on a bad snippet
            error = traceback.format_exc()
        held = self._ns.get("df")
        if isinstance(held, pd.DataFrame):
            self._df = held  # persist any reassignment/mutation of df back onto the held object
        return {"output": out.getvalue(), "returnValue": _jsonable(return_value), "error": error}


# --- the interactive, self-contained showcase ------------------------------------------------------

def sample_sales_frame() -> pd.DataFrame:
    """A tiny monthly sales frame across two regions — the pandas showcase dataset."""
    return pd.DataFrame({
        "month": ["Jan", "Feb", "Mar", "Apr", "May", "Jun"],
        "region": ["EMEA", "EMEA", "EMEA", "APAC", "APAC", "APAC"],
        "sales": [120.0, 135.5, 128.0, 98.0, 143.0, 150.0],
        "units": [12, 14, 13, 9, 15, 16],
    })


class RecordingConnection:
    """An in-process stand-in for :class:`MeshConnection` that records what the node would send.

    Lets the demo (and the tests) drive a :class:`PandasNode` through the exact same handler path a live
    mesh would, without a gRPC channel — the same duck-typing the SDK's own tests use."""

    def __init__(self) -> None:
        self.responses: list[tuple[str, dict[str, Any]]] = []
        self.posts: list[tuple[str, str, dict[str, Any]]] = []
        self._handler = None

    def serve(self, handler: Any) -> None:
        self._handler = handler

    async def respond(self, to: Any, message_type: str, message: dict[str, Any]) -> None:
        self.responses.append((message_type, message))

    async def post(self, target: str, message_type: str, message: dict[str, Any], access_context: Any = None) -> None:
        self.posts.append((target, message_type, message))

    async def send_command(self, node: "PandasNode", command: str, **fields: Any) -> tuple[str, dict[str, Any]]:
        """Route a PandasCommand to the node the way the mesh would, and return its response."""
        delivery = envelope.Delivery(
            id="demo", sender="py/demo-driver", target=DEFAULT_ADDRESS, request_id=None,
            message_type=COMMAND_TYPE, message={"$type": COMMAND_TYPE, "command": command, **fields}, raw={},
        )
        await node.handle(delivery)
        return self.responses[-1]


async def run_demo() -> dict[str, Any]:
    """Scripted end-to-end: create a frame → mutate it → render it → analyse it. Returns the payloads."""
    conn = RecordingConnection()
    node = PandasNode(conn)

    # 1) create the live frame in Python
    await conn.send_command(node, "load", data=json.loads(sample_sales_frame().to_json(orient="records")))
    started_rows = int(len(node.dataframe))

    # 2) MUTATE it over the mesh: append two months
    await conn.send_command(node, "append", rows=[
        {"month": "Jul", "region": "APAC", "sales": 161.0, "units": 17},
        {"month": "Aug", "region": "EMEA", "sales": 152.0, "units": 15},
    ])

    # 3) render the CURRENT frame back as a real DataGrid
    _, grid = await conn.send_command(node, "render")

    # 4) pandas showcase: groupby region (sum) + a 3-month rolling mean of sales
    _, grouped = await conn.send_command(node, "groupby", by="region", agg="sum")
    _, rolling = await conn.send_command(node, "rolling", column="sales", window=3)

    return {
        "started_rows": started_rows,
        "row_count": int(len(node.dataframe)),
        "grid": grid,
        "grouped": grouped,
        "rolling": rolling,
    }


async def serve(url: str, token: Optional[str] = None, address: str = DEFAULT_ADDRESS,
                *, reconnect: bool = False, retry_seconds: float = 3.0) -> None:
    """Connect as a stable ``py/*`` participant and serve the held frame until cancelled.

    ``reconnect`` (the co-deployed **gate** mode): if the connection can't be established — the portal
    isn't up yet — or it drops later — the portal restarted — wait ``retry_seconds`` and reconnect
    instead of exiting, so the participant outlives portal restarts. The default keeps the original
    single-shot behaviour for scripts and tests."""
    # Reuse the worker's tiny signal/backoff helpers — same reconnect machinery as the code gate.
    from meshweaver.worker import _install_stop_signals, _sleep_or_stop
    stop = asyncio.Event()
    _install_stop_signals(stop)
    while not stop.is_set():
        try:
            conn = await _connect(url, token=token, address=address)
        except Exception as ex:  # connect/ack failed — portal not ready, DNS, TLS, …
            if not reconnect:
                raise
            print(f"meshweaver pandas node: connect to {url} failed "
                  f"({type(ex).__name__}: {ex}); retrying in {retry_seconds}s", flush=True)
            await _sleep_or_stop(stop, retry_seconds)
            continue
        PandasNode(conn)
        print(f"meshweaver pandas node serving as {conn.address} -> {url}", flush=True)
        try:
            closed = asyncio.ensure_future(conn.wait_closed())
            stopping = asyncio.ensure_future(stop.wait())
            await asyncio.wait({closed, stopping}, return_when=asyncio.FIRST_COMPLETED)
            for task in (closed, stopping):
                task.cancel()
            await asyncio.gather(closed, stopping, return_exceptions=True)
        finally:
            await conn.close()
        if not reconnect:
            break
        if not stop.is_set():
            print(f"meshweaver pandas node: connection ended; reconnecting in {retry_seconds}s", flush=True)
            await _sleep_or_stop(stop, retry_seconds)


def main() -> None:
    p = argparse.ArgumentParser(prog="meshweaver.examples.pandas_node",
                                description="A Python-backed mesh node holding a live pandas DataFrame.")
    p.add_argument("--demo", action="store_true", help="run the self-contained scripted showcase (no mesh)")
    p.add_argument("--url", default=None, help="portal gRPC endpoint, e.g. https://memex.meshweaver.cloud")
    p.add_argument("--token", default=None, help="MeshWeaver API token (validated server-side)")
    p.add_argument("--address", default=DEFAULT_ADDRESS, help="stable participant address the mesh targets")
    p.add_argument("--reconnect", action="store_true", help="gate mode: reconnect instead of exiting when the portal restarts")
    p.add_argument("--retry-seconds", type=float, default=3.0, help="reconnect backoff (with --reconnect)")
    args = p.parse_args()

    if args.demo or not args.url:
        result = asyncio.run(run_demo())
        print(json.dumps(result["grid"], indent=2))
        print(f"\n# live frame now has {result['row_count']} rows "
              f"(started at {result['started_rows']}, appended 2 over the mesh)")
        return

    asyncio.run(serve(args.url, token=args.token, address=args.address,
                      reconnect=args.reconnect, retry_seconds=args.retry_seconds))


if __name__ == "__main__":
    main()
