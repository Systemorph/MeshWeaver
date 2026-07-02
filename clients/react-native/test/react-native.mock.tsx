// A minimal react-native stand-in for headless tests. Each primitive renders as a host node named after
// itself (so react-test-renderer's toJSON tags them by type and carries their props) — enough to assert
// that the leaf pack maps a UiControl tree to the right native components with the right props/bindings,
// without a native runtime. This is NOT a device render; it's the deterministic, CI-runnable unit level.
import React from "react";

const host =
  (name: string) =>
  ({ children, ...props }: any) =>
    React.createElement(name, props, children);

export const View = host("View");
export const Text = host("Text");
export const TextInput = host("TextInput");
export const Switch = host("Switch");
export const Pressable = host("Pressable");
export const ScrollView = host("ScrollView");
export const ActivityIndicator = host("ActivityIndicator");
export const SafeAreaView = host("SafeAreaView");
export const StatusBar = host("StatusBar");

export const StyleSheet = { create: <T,>(styles: T): T => styles };

// The pack branches on Platform.OS (web renders real HTML for Markdown/Html; native renders <Text>).
// The headless unit level asserts the NATIVE mapping, so report a native OS.
export const Platform = { OS: "ios" };
