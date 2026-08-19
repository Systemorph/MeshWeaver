import math
import struct

from memex_voice_gateway.vad import Endpointer, rms


def tone(ms: int, amplitude: int = 8000, rate: int = 16000) -> bytes:
    n = rate * ms // 1000
    return struct.pack(f"<{n}h", *(int(amplitude * math.sin(i / 5)) for i in range(n)))


def silence(ms: int, rate: int = 16000) -> bytes:
    return b"\x00\x00" * (rate * ms // 1000)


def feed_chunks(endpointer: Endpointer, audio: bytes, chunk_ms: int = 20) -> bool:
    step = 16000 * 2 * chunk_ms // 1000
    done = False
    for offset in range(0, len(audio), step):
        done = endpointer.feed(audio[offset:offset + step])
        if done:
            break
    return done


def test_rms_of_silence_is_zero():
    assert rms(silence(20)) == 0.0
    assert rms(b"") == 0.0


def test_speech_then_silence_ends_the_utterance():
    endpointer = Endpointer(silence_ms=400)
    audio = silence(200) + tone(800) + silence(1000)
    assert feed_chunks(endpointer, audio) is True
    assert len(endpointer.audio) > 0


def test_pure_silence_only_ends_at_max_duration():
    endpointer = Endpointer(silence_ms=400, max_utterance_s=1.0)
    assert feed_chunks(endpointer, silence(900)) is False
    assert feed_chunks(endpointer, silence(300)) is True  # crosses max_utterance_s


def test_too_short_speech_does_not_end_early():
    endpointer = Endpointer(silence_ms=300, min_utterance_s=0.5)
    # 100 ms of speech is below the minimum, so trailing silence must NOT end the round yet.
    assert feed_chunks(endpointer, silence(100) + tone(100) + silence(500)) is False
