"""A full MeshWeaver hub programmed in Python — standalone, no .NET code anywhere in its loop.

A hub on the mesh is three things: an **address** deliveries route to, a set of **message handlers**
keyed by message type, and **state** the handlers own. :class:`PyHub` is exactly that, in Python —
the in-language mirror of the C# ``MessageHubConfiguration.WithHandler<T>(...)`` model:

* ``hub.register("SomeRequest", handler)`` — one handler per message type (C#: ``WithHandler<T>``),
* a handler returns ``(response_type, payload)`` and the hub responds, correlated, to the sender
  (C#: ``hub.Post(response, o => o.ResponseFor(request))``),
* a raising handler answers with an ``ErrorResponse`` instead of wedging — errors always propagate
  to the caller, never into silence,
* inbound dispatch is serialised by the connection's read loop; outbound requests go through
  ``observe`` (request/response) and ``post`` (fire-and-forget), exactly like any C# hub.

:class:`NamespaceStatsHub` is the working example on top: on start it **loads info from the mesh**
(every node in a namespace), serves statistics about them to any participant that asks, and **saves
its report back to the mesh** as a Markdown node — read, compute, write, all from Python.

Run the self-contained showcase (no mesh needed)::

    python -m meshweaver.examples.standalone_hub --demo

Or attach the hub to a live mesh::

    python -m meshweaver.examples.standalone_hub --url https://memex.meshweaver.cloud \
        --token mw_… --namespace PythonDemo --address py/stats
"""
from __future__ import annotations

import argparse
import asyncio
import json
from typing import Any, Awaitable, Callable, Optional

from .. import envelope
from ..connection import connect as _connect
from ..mesh import Mesh

DEFAULT_ADDRESS = "py/stats"

#: A handler takes the inbound delivery and returns ``(response_type, payload)`` or ``None``.
Handler = Callable[["envelope.Delivery"], Awaitable[Optional[tuple[str, dict[str, Any]]]]]


class PyHub:
    """The hub programming model, in Python: an address, handlers by message type, owned state.

    Construct with a connected :class:`~meshweaver.connection.MeshConnection` (or any object exposing
    ``serve`` / ``respond`` — the tests pass a recording fake). Handlers registered via
    :meth:`register` receive every delivery of their message type addressed to this hub."""

    def __init__(self, connection: Any):
        self._c = connection
        self._handlers: dict[str, Handler] = {}
        connection.serve(self.handle)

    def register(self, message_type: str, handler: Handler) -> "PyHub":
        """Register the handler for ``message_type`` — the C# ``WithHandler<T>`` of this hub."""
        self._handlers[message_type] = handler
        return self

    async def handle(self, delivery: "envelope.Delivery") -> None:
        """Dispatch one inbound delivery. Unknown types are ignored (another participant's traffic);
        a raising handler answers with an ``ErrorResponse`` — the hub never wedges, the caller
        always learns what happened."""
        handler = self._handlers.get(delivery.message_type)
        if handler is None:
            return
        try:
            reply = await handler(delivery)
        except Exception as ex:
            await self._c.respond(delivery, "ErrorResponse",
                                  {"error": f"{type(ex).__name__}: {ex}"})
            return
        if reply is not None:
            response_type, payload = reply
            await self._c.respond(delivery, response_type, payload)


# --- the working hub: load from the mesh, serve, save back ------------------------------------------

class NamespaceStatsHub(PyHub):
    """A standalone Python hub that knows one namespace of the mesh.

    * :meth:`load` — mesh → Python: reads every node in the namespace into hub state,
    * ``NamespaceStatsRequest`` → ``NamespaceStatsResponse`` — serves statistics over that state,
    * ``ReloadRequest`` — re-reads the namespace, then answers with fresh statistics,
    * :meth:`save_report` — Python → mesh: writes the statistics back as a Markdown node
      (``{namespace}/PythonHubReport``), so the hub's knowledge is itself mesh content."""

    def __init__(self, connection: Any, mesh: Any, namespace: str):
        super().__init__(connection)
        self._mesh = mesh
        self.namespace = namespace
        self.nodes: list[Any] = []
        self.register("NamespaceStatsRequest", self._on_stats)
        self.register("ReloadRequest", self._on_reload)

    # ---- mesh -> Python: the hub's state is loaded from the mesh -----------------------------------

    async def load(self) -> dict[str, Any]:
        """Read every node in the namespace into hub state and return the statistics."""
        hits = await self._mesh.search(f"namespace:{self.namespace}", limit=500)
        self.nodes = []
        for hit in hits:
            path = hit.get("path") or hit.get("Path")
            if path:
                self.nodes.append(await self._mesh.get(path))
        return self.stats()

    def stats(self) -> dict[str, Any]:
        """Statistics over the held state: node count, per-nodeType counts, total words of content."""
        by_type: dict[str, int] = {}
        words = 0
        for node in self.nodes:
            node_type = self._field(node, "nodeType", "NodeType", "node_type") or "?"
            by_type[node_type] = by_type.get(node_type, 0) + 1
            content = self._field(node, "content", "Content")
            if isinstance(content, dict):
                text = content.get("content") or content.get("Content")
                if isinstance(text, str):
                    words += len(text.split())
        return {"namespace": self.namespace, "nodeCount": len(self.nodes),
                "byNodeType": by_type, "contentWords": words}

    @staticmethod
    def _field(node: Any, *names: str) -> Any:
        for name in names:
            value = node.get(name) if isinstance(node, dict) else getattr(node, name, None)
            if value is not None:
                return value
        return None

    # ---- the served surface -------------------------------------------------------------------------

    async def _on_stats(self, delivery: "envelope.Delivery") -> tuple[str, dict[str, Any]]:
        return "NamespaceStatsResponse", self.stats()

    async def _on_reload(self, delivery: "envelope.Delivery") -> tuple[str, dict[str, Any]]:
        return "NamespaceStatsResponse", await self.load()

    # ---- Python -> mesh: the hub's knowledge becomes mesh content ----------------------------------

    async def save_report(self) -> str:
        """Write the statistics back to the mesh as a readable Markdown node; returns its path."""
        stats = self.stats()
        path = f"{self.namespace}/PythonHubReport"
        rows = "\n".join(f"| {t} | {n} |" for t, n in sorted(stats["byNodeType"].items()))
        body = (
            f"# Namespace report: {self.namespace}\n\n"
            f"Written by the standalone Python hub (`{DEFAULT_ADDRESS}`, "
            f"`meshweaver.examples.standalone_hub`).\n\n"
            f"- **Nodes:** {stats['nodeCount']}\n"
            f"- **Words of content:** {stats['contentWords']}\n\n"
            f"| Node type | Count |\n|---|---|\n{rows}\n"
        )
        await self._mesh.create_or_update({
            "id": "PythonHubReport",
            "namespace": self.namespace,
            "name": f"Python hub report — {self.namespace}",
            "nodeType": "Markdown",
            "description": f"Namespace statistics computed by the standalone Python hub ({stats['nodeCount']} nodes).",
            "content": {"$type": "MarkdownContent", "content": body},
        })
        return path


# --- run it -------------------------------------------------------------------------------------------

async def serve(url: str, token: Optional[str] = None, namespace: str = "PythonDemo",
                address: str = DEFAULT_ADDRESS) -> None:
    """Attach the hub to a live mesh: load the namespace, publish the report, serve until cancelled."""
    conn = await _connect(url, token=token, address=address)
    mesh = Mesh(conn, url=url, token=token)   # gRPC participant + the portal's REST verbs
    hub = NamespaceStatsHub(conn, mesh, namespace)
    stats = await hub.load()
    report_path = await hub.save_report()
    print(f"python hub serving as {conn.address} -> {url}")
    print(f"loaded {stats['nodeCount']} nodes from {namespace}; report saved to {report_path}")
    try:
        await asyncio.Event().wait()  # inbound NamespaceStatsRequest / ReloadRequest drive the hub
    finally:
        await conn.close()


class RecordingConnection:
    """In-process stand-in for a MeshConnection — records what the hub would send (the demo/tests)."""

    def __init__(self) -> None:
        self.responses: list[tuple[str, dict[str, Any]]] = []
        self._handler: Any = None

    def serve(self, handler: Any) -> None:
        self._handler = handler

    async def respond(self, to: Any, message_type: str, message: dict[str, Any]) -> None:
        self.responses.append((message_type, message))

    async def deliver(self, message_type: str, message: Optional[dict[str, Any]] = None) -> tuple[str, dict[str, Any]]:
        """Route a delivery to the hub the way the mesh would, and return its response."""
        delivery = envelope.Delivery(
            id="demo", sender="py/demo-driver", target=DEFAULT_ADDRESS, request_id=None,
            message_type=message_type, message={"$type": message_type, **(message or {})}, raw={},
        )
        await self._handler(delivery)
        return self.responses[-1]


class DemoMesh:
    """A tiny in-memory namespace, standing in for the live mesh in ``--demo`` mode."""

    def __init__(self) -> None:
        self.nodes = {
            "PythonDemo/SalesData": {"path": "PythonDemo/SalesData", "nodeType": "Markdown",
                                     "content": {"content": "month,region,sales,units " * 9}},
            "PythonDemo/PandasExplorer": {"path": "PythonDemo/PandasExplorer", "nodeType": "NodeType",
                                          "content": {"content": "an interactive pandas frontend"}},
            "PythonDemo/PrimeReport": {"path": "PythonDemo/PrimeReport", "nodeType": "NodeType",
                                       "content": {"content": "primes computed by python"}},
        }
        self.upserts: list[dict[str, Any]] = []

    async def search(self, query: str, limit: int = 500) -> list[dict[str, Any]]:
        return [{"path": p} for p in self.nodes]

    async def get(self, path: str) -> dict[str, Any]:
        return self.nodes[path]

    async def create_or_update(self, node: dict[str, Any]) -> dict[str, Any]:
        self.upserts.append(node)
        return {"status": "ok"}


async def run_demo() -> dict[str, Any]:
    """Scripted end-to-end without a mesh: load → serve a stats request → save the report."""
    conn = RecordingConnection()
    mesh = DemoMesh()
    hub = NamespaceStatsHub(conn, mesh, "PythonDemo")
    await hub.load()
    _, stats = await conn.deliver("NamespaceStatsRequest")
    report_path = await hub.save_report()
    return {"stats": stats, "report_path": report_path, "report": mesh.upserts[-1]}


def main() -> None:
    p = argparse.ArgumentParser(prog="meshweaver.examples.standalone_hub",
                                description="A full MeshWeaver hub programmed in Python.")
    p.add_argument("--demo", action="store_true", help="run the self-contained showcase (no mesh)")
    p.add_argument("--url", default=None, help="portal gRPC endpoint, e.g. https://memex.meshweaver.cloud")
    p.add_argument("--token", default=None, help="MeshWeaver API token (validated server-side)")
    p.add_argument("--namespace", default="PythonDemo", help="the namespace this hub loads and reports on")
    p.add_argument("--address", default=DEFAULT_ADDRESS, help="stable participant address of this hub")
    args = p.parse_args()

    if args.demo or not args.url:
        result = asyncio.run(run_demo())
        print(json.dumps(result["stats"], indent=2))
        print(f"\n# report node written to {result['report_path']}")
        return

    asyncio.run(serve(args.url, token=args.token, namespace=args.namespace, address=args.address))


if __name__ == "__main__":
    main()
