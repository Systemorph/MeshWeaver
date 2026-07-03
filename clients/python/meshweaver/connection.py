"""The gRPC participant connection — the foreign-language counterpart of the SignalR client.

One bidirectional ``Open`` stream IS the mesh participant connection (see ``mesh.proto``). This module
owns the transport, request/response correlation, and live-stream demux; ``mesh.py`` layers the
ergonomic operations on top. Everything here is protocol-level and mirrors
``MeshWeaver.Connection.SignalR`` / ``MeshGrpcService`` exactly.
"""
from __future__ import annotations

import asyncio
import json
import uuid
from typing import Any, AsyncIterator, Awaitable, Callable, Optional

import grpc

from . import envelope
from ._generated import mesh_pb2, mesh_pb2_grpc


class MeshConnection:
    """A connected mesh participant. Use :func:`connect` to obtain one."""

    def __init__(self, channel: grpc.aio.Channel, address: str, token: Optional[str]):
        self._channel = channel
        self.address = address
        self._token = token
        self._stub = mesh_pb2_grpc.MeshStub(channel)
        self._call: Any = None
        self._send_q: asyncio.Queue = asyncio.Queue()
        self._pending: dict[str, asyncio.Future] = {}            # delivery.id -> response future
        self._subscriptions: dict[str, asyncio.Queue] = {}       # stream_id -> live change queue
        self._handler: Optional[Callable[["envelope.Delivery"], Awaitable[None]]] = None  # inbound requests
        self._ack = asyncio.Event()
        self._reader: Optional[asyncio.Task] = None
        self._writer: Optional[asyncio.Task] = None

    # ---- lifecycle -------------------------------------------------------

    async def _open(self) -> None:
        metadata = [("authorization", f"Bearer {self._token}")] if self._token else []
        self._call = self._stub.Open(metadata=metadata)
        self._reader = asyncio.create_task(self._read_loop())
        self._writer = asyncio.create_task(self._write_loop())
        # Register our address for inbound routing, then wait for the server's ack.
        await self._send_q.put(mesh_pb2.ClientFrame(connect=json.dumps(self.address)))
        await asyncio.wait_for(self._ack.wait(), timeout=30)

    async def close(self) -> None:
        await self._send_q.put(None)  # poison pill stops the writer
        if self._writer:
            await self._writer
        if self._reader:
            self._reader.cancel()
        await self._channel.close()

    async def __aenter__(self) -> "MeshConnection":
        return self

    async def __aexit__(self, *exc: Any) -> None:
        await self.close()

    # ---- pumps -----------------------------------------------------------

    async def _write_loop(self) -> None:
        # One writer owns the request stream (gRPC forbids concurrent writes).
        while True:
            frame = await self._send_q.get()
            if frame is None:
                break
            await self._call.write(frame)
        await self._call.done_writing()

    async def _read_loop(self) -> None:
        async for server_frame in self._call:
            kind = server_frame.WhichOneof("kind")
            if kind == "ack":
                self._ack.set()
            elif kind == "receive":
                self._dispatch(server_frame.receive)

    def _dispatch(self, payload: str) -> None:
        delivery = envelope.parse_delivery(payload)
        # 1) request/response correlation
        if delivery.request_id and delivery.request_id in self._pending:
            fut = self._pending.pop(delivery.request_id)
            if not fut.done():
                fut.set_result(delivery)
            return
        # 2) live-stream change → demux by StreamId carried in the change message
        stream_id = delivery.message.get("streamId") or delivery.message.get("StreamId")
        if stream_id and stream_id in self._subscriptions:
            self._subscriptions[stream_id].put_nowait(delivery)
            return
        # 3) inbound request (not a response, not a stream change) → the registered worker handler.
        #    Each runs as its own task so one slow handler can't stall the read loop.
        if self._handler is not None:
            asyncio.create_task(self._handler(delivery))

    # ---- primitives (mesh features build on these) -----------------------

    async def observe(self, target: str, message_type: str, message: dict[str, Any], timeout: float = 30) -> envelope.Delivery:
        """Post a request to ``target`` and await its response (correlated by RequestId)."""
        delivery_id = uuid.uuid4().hex
        fut: asyncio.Future = asyncio.get_running_loop().create_future()
        self._pending[delivery_id] = fut
        payload = envelope.build_deliver(
            delivery_id=delivery_id, sender=self.address, target=target,
            message_type=message_type, message=message,
        )
        await self._send_q.put(mesh_pb2.ClientFrame(deliver=payload))
        try:
            return await asyncio.wait_for(fut, timeout)
        finally:
            self._pending.pop(delivery_id, None)

    async def post(self, target: str, message_type: str, message: dict[str, Any],
                   access_context: Optional[dict[str, Any]] = None) -> None:
        """Fire-and-forget a message to ``target``. ``access_context`` carries an identity on the
        delivery (honoured only on the trusted loopback endpoint — see :func:`envelope.build_deliver`)."""
        payload = envelope.build_deliver(
            delivery_id=uuid.uuid4().hex, sender=self.address, target=target,
            message_type=message_type, message=message, access_context=access_context,
        )
        await self._send_q.put(mesh_pb2.ClientFrame(deliver=payload))

    async def respond(self, to: "envelope.Delivery", message_type: str, message: dict[str, Any]) -> None:
        """Reply to an inbound request — addressed back to its sender, correlated by its delivery id.
        The inbound request's ``accessContext`` is echoed, so on a trusted connection the response
        runs under the REQUESTER's identity (the gate acting on the user's behalf)."""
        payload = envelope.build_deliver(
            delivery_id=uuid.uuid4().hex, sender=self.address, target=to.sender or "",
            message_type=message_type, message=message, request_id=to.id,
            access_context=to.access_context,
        )
        await self._send_q.put(mesh_pb2.ClientFrame(deliver=payload))

    def serve(self, handler: Callable[["envelope.Delivery"], Awaitable[None]]) -> None:
        """Register a handler for inbound requests addressed to this participant (the worker role).

        Deliveries that are neither a correlated response nor a live-stream change are dispatched here —
        e.g. a ``SubmitCodeRequest`` the mesh routes to this ``py/*`` worker. Reply with :meth:`respond`,
        or write results straight to a node via the ergonomic ``Mesh`` ops."""
        self._handler = handler

    async def subscribe(self, target: str, stream_id: str, subscribe_type: str, subscribe_msg: dict[str, Any]) -> AsyncIterator[envelope.Delivery]:
        """Open a live stream: post a subscribe request, then yield each change addressed back to us."""
        q: asyncio.Queue = asyncio.Queue()
        self._subscriptions[stream_id] = q
        await self.post(target, subscribe_type, {**subscribe_msg, "streamId": stream_id})
        try:
            while True:
                yield await q.get()
        finally:
            self._subscriptions.pop(stream_id, None)


async def connect(url: str, token: Optional[str] = None, address: Optional[str] = None,
                  root_certificates: Optional[bytes] = None) -> MeshConnection:
    """Connect a Python process to the mesh as a participant.

    ``url`` is the portal gRPC endpoint:

    * ``https://memex.meshweaver.cloud`` — TLS through the portal ingress (the same-host
      ``/meshweaver.v1.Mesh`` gRPC route). ``root_certificates`` overrides the trust roots for
      self-signed local portals (pass the PEM bytes of the ingress cert, e.g. ``tls.crt`` from the
      cluster's TLS secret); omit it for real certificates.
    * ``http://127.0.0.1:8082`` — the TRUSTED loopback endpoint for gates that ship in the same
      deployment as the portal (same pod): h2c, no token needed — reachability is the
      authentication, and the server honours an ``accessContext`` carried on deliveries.

    ``token`` is a MeshWeaver API token; the server validates it and stamps every write with your
    identity. ``address`` defaults to a fresh ``py/<uuid>`` participant.
    """
    target = url.split("://", 1)[-1]
    if url.startswith("https://"):
        channel = grpc.aio.secure_channel(target, grpc.ssl_channel_credentials(root_certificates))
    else:
        channel = grpc.aio.insecure_channel(target)
    conn = MeshConnection(channel, address or f"py/{uuid.uuid4().hex}", token)
    await conn._open()
    return conn
