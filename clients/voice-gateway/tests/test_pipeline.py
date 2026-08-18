import asyncio

from memex_voice_gateway.pipeline import VoicePipeline


def make_pipeline(**overrides):
    calls = {"announced": [], "spoken": []}

    async def transcribe(pcm):
        return overrides.get("transcript", "wie spät ist es")

    async def ask(text):
        return "u/_Thread/t1"

    async def await_reply(path, budget):
        return overrides.get("reply", "Es ist drei Uhr.")

    async def speak(text):
        calls["spoken"].append(text)
        return f"http://gw/tts/{len(calls['spoken'])}.wav"

    async def announce(url, text):
        calls["announced"].append(text)

    pipeline = VoicePipeline(
        transcribe=overrides.get("transcribe", transcribe),
        ask=overrides.get("ask", ask),
        await_reply=overrides.get("await_reply", await_reply),
        speak=speak, announce=announce,
        reply_budget_s=0.05, announce_budget_s=0.2,
        hold_phrase="Moment bitte.", error_phrase="Fehler.",
    )
    return pipeline, calls


def test_inline_reply_within_budget():
    pipeline, calls = make_pipeline()
    result = asyncio.run(pipeline.run(b"pcm"))
    assert result.reply == "Es ist drei Uhr."
    assert result.tts_url and calls["spoken"] == ["Es ist drei Uhr."]
    assert calls["announced"] == []


def test_budget_miss_holds_then_announces():
    attempts = []

    async def slow_reply(path, budget):
        attempts.append(budget)
        if len(attempts) == 1:
            return None            # inline budget missed
        return "Die Antwort."      # background poll succeeds

    async def scenario():
        pipeline, calls = make_pipeline(await_reply=slow_reply)
        result = await pipeline.run(b"pcm")
        assert result.reply is None
        assert calls["spoken"] == ["Moment bitte."]
        await asyncio.gather(*pipeline._background)
        return calls

    calls = asyncio.run(scenario())
    assert calls["announced"] == ["Die Antwort."]
    assert calls["spoken"] == ["Moment bitte.", "Die Antwort."]


def test_empty_transcript_ends_quietly():
    pipeline, calls = make_pipeline(transcript="")
    result = asyncio.run(pipeline.run(b"pcm"))
    assert result.reply is None and result.tts_url is None
    assert calls["spoken"] == [] and calls["announced"] == []


def test_stt_failure_speaks_error_phrase():
    async def broken(pcm):
        raise RuntimeError("stt down")

    pipeline, calls = make_pipeline(transcribe=broken)
    result = asyncio.run(pipeline.run(b"pcm"))
    assert result.reply == "Fehler."
    assert calls["spoken"] == ["Fehler."]
