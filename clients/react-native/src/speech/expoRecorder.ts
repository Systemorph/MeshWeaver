// Expo (expo-av) microphone capture — the RN implementation of the Recorder seam. Capture only:
// recognition happens on the centralized Whisper container (transcription.ts), never on-device.
//
// Format: 16 kHz mono — what Whisper wants. iOS records WAV/LINEARPCM (the safe interchange format
// the whisper.cpp server always accepts); Android's MediaRecorder cannot produce WAV, so it records
// AAC/.m4a — the portal transcribe endpoint (or a Whisper container built with ffmpeg + `--convert`)
// transcodes it. See deploy/whisper/Dockerfile and transcription.ts's TODO(portal endpoint).

import { Audio } from "expo-av";
import { Platform } from "react-native";
import type { Recorder } from "./recorder";
import type { AudioInput } from "./transcription";

const RECORDING_OPTIONS: Audio.RecordingOptions = {
  isMeteringEnabled: false,
  ios: {
    extension: ".wav",
    outputFormat: Audio.IOSOutputFormat.LINEARPCM,
    audioQuality: Audio.IOSAudioQuality.HIGH,
    sampleRate: 16_000,
    numberOfChannels: 1,
    bitRate: 256_000,
    linearPCMBitDepth: 16,
    linearPCMIsBigEndian: false,
    linearPCMIsFloat: false,
  },
  android: {
    extension: ".m4a",
    outputFormat: Audio.AndroidOutputFormat.MPEG_4,
    audioEncoder: Audio.AndroidAudioEncoder.AAC,
    sampleRate: 16_000,
    numberOfChannels: 1,
    bitRate: 64_000,
  },
  web: {
    mimeType: "audio/webm",
    bitsPerSecond: 128_000,
  },
};

export class ExpoAvRecorder implements Recorder {
  private recording: Audio.Recording | null = null;

  async start(): Promise<void> {
    if (this.recording) throw new Error("Already recording.");
    const permission = await Audio.requestPermissionsAsync();
    if (!permission.granted) throw new Error("Microphone permission denied.");
    await Audio.setAudioModeAsync({ allowsRecordingIOS: true, playsInSilentModeIOS: true });
    const { recording } = await Audio.Recording.createAsync(RECORDING_OPTIONS);
    this.recording = recording;
  }

  async stop(): Promise<AudioInput> {
    const recording = this.recording;
    if (!recording) throw new Error("Not recording.");
    this.recording = null;
    const status = await recording.stopAndUnloadAsync();
    const uri = recording.getURI();
    if (!uri) throw new Error("Recording produced no file.");
    const wav = Platform.OS === "ios";
    return {
      uri,
      contentType: wav ? "audio/wav" : "audio/mp4",
      fileName: wav ? "audio.wav" : "audio.m4a",
      durationMs: status.durationMillis,
    };
  }

  async cancel(): Promise<void> {
    const recording = this.recording;
    if (!recording) return;
    this.recording = null;
    try {
      await recording.stopAndUnloadAsync();
    } catch {
      // Already unloaded — cancelling must not mask the error that triggered it.
    }
  }
}
