// Vitest stand-in for expo-audio (the SDK-57 recorder): rnComposer — mounted by the ThreadChat
// leaf — constructs ExpoAudioRecorder, whose module reads these at load. Never records in tests.
export const AudioModule = {};
export const AudioQuality = { HIGH: "high" };
export const IOSOutputFormat = { LINEARPCM: "lpcm" };
export const requestRecordingPermissionsAsync = async () => ({ granted: true });
export const setAudioModeAsync = async () => {};
export type AudioRecorder = unknown;
export type RecordingOptions = Record<string, unknown>;
export default {};
