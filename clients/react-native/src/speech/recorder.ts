// The platform seam for microphone capture. The speech→submit flow (pushToTalk.ts) and its tests
// depend ONLY on this interface; the Expo implementation lives in expoRecorder.ts. Capture is the
// only on-device step — recognition is the centralized Whisper container (see transcription.ts).

import type { AudioInput } from "./transcription";

export interface Recorder {
  /** Begin capturing microphone audio (requests permission on first use). */
  start(): Promise<void>;
  /** Stop capturing and resolve the recorded audio, ready to post to the transcription endpoint. */
  stop(): Promise<AudioInput>;
  /** Abandon the recording without producing audio (e.g. after an upstream error). */
  cancel(): Promise<void>;
}
