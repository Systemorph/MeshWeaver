import asyncio

from memex_voice_gateway.ollama import SYSTEM_PROMPT, OllamaBrain, build_messages


def test_build_messages_prepends_system_and_trims_history():
    history = [{"role": "user", "content": f"q{i}"} for i in range(30)]
    messages = build_messages(history, "neu", max_turns=2)
    assert messages[0] == {"role": "system", "content": SYSTEM_PROMPT}
    assert len(messages) == 1 + 4 + 1  # system + last 2*2 history + new user message
    assert messages[-1] == {"role": "user", "content": "neu"}


def test_budget_miss_leaves_generation_running_and_second_wait_succeeds():
    async def scenario():
        brain = OllamaBrain("http://unused", "test-model")
        release = asyncio.Event()

        async def slow_chat(messages, handle=None):
            await release.wait()
            return "Die Antwort."

        brain._chat = slow_chat  # inject a fake transport
        handle = await brain.ask("Frage?")
        assert await brain.await_reply(handle, 0.05) is None  # budget missed, still running
        release.set()
        reply = await brain.await_reply(handle, 1.0)          # announce path retries the handle
        assert reply == "Die Antwort."
        assert brain._history[-1] == {"role": "assistant", "content": "Die Antwort."}
        await brain.close()

    asyncio.run(scenario())


def test_history_resets_after_idle_window():
    async def scenario():
        brain = OllamaBrain("http://unused", "test-model", idle_minutes=0)

        async def chat(messages, handle=None):
            return "ok"

        brain._chat = chat
        first = await brain.ask("erste Frage")
        await brain.await_reply(first, 1.0)
        brain._last_used -= 1  # idle_minutes=0 → any elapsed time exceeds the window
        await brain.ask("zweite Frage")
        assert [m["content"].split("] ", 1)[1] for m in brain._history] == ["zweite Frage"]
        await brain.close()

    asyncio.run(scenario())


def test_real_time_anchor_in_messages():
    import datetime
    from memex_voice_gateway.ollama import now_line
    stamp = now_line(datetime.datetime(2026, 8, 18, 21, 30))
    assert stamp == "Current time: Tuesday, 18 August 2026, 21:30."
    messages = build_messages([], "[21:30] Frage", now=stamp)
    assert stamp in messages[0]["content"] and messages[0]["role"] == "system"
