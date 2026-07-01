"""IMessageDelivery envelope (de)serialization.

The mesh carries every message as a System.Text.Json ``IMessageDelivery`` — protobuf only frames
it (see ``mesh.proto``). This module is the ONE place that builds/parses that JSON, so when the exact
wire shape is pinned it changes here and nowhere else.

Parsing is resilient (reads the few fields we need case-insensitively). Building is the side that must
match the server's ``JsonSerializer.Deserialize<IMessageDelivery>`` exactly:

* The delivery object needs a ``$type`` discriminator that the server's ``ITypeRegistry`` resolves to
  the concrete ``MessageDelivery<TMessage>``; the payload under ``message`` needs its own ``$type``
  (the registered short name of the request, e.g. ``"GetNodeRequest"``).
* ``sender`` / ``target`` serialize as plain Address strings (``"type/id"``).

🔬 VALIDATION: the canonical sample is whatever the C# round-trip test
(``MeshGrpcTransportTest``) logs for a real request/response. Capture it, then confirm
``DELIVERY_TYPE`` and the property casing below. Everything else in the SDK is transport-level and
already correct.
"""
from __future__ import annotations

import json
from dataclasses import dataclass
from typing import Any, Optional

# $type token the server uses for the concrete delivery envelope. Confirm against a captured sample.
DELIVERY_TYPE = "MessageDelivery"
TYPE_KEY = "$type"
REQUEST_ID = "RequestId"


def build_deliver(
    *, delivery_id: str, sender: str, target: str, message_type: str, message: dict[str, Any],
    request_id: Optional[str] = None,
) -> str:
    """Serialize a participant->mesh delivery to the JSON the server deserializes.

    ``request_id`` set => this is a RESPONSE: the server correlates it back to the original request by
    ``properties.RequestId`` (the same key ``observe`` keys responses on)."""
    envelope = {
        TYPE_KEY: DELIVERY_TYPE,
        "id": delivery_id,
        "sender": sender,
        "target": target,
        "message": {TYPE_KEY: message_type, **message},
        "properties": {REQUEST_ID: request_id} if request_id else {},
    }
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


def parse_delivery(payload: str) -> Delivery:
    """Parse a mesh->participant delivery JSON, tolerant of property casing."""
    root = json.loads(payload)
    props = _get(root, "properties") or {}
    message = _get(root, "message") or {}
    return Delivery(
        id=_get(root, "id"),
        sender=_get(root, "sender"),
        target=_get(root, "target"),
        request_id=_get(props, REQUEST_ID),
        message_type=message.get(TYPE_KEY) if isinstance(message, dict) else None,
        message=message if isinstance(message, dict) else {},
        raw=root,
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
