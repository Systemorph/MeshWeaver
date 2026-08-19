import struct

from memex_voice_gateway.stt import wav_from_pcm


def test_wav_header_fields():
    pcm = b"\x01\x02" * 1600  # 100 ms at 16 kHz mono s16le
    wav = wav_from_pcm(pcm, sample_rate=16000)
    assert wav[:4] == b"RIFF" and wav[8:12] == b"WAVE"
    assert struct.unpack("<I", wav[4:8])[0] == 36 + len(pcm)
    fmt = struct.unpack("<IHHIIHH", wav[16:36])
    assert fmt == (16, 1, 1, 16000, 32000, 2, 16)
    assert wav[36:40] == b"data"
    assert struct.unpack("<I", wav[40:44])[0] == len(pcm)
    assert wav[44:] == pcm
