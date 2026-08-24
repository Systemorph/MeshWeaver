// expo-av stand-in for headless tests — the Video leaf renders as a host node named "Video" so
// toJSON tags it and carries its props (source/useNativeControls/posterSource).
import React from "react";

export const Video = ({ children, ...props }: any) => React.createElement("Video", props, children);

export const ResizeMode = { CONTAIN: "contain", COVER: "cover", STRETCH: "stretch" } as const;

// The Audio surface expoRecorder reads at MODULE LOAD (RECORDING_OPTIONS): rnComposer — the shared
// composer bar the ThreadChat leaf mounts — imports it into the pack graph, so headless tests need
// the enums present (never a real recording).
export const Audio = {
  IOSOutputFormat: { LINEARPCM: "lpcm" },
  IOSAudioQuality: { HIGH: "high" },
  AndroidOutputFormat: { MPEG_4: 2 },
  AndroidAudioEncoder: { AAC: 3 },
  requestPermissionsAsync: async () => ({ granted: true }),
  setAudioModeAsync: async () => {},
  Recording: { createAsync: async () => ({ recording: { stopAndUnloadAsync: async () => {}, getURI: () => null } }) },
} as any;
