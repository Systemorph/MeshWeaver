"""IMessageDelivery envelope (de)serialization.

The mesh carries every message as a System.Text.Json ``IMessageDelivery`` — protobuf only frames
it (see ``mesh.proto``). This module is the ONE place that builds/parses that JSON, so when the exact
wire shape is pinned it changes here and nowhere else.

Parsing is resilient (reads the few fields we need case-insensitively). Building is the side that must
match the server's ``JsonSerializer.Deserialize<IMessageDelivery>`` exactly:

* The delivery's ``$type`` is the RawJson-typed envelope (see ``DELIVERY_TYPE``) — the payload under
  ``message`` carries its own ``$type`` (the registered short name of the request, e.g.
  ``"PatchDataRequest"``), which the HANDLING hub deserializes late; an unregistered payload type
  simply round-trips as raw JSON.
* ``sender`` / ``target`` serialize as plain Address strings (``"type/id"``); property casing is
  camelCase except the ``properties`` dictionary keys (verbatim, e.g. ``RequestId``).

Pinned against the canonical capture from ``MeshGrpcTransportTest`` (2026-07-03)::

    {"$type":"MessageDelivery`1[RawJson]",
     "message":{"$type":"EchoResponse","text":"hello mesh"},
     "id":"…","sender":"mesh/…","target":"py/…","properties":{"RequestId":"…"}}
"""
from __future__ import annotations

import json
from dataclasses import dataclass
from typing import Any, Optional

# $type of the delivery envelope. The server serializes deliveries whose payload travelled the mesh
# as MessageDelivery`1[RawJson]; a client-built delivery uses the same envelope and the receiving
# hub late-deserializes `message` by ITS $type. (Backtick-1 = the CLR constructed-generic name.)
DELIVERY_TYPE = "MessageDelivery`1[RawJson]"
TYPE_KEY = "$type"
REQUEST_ID = "RequestId"


def build_deliver(
    *, delivery_id: str, sender: str, target: str, message_type: str, message: dict[str, Any],
    request_id: Optional[str] = None, access_context: Optional[dict[str, Any]] = None,
) -> str:
    """Serialize a participant->mesh delivery to the JSON the server deserializes.

    ``request_id`` set => this is a RESPONSE: the server correlates it back to the original request by
    ``properties.RequestId`` (the same key ``observe`` keys responses on).

    ``access_context`` carries an identity on the delivery. Only meaningful on the TRUSTED loopback
    endpoint, where the server passes it through — a gate executing a user's request echoes the
    requester's context so its write-backs run under that user's identity. On token-authenticated
    connections the server re-stamps regardless (a forged identity is never trusted)."""
    envelope = {
        TYPE_KEY: DELIVERY_TYPE,
        "id": delivery_id,
        "sender": sender,
        "target": target,
        "message": {TYPE_KEY: message_type, **message},
        "properties": {REQUEST_ID: request_id} if request_id else {},
    }
    if access_context:
        envelope["accessContext"] = access_context
    return json.dumps(envelope)


@dataclass(frozen=True)
class Delivery:
    """The few envelope fields the SDK routes on."""

    id: Optional[str]
    sender: Optional[str]
    target: Optional[str]
    request_id: Optional[str]
    message_type: Optional[str]
    message: dict[str, Any]
    raw: dict[str, Any]
    access_context: Optional[dict[str, Any]] = None


def parse_delivery(payload: str) -> Delivery:
    """Parse a mesh->participant delivery JSON, tolerant of property casing."""
    root = json.loads(payload)
    props = _get(root, "properties") or {}
    message = _get(root, "message") or {}
    context = _get(root, "accessContext")
    return Delivery(
        id=_get(root, "id"),
        sender=_get(root, "sender"),
        target=_get(root, "target"),
        request_id=_get(props, REQUEST_ID),
        message_type=message.get(TYPE_KEY) if isinstance(message, dict) else None,
        message=message if isinstance(message, dict) else {},
        raw=root,
        access_context=context if isinstance(context, dict) else None,
    )


def _get(obj: Any, key: str) -> Any:
    """Case-insensitive single-key lookup (camelCase vs PascalCase resilience)."""
    if not isinstance(obj, dict):
        return None
    if key in obj:
        return obj[key]
    low = key.lower()
    for k, v in obj.items():
        if k.lower() == low:
            return v
    return None
