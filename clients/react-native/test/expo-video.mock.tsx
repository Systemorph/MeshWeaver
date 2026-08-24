// expo-video stand-in for headless tests — `VideoView` renders as a host node named "Video" (the
// same tag the old expo-av mock used, so the assertions stay about the CONTROL and not the vendor)
// and carries its props (player/nativeControls/contentFit).
//
// `useVideoPlayer` returns an inert player: the real one is a native SharedObject, and the unit
// level asserts the MAPPING from a UiControl tree to props, not playback. It records the source so
// a test can prove the control handed the right URI to the player rather than to the view — which
// is where expo-video moved it (expo-av took `source` on the view itself).
import React from "react";

export type MockVideoPlayer = {
  source: unknown;
  listeners: Record<string, ((payload: any) => void)[]>;
  addListener: (name: string, fn: (payload: any) => void) => { remove: () => void };
  emit: (name: string, payload: any) => void;
};

export function useVideoPlayer(source: unknown): MockVideoPlayer {
  const player = React.useMemo<MockVideoPlayer>(() => {
    const listeners: Record<string, ((payload: any) => void)[]> = {};
    return {
      source,
      listeners,
      addListener: (name, fn) => {
        (listeners[name] ??= []).push(fn);
        return { remove: () => { listeners[name] = (listeners[name] ?? []).filter((f) => f !== fn); } };
      },
      emit: (name, payload) => { for (const fn of listeners[name] ?? []) fn(payload); },
    };
  }, [source]);
  return player;
}

export const VideoView = ({ children, ...props }: any) => React.createElement("Video", props, children);
