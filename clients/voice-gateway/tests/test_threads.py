import json

import pytest

from memex_voice_gateway.threads import McpError, extract_reply, parse_tool_result


def rpc_result(text: str) -> str:
    return json.dumps({"jsonrpc": "2.0", "id": 9,
                       "result": {"content": [{"type": "text", "text": text}]}})


def test_parse_plain_json_result():
    assert parse_tool_result(rpc_result("hello")) == "hello"


def test_parse_sse_wrapped_result():
    body = "event: message\ndata: " + rpc_result("hello") + "\n\n"
    assert parse_tool_result(body) == "hello"


def test_parse_rpc_error_raises():
    body = json.dumps({"jsonrpc": "2.0", "id": 9, "error": {"code": -32000, "message": "nope"}})
    with pytest.raises(McpError):
        parse_tool_result(body)


def test_parse_tool_level_error_raises():
    body = json.dumps({"jsonrpc": "2.0", "id": 9,
                       "result": {"isError": True,
                                  "content": [{"type": "text", "text": "agent exploded"}]}})
    with pytest.raises(McpError, match="agent exploded"):
        parse_tool_result(body)


# --- extract_reply: fixtures mirror live thread nodes observed on memex 2026-08-18 ---

def thread_node(messages, pending=None, summary=None) -> str:
    content = {"$type": "Thread", "messages": messages,
               "pendingUserMessages": pending or {}}
    if summary is not None:
        content["summary"] = summary
    return json.dumps({"$type": "MeshNode", "content": content})


def test_new_message_ids_are_reported():
    new_ids, failure = extract_reply(thread_node(["u1", "a1"]), known_ids={"u1"})
    assert new_ids == ["a1"] and failure is None


def test_dispatch_failure_surfaces_via_summary():
    node = thread_node(["u1"], summary="Selected agent 'Voice' was not found")
    new_ids, failure = extract_reply(node, known_ids={"u1"})
    assert new_ids == [] and "not found" in failure


def test_pending_round_suppresses_stale_summary():
    pending = {"u2": {"role": "user", "text": "next question"}}
    node = thread_node(["u1"], pending=pending, summary="an old digest")
    _, failure = extract_reply(node, known_ids={"u1"})
    assert failure is None


def test_await_reply_skips_in_progress_placeholder():
    """The platform materializes an assistant child MID-round ('Generating response...',
    InProgress) whose text later becomes the real answer — it must be neither read aloud
    nor marked seen."""
    import asyncio

    from memex_voice_gateway.threads import MemexThreads

    async def scenario():
        client = MemexThreads("http://unused", "t", "ns")
        phase = {"n": 0}

        async def fake_call(tool, arguments):
            path = arguments.get("path", "")
            if path == "@ns/_Thread/t1":
                return json.dumps({"content": {"messages": ["m1"]}})
            phase["n"] += 1
            if phase["n"] == 1:
                return json.dumps({"content": {"role": "assistant",
                                               "text": "Generating response...",
                                               "status": "InProgress"}})
            return json.dumps({"content": {"role": "assistant",
                                           "text": "Es sind 26 Kantone.",
                                           "status": "Completed"}})

        client.call = fake_call
        reply = await client.await_reply("ns/_Thread/t1", budget_s=5, poll_interval_s=0.01)
        assert reply == "Es sind 26 Kantone."

    asyncio.run(scenario())
