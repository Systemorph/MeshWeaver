// expo-av stand-in for headless tests — the Video leaf renders as a host node named "Video" so
// toJSON tags it and carries its props (source/useNativeControls/posterSource).
import React from "react";

export const Video = ({ children, ...props }: any) => React.createElement("Video", props, children);

export const ResizeMode = { CONTAIN: "contain", COVER: "cover", STRETCH: "stretch" } as const;
