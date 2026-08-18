"""End-of-utterance detection over the satellite's 16 kHz mono s16le stream.

Energy-based endpointing with an adaptive noise floor — no model, no native deps, fully
unit-testable. The device's XMOS pipeline already gives us clean, echo-cancelled audio, so a
simple RMS gate is enough to find the end of speech; swap in a model VAD later if dialect
pauses prove too long for a fixed silence window.
"""

from __future__ import annotations

import struct
from dataclasses import dataclass


def rms(frame: bytes) -> float:
    """Root mean square of a 16-bit little-endian PCM frame. Empty frame → 0."""
    n = len(frame) // 2
    if n == 0:
        return 0.0
    samples = struct.unpack(f"<{n}h", frame[: n * 2])
    return (sum(s * s for s in samples) / n) ** 0.5


@dataclass
class Endpointer:
    """Feed PCM chunks; `done` flips true one silence-window after speech ended.

    The threshold adapts: the noise floor is the running minimum of recent frame energy,
    and speech is anything a fixed factor above it. This survives both a quiet room and a
    fan next to the mic without per-site tuning.
    """

    sample_rate: int = 16000
    silence_ms: int = 800
    max_utterance_s: float = 15.0
    min_utterance_s: float = 0.4
    speech_factor: float = 3.0
    floor_init: float = 150.0
    calibrate_ms: int = 0       # treat the first N ms as ambient (TV, music) — floor, not speech
    onset_timeout_s: float = 0  # 0 = off; else end early when no speech starts in time

    def __post_init__(self) -> None:
        self._buffer = bytearray()
        self._noise_floor = self.floor_init
        self._speech_seen = False
        self._silence_bytes = 0
        self._speech_bytes = 0
        self.done = False
        self.ended_by_cap = False   # True = closed by max duration, NOT by a silence gap
        self.speech_seen = False

    @property
    def audio(self) -> bytes:
        return bytes(self._buffer)

    def _bytes_per_ms(self) -> float:
        return self.sample_rate * 2 / 1000.0

    def feed(self, chunk: bytes) -> bool:
        """Accumulate a chunk; returns True once the utterance is complete."""
        if self.done or not chunk:
            return self.done
        self._buffer.extend(chunk)

        level = rms(chunk)
        # Ambient calibration window (follow-up rounds): whatever is audible right when the
        # mic opens — a TV, music — is the FLOOR, not speech; only louder-than-that counts.
        if len(self._buffer) <= self.calibrate_ms * self._bytes_per_ms():
            self._noise_floor = max(self._noise_floor, level)
            return self.done
        # Track the floor down fast, up slowly — a shout must not raise it.
        if level < self._noise_floor:
            self._noise_floor = max(1.0, 0.5 * self._noise_floor + 0.5 * level)
        else:
            self._noise_floor = 0.995 * self._noise_floor + 0.005 * level

        if level > self._noise_floor * self.speech_factor:
            self._speech_seen = True
            self._speech_bytes += len(chunk)
            self._silence_bytes = 0
        elif self._speech_seen:
            self._silence_bytes += len(chunk)

        long_enough = self._speech_bytes >= self.min_utterance_s * self.sample_rate * 2
        silent_long = self._silence_bytes >= self.silence_ms * self._bytes_per_ms()
        too_long = len(self._buffer) >= self.max_utterance_s * self.sample_rate * 2
        no_onset = (self.onset_timeout_s > 0 and not self._speech_seen
                    and len(self._buffer) >= self.onset_timeout_s * self.sample_rate * 2)

        if (self._speech_seen and long_enough and silent_long) or too_long or no_onset:
            self.done = True
            self.ended_by_cap = too_long or no_onset
            self.speech_seen = self._speech_seen
        return self.done
