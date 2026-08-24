// Expo (expo-audio) microphone capture — the RN implementation of the Recorder seam. Capture only:
// recognition happens on the centralized Whisper container (transcription.ts), never on-device.
//
// Format: 16 kHz mono — what Whisper wants. iOS records WAV/LINEARPCM (the safe interchange format
// the whisper.cpp server always accepts); Android's MediaRecorder cannot produce WAV, so it records
// AAC/.m4a — the portal transcribe endpoint (or a Whisper container built with ffmpeg + `--convert`)
// transcodes it. See deploy/whisper/Dockerfile and transcription.ts's TODO(portal endpoint).
//
// 🚨 Ported from `expo-av` (removed in Expo SDK 57) — see #1584. The FORMAT contract above is what
// /api/speech/transcribe consumes, so it is preserved byte-for-byte; only the API moved:
//
//   expo-av                                        expo-audio
//   ────────────────────────────────────────────── ──────────────────────────────────────────────
//   Audio.requestPermissionsAsync()                requestRecordingPermissionsAsync()
//   Audio.setAudioModeAsync({allowsRecordingIOS,   setAudioModeAsync({allowsRecording,
//                            playsInSilentModeIOS})                    playsInSilentMode})
//   Audio.Recording.createAsync(o) -> {recording}  new AudioRecorder(o); prepareToRecordAsync(); record()
//   recording.stopAndUnloadAsync() -> {durationMillis}  getStatus().durationMillis, then stop()
//   recording.getURI()                             recorder.uri
//   Audio.IOSAudioQuality.HIGH  (0x60)             AudioQuality.HIGH        (96 — same value)
//   Audio.AndroidOutputFormat.MPEG_4               'mpeg4'                  (string union now)
//   Audio.AndroidAudioEncoder.AAC                  'aac'
//
// The one shape change that is not a rename: expo-av declared extension/sampleRate/channels/bitRate
// PER PLATFORM, expo-audio declares them at the TOP LEVEL and keeps only the codec-specific bits in
// `ios`/`android`/`web`. `bitRate` in particular has no per-platform slot any more, so the two
// values expo-av carried (256 kbps iOS / 64 kbps Android) are selected here by Platform.
//
// One correction taken while porting: the container is now THREE cases, not two. Both this file and
// its expo-av predecessor branched on `Platform.OS === "ios"` alone, so Expo WEB — which records
// `audio/webm` via MediaRecorder, and always did — was labelled `.m4a` / `audio/mp4` on the way to
// the transcribe endpoint. `CONTAINER` below keeps the label and the bytes together.
// (Web capture is still not end-to-end: the recorder yields a `blob:` URL, and transcription.ts
// posts a `uri` through React Native's `{uri,name,type}` FormData extension, which is native-only.
// That gap predates this port and is not addressed here — but the label is no longer also wrong.)

import {
  AudioModule,
  AudioQuality,
  IOSOutputFormat,
  requestRecordingPermissionsAsync,
  setAudioModeAsync,
  type AudioRecorder,
  type RecordingOptions,
} from "expo-audio";
import { Platform } from "react-native";
import type { Recorder } from "./recorder";
import type { AudioInput } from "./transcription";

const ios = Platform.OS === "ios";
const web = Platform.OS === "web";

/** What each platform actually produces — the label must match the bytes, see `stop()`. */
const CONTAINER = ios
  ? { extension: ".wav", contentType: "audio/wav", fileName: "audio.wav" }
  : web
    ? { extension: ".webm", contentType: "audio/webm", fileName: "audio.webm" }
    : { extension: ".m4a", contentType: "audio/mp4", fileName: "audio.m4a" };

const RECORDING_OPTIONS: RecordingOptions = {
  isMeteringEnabled: false,
  // Top-level now (see the note above). `ios`/`android` below re-state the extension because the
  // platform blocks are spread OVER these on the way to the native module.
  extension: CONTAINER.extension,
  sampleRate: 16_000,
  numberOfChannels: 1,
  // LINEAR PCM ignores bitRate (it is sampleRate x channels x depth); the value is carried over
  // from the expo-av options unchanged so nothing about the produced files moves.
  bitRate: ios ? 256_000 : 64_000,
  ios: {
    extension: ".wav",
    outputFormat: IOSOutputFormat.LINEARPCM,
    audioQuality: AudioQuality.HIGH,
    sampleRate: 16_000,
    linearPCMBitDepth: 16,
    linearPCMIsBigEndian: false,
    linearPCMIsFloat: false,
  },
  android: {
    extension: ".m4a",
    outputFormat: "mpeg4",
    audioEncoder: "aac",
    sampleRate: 16_000,
  },
  web: {
    mimeType: "audio/webm",
    bitsPerSecond: 128_000,
  },
};

/**
 * The per-platform, FLATTENED options the native/web recorder constructor actually takes.
 *
 * expo-audio does this internally in `useAudioRecorder` (`utils/options.createRecordingOptions`),
 * but that helper is not part of the package's public surface and this seam is a plain class, not
 * a hook — `PushToTalkController` is not a React component. Flattening here is 10 lines and keeps
 * the seam imperative; the alternative is reshaping the whole speech pipeline around a hook.
 */
function platformOptions(o: RecordingOptions): Partial<RecordingOptions> {
  const common = {
    extension: o.extension,
    sampleRate: o.sampleRate,
    numberOfChannels: o.numberOfChannels,
    bitRate: o.bitRate,
    isMeteringEnabled: o.isMeteringEnabled ?? false,
  };
  if (Platform.OS === "ios") return { ...common, ...o.ios } as Partial<RecordingOptions>;
  if (Platform.OS === "android") return { ...common, ...o.android } as Partial<RecordingOptions>;
  return { ...common, ...o.web } as Partial<RecordingOptions>;
}

type RecorderCtor = new (options: Partial<RecordingOptions>) => AudioRecorder;

/**
 * expo-audio exposes NO imperative recorder constructor under one name on every platform: the
 * native build's `AudioModule` is the `NativeAudioModule` instance carrying `AudioRecorder`, while
 * on web `index.js` resolves to `ExpoAudio.web.js`, whose `AudioModule` is the module NAMESPACE of
 * `AudioModule.web` and carries `AudioRecorderWeb`. Both classes implement the same `AudioRecorder`
 * interface — only the TYPES describe the native shape alone, hence the cast.
 */
function recorderCtor(): RecorderCtor {
  const mod = AudioModule as unknown as {
    AudioRecorder?: RecorderCtor;
    AudioRecorderWeb?: RecorderCtor;
  };
  const ctor = mod.AudioRecorder ?? mod.AudioRecorderWeb;
  if (!ctor) throw new Error("expo-audio exposes no AudioRecorder on this platform.");
  return ctor;
}

export class ExpoAudioRecorder implements Recorder {
  private recorder: AudioRecorder | null = null;

  async start(): Promise<void> {
    if (this.recorder) throw new Error("Already recording.");
    const permission = await requestRecordingPermissionsAsync();
    if (!permission.granted) throw new Error("Microphone permission denied.");
    // Renamed, not re-scoped: expo-av's allowsRecordingIOS/playsInSilentModeIOS are expo-audio's
    // allowsRecording/playsInSilentMode (both still iOS-only in effect).
    await setAudioModeAsync({ allowsRecording: true, playsInSilentMode: true });
    const Ctor = recorderCtor();
    const recorder = new Ctor(platformOptions(RECORDING_OPTIONS));
    // No argument on purpose. The native prototype re-flattens whatever it is given, and the web
    // class ignores arguments entirely — in both, the CONSTRUCTOR options are the authoritative
    // ones (iOS builds its AVAudioRecorder from them), so re-passing them here can only diverge.
    await recorder.prepareToRecordAsync();
    recorder.record();
    this.recorder = recorder;
  }

  async stop(): Promise<AudioInput> {
    const recorder = this.recorder;
    if (!recorder) throw new Error("Not recording.");
    this.recorder = null;
    // Read the duration BEFORE stopping: expo-av returned it FROM stopAndUnloadAsync(), but
    // expo-audio's stop() resolves void and the web recorder drops its MediaRecorder inside stop(),
    // after which getStatus() can no longer measure the elapsed time.
    const durationMs = recorder.getStatus().durationMillis;
    await recorder.stop();
    const uri = recorder.uri;
    if (!uri) throw new Error("Recording produced no file.");
    return {
      uri,
      contentType: CONTAINER.contentType,
      fileName: CONTAINER.fileName,
      durationMs,
    };
  }

  async cancel(): Promise<void> {
    const recorder = this.recorder;
    if (!recorder) return;
    this.recorder = null;
    try {
      await recorder.stop();
    } catch {
      // Already stopped — cancelling must not mask the error that triggered it.
    }
  }
}
