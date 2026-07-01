// Light/dark mode parity with the Blazor portal's <FluentDesignTheme StorageName="theme">:
// same three modes (system default / light / dark), same localStorage key + JSON shape, system mode
// follows prefers-color-scheme live, and the resolved mode lands on <body data-theme> exactly like
// Blazor's UpdateBodyDataSetTheme. The MeshAreaView integration asserts the actual Fluent design
// tokens (webLightTheme/webDarkTheme CSS custom properties) applied to the DOM flip with the mode —
// the mechanism by which every control in the pack restyles.

import { beforeEach, describe, expect, it } from "vitest";
import { act, fireEvent, render, renderHook, screen } from "@testing-library/react";
import { FluentProvider, webDarkTheme, webLightTheme } from "@fluentui/react-components";
import { MeshAreaView, StaticAreaSource } from "../index.js";
import { ThemeToggle } from "./ThemeToggle.js";
import { readStoredThemeMode, useThemeMode, writeStoredThemeMode } from "./themeMode.js";

// ── Controllable prefers-color-scheme mock (jsdom has no matchMedia) ─────────────────────────────
class MockMediaQueryList {
  matches = false;
  media = "(prefers-color-scheme: dark)";
  private readonly listeners = new Set<(e: { matches: boolean }) => void>();
  addEventListener = (_: string, cb: (e: { matches: boolean }) => void) => this.listeners.add(cb);
  removeEventListener = (_: string, cb: (e: { matches: boolean }) => void) => this.listeners.delete(cb);
  setPrefersDark(dark: boolean) {
    this.matches = dark;
    this.listeners.forEach((l) => l({ matches: dark }));
  }
}

let mql: MockMediaQueryList;

// jsdom under this vitest setup exposes NO localStorage (Node's experimental global shadows it as
// undefined) — shim an in-memory Storage, same spirit as the matchMedia/ResizeObserver shims.
class MemoryStorage {
  private m = new Map<string, string>();
  getItem = (k: string) => this.m.get(k) ?? null;
  setItem = (k: string, v: string) => void this.m.set(k, String(v));
  removeItem = (k: string) => void this.m.delete(k);
  clear = () => this.m.clear();
  key = (i: number) => Array.from(this.m.keys())[i] ?? null;
  get length() {
    return this.m.size;
  }
}

beforeEach(() => {
  mql = new MockMediaQueryList();
  window.matchMedia = (() => mql) as unknown as typeof window.matchMedia;
  if (!(globalThis as any).ResizeObserver)
    (globalThis as any).ResizeObserver = class {
      observe() {}
      unobserve() {}
      disconnect() {}
    };
  Object.defineProperty(globalThis, "localStorage", { value: new MemoryStorage(), configurable: true });
  delete document.body.dataset.theme;
});

/** Read a design-token custom property from the style rules FluentProvider injected. */
function appliedToken(name: string): string | undefined {
  for (const tag of Array.from(document.querySelectorAll("style"))) {
    for (const rule of Array.from(tag.sheet?.cssRules ?? [])) {
      const v = (rule as CSSStyleRule).style?.getPropertyValue(name);
      if (v) return v.trim();
    }
  }
  return undefined;
}

describe("useThemeMode — Blazor FluentDesignTheme semantics", () => {
  it("defaults to system and resolves against the OS preference", () => {
    const light = renderHook(() => useThemeMode());
    expect(light.result.current.mode).toBe("system");
    expect(light.result.current.resolved).toBe("light");
    expect(light.result.current.theme).toBe(webLightTheme);
    light.unmount();

    mql.setPrefersDark(true);
    const dark = renderHook(() => useThemeMode());
    expect(dark.result.current.mode).toBe("system");
    expect(dark.result.current.resolved).toBe("dark");
    expect(dark.result.current.theme).toBe(webDarkTheme);
  });

  it("system mode follows prefers-color-scheme changes live", () => {
    const { result } = renderHook(() => useThemeMode());
    expect(result.current.resolved).toBe("light");
    act(() => mql.setPrefersDark(true));
    expect(result.current.resolved).toBe("dark");
    expect(document.body.dataset.theme).toBe("dark");
    act(() => mql.setPrefersDark(false));
    expect(result.current.resolved).toBe("light");
    expect(document.body.dataset.theme).toBe("light");
  });

  it("an explicit mode wins over the OS preference", () => {
    mql.setPrefersDark(true);
    const { result } = renderHook(() => useThemeMode());
    act(() => result.current.setMode("light"));
    expect(result.current.resolved).toBe("light");
    expect(result.current.theme).toBe(webLightTheme);
  });

  it("reads the Blazor FluentDesignTheme localStorage shape under the same key", () => {
    localStorage.setItem("theme", JSON.stringify({ mode: "dark", primaryColor: "default" }));
    const { result } = renderHook(() => useThemeMode());
    expect(result.current.mode).toBe("dark");
    expect(result.current.resolved).toBe("dark");
  });

  it("tolerates a bare-string or garbage stored value (falls back to system)", () => {
    localStorage.setItem("theme", "dark");
    expect(readStoredThemeMode()).toBe("dark");
    localStorage.setItem("theme", "{not json");
    expect(readStoredThemeMode()).toBe("system");
  });

  it("setMode persists Blazor-compatible JSON, preserving sibling fields", () => {
    localStorage.setItem("theme", JSON.stringify({ mode: "light", primaryColor: "#123456" }));
    const { result } = renderHook(() => useThemeMode());
    act(() => result.current.setMode("dark"));
    expect(JSON.parse(localStorage.getItem("theme")!)).toEqual({ mode: "dark", primaryColor: "#123456" });
    expect(document.body.dataset.theme).toBe("dark");
  });

  it("keeps every hook instance in the document in sync", () => {
    const a = renderHook(() => useThemeMode());
    const b = renderHook(() => useThemeMode());
    act(() => a.result.current.setMode("dark"));
    expect(b.result.current.mode).toBe("dark");
    expect(b.result.current.resolved).toBe("dark");
  });

  it("writeStoredThemeMode + a storage event sync across tabs", () => {
    const { result } = renderHook(() => useThemeMode());
    writeStoredThemeMode("dark");
    act(() => {
      window.dispatchEvent(new StorageEvent("storage", { key: "theme", newValue: localStorage.getItem("theme") }));
    });
    expect(result.current.mode).toBe("dark");
  });
});

describe("MeshAreaView applies the resolved design tokens", () => {
  const tree = { data: {}, areas: { main: { $type: "Label", data: "themed" } } };

  it("renders dark tokens when the stored mode is dark, light when light", () => {
    localStorage.setItem("theme", JSON.stringify({ mode: "dark" }));
    const dark = render(<MeshAreaView source={new StaticAreaSource(tree)} rootArea="main" />);
    expect(appliedToken("--colorNeutralBackground1")).toBe(webDarkTheme.colorNeutralBackground1);
    dark.unmount();

    localStorage.setItem("theme", JSON.stringify({ mode: "light" }));
    render(<MeshAreaView source={new StaticAreaSource(tree)} rootArea="main" />);
    expect(appliedToken("--colorNeutralBackground1")).toBe(webLightTheme.colorNeutralBackground1);
  });

  it("an explicit theme prop still wins", () => {
    localStorage.setItem("theme", JSON.stringify({ mode: "dark" }));
    render(<MeshAreaView source={new StaticAreaSource(tree)} rootArea="main" theme={webLightTheme} />);
    expect(appliedToken("--colorNeutralBackground1")).toBe(webLightTheme.colorNeutralBackground1);
  });
});

describe("ThemeToggle", () => {
  // An app shell following the hook (as the portal example does) hosting the toggle.
  function Shell() {
    const { theme } = useThemeMode();
    return (
      <FluentProvider theme={theme}>
        <ThemeToggle />
      </FluentProvider>
    );
  }

  it("switches mode from the menu and persists it — controls restyle", () => {
    const tree = { data: {}, areas: { main: { $type: "Label", data: "hello" } } };
    render(
      <>
        <Shell />
        <MeshAreaView source={new StaticAreaSource(tree)} rootArea="main" />
      </>,
    );
    expect(appliedToken("--colorNeutralBackground1")).toBe(webLightTheme.colorNeutralBackground1);

    fireEvent.click(screen.getByRole("button", { name: "Change theme" }));
    fireEvent.click(screen.getByRole("menuitemradio", { name: /dark/i }));

    expect(JSON.parse(localStorage.getItem("theme")!).mode).toBe("dark");
    expect(document.body.dataset.theme).toBe("dark");
    // The MeshAreaView (its own useThemeMode subscriber) swapped its tokens to dark.
    expect(appliedToken("--colorNeutralBackground1")).toBe(webDarkTheme.colorNeutralBackground1);
  });
});

describe("Appearance control — the theme settings panel (Blazor AppearanceView parity)", () => {
  it("renders a theme picker bound to the persisted mode, with reset", () => {
    const tree = { data: {}, areas: { main: { $type: "Appearance" } } };
    render(<MeshAreaView source={new StaticAreaSource(tree)} rootArea="main" />);

    fireEvent.click(screen.getByRole("combobox"));
    fireEvent.click(screen.getByRole("option", { name: "Dark" }));
    expect(JSON.parse(localStorage.getItem("theme")!).mode).toBe("dark");
    expect(appliedToken("--colorNeutralBackground1")).toBe(webDarkTheme.colorNeutralBackground1);

    fireEvent.click(screen.getByRole("button", { name: "Reset settings" }));
    expect(localStorage.getItem("theme")).toBeNull();
    expect(screen.getByText("Settings reset!")).toBeTruthy();
    // Back to system (OS = light here).
    expect(appliedToken("--colorNeutralBackground1")).toBe(webLightTheme.colorNeutralBackground1);
  });
});
