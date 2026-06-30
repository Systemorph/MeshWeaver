"""Envelope build/parse — the one place the IMessageDelivery JSON is shaped. Mirrors the JS SDKs' tests."""
import json

from meshweaver import envelope as e


def test_build_carries_type_and_nested_message_type():
    payload = e.build_deliver(
        delivery_id="d1", sender="node/a", target="mesh/main", message_type="QueryRequest", message={"query": "x"}
    )
    obj = json.loads(payload)
    assert obj[e.TYPE_KEY] == e.DELIVERY_TYPE
    assert obj["id"] == "d1"
    assert obj["sender"] == "node/a"
    assert obj["target"] == "mesh/main"
    assert obj["message"][e.TYPE_KEY] == "QueryRequest"  # the inner message carries its own $type
    assert obj["message"]["query"] == "x"
    assert obj["properties"] == {}


def test_round_trips_through_parse():
    d = e.parse_delivery(
        e.build_deliver(delivery_id="d2", sender="s", target="t", message_type="Ping", message={"n": 1})
    )
    assert d.id == "d2"
    assert d.sender == "s"
    assert d.target == "t"
    assert d.message_type == "Ping"
    assert d.message["n"] == 1


def test_extracts_request_id_and_is_casing_resilient():
    d = e.parse_delivery(
        json.dumps({"Id": "X", "Sender": "p", "Message": {"$type": "M"}, "Properties": {"RequestId": "r"}})
    )
    assert d.id == "X"
    assert d.sender == "p"
    assert d.request_id == "r"
    assert d.message_type == "M"
