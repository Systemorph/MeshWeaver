import asyncio

import aiohttp
import pytest

from memex_voice_gateway.ollama import OllamaBrain
from memex_voice_gateway.stt import wav_from_pcm
from memex_voice_gateway.tts import TtsFileServer, split_wav, streaming_wav_header


def test_split_wav_round_trip():
    pcm = b"\x01\x02" * 800
    got, rate = split_wav(wav_from_pcm(pcm, sample_rate=22050))
    assert got == pcm and rate == 22050


def test_streaming_header_shape():
    header = streaming_wav_header(22050)
    assert header[:4] == b"RIFF" and header[8:12] == b"WAVE" and len(header) == 44


@pytest.mark.asyncio
async def test_stream_endpoint_plays_chunks_as_they_arrive():
    server = TtsFileServer("127.0.0.1", 8391)
    await server.start()
    try:
        stream, url = server.open_stream(sample_rate=22050)

        async def feed():
            await stream.push(b"AA" * 100)
            await asyncio.sleep(0.05)
            await stream.push(b"BB" * 100)
            await stream.close()

        task = asyncio.create_task(feed())
        async with aiohttp.ClientSession() as http:
            async with http.get(url.replace("127.0.0.1:8391", "127.0.0.1:8391")) as response:
                assert response.status == 200
                body = await response.read()
        await task
        assert body[:4] == b"RIFF"
        assert body[44:] == b"AA" * 100 + b"BB" * 100

        # a stream is REPLAYABLE: the device's player sniffs the URL and re-requests it
        # for playback, so the second fetch must serve the full buffered audio again
        async with aiohttp.ClientSession() as http:
            async with http.get(url) as response:
                assert response.status == 200
                assert (await response.read())[44:] == b"AA" * 100 + b"BB" * 100
    finally:
        await server.stop()


def test_ollama_stream_text_drains_generated_pieces():
    async def scenario():
        brain = OllamaBrain("http://unused", "test-model")

        async def fake_streaming(messages, handle):
            queue = brain._chunks[handle]
            for piece in ["Die Schweiz ", "hat 26 ", "Kantone."]:
                await queue.put(piece)
            await queue.put(None)
            return "Die Schweiz hat 26 Kantone."

        brain._chat_streaming = fake_streaming
        handle = await brain.ask("Frage?")
        stream = brain.stream_text(handle)
        pieces = [piece async for piece in stream]
        assert "".join(pieces) == "Die Schweiz hat 26 Kantone."
        assert await brain.await_reply(handle, 1.0) == "Die Schweiz hat 26 Kantone."
        await brain.close()

    asyncio.run(scenario())
