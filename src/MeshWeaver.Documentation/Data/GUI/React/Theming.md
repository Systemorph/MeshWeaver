---
Name: Theming the React Frontend
Category: Documentation
Description: Light/dark/system theming with the SAME localStorage contract as the Blazor portal — one persisted preference across both frontends, Fluent design tokens, useThemeMode and ThemeToggle.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="4"/><path d="M12 2v2"/><path d="M12 20v2"/><path d="m4.93 4.93 1.41 1.41"/><path d="m17.66 17.66 1.41 1.41"/><path d="M2 12h2"/><path d="M20 12h2"/><path d="m6.34 17.66-1.41 1.41"/><path d="m19.07 4.93-1.41 1.41"/></svg>
---

# Theming the React Frontend

The React frontend themes with **the same semantics and the same persistence contract** as the Blazor portal's `<FluentDesignTheme>` — so a user switching between the two frontends on one origin keeps a single preference. Everything lives in `clients/react/src/theme/`.

## The shared contract with Blazor

Four points of deliberate compatibility (`theme/themeMode.ts`):

1. **Three modes** — `"system"` (default), `"light"`, `"dark"` — Blazor's `DesignThemeModes`.
2. **Same storage key, same JSON shape.** The preference persists in localStorage under the key `"theme"` (the Blazor portal's `<FluentDesignTheme StorageName="theme">` in `SiteSettingsPanel.razor`), as the same JSON object the fluent-design-theme web component writes: `{ mode, primaryColor? }`. Writing the mode **preserves sibling fields** a Blazor portal may have stored under the same key:

```ts
export const DEFAULT_THEME_STORAGE_KEY = "theme";

// Reads tolerate the Blazor JSON shape ({ mode, primaryColor }), a bare string
// ("dark"), or nothing/garbage (→ "system", the Blazor default).
export function readStoredThemeMode(storageKey?: string): ThemeMode;

// Writes keep { ...existing, mode } — primaryColor and friends survive.
export function writeStoredThemeMode(mode: ThemeMode, storageKey?: string): void;

// Blazor's "Reset settings" equivalent — clears the key, back to system.
export function clearStoredTheme(storageKey?: string): void;
```

3. **`system` follows the OS live** — `prefers-color-scheme` is observed, so an OS-level switch restyles a running app without a reload.
4. **The resolved mode is mirrored onto the document** — `document.body.dataset.theme` (the same `body[data-theme]` attribute Blazor's theme script sets) plus `document.documentElement.style.colorScheme`, so scrollbars and native inputs restyle too.

## Design-token propagation

The Fluent design tokens (`--colorNeutralBackground1`, `--colorBrandBackground2`, …) are applied by `<FluentProvider>` from `webLightTheme` / `webDarkTheme` — the React v9 equivalent of Blazor's design-token stylesheet. Every control in the pack styles itself from tokens, so the whole tree restyles from the one switch; custom controls should do the same (`color: "var(--colorNeutralForeground3)"`, never hard-coded colors).

```ts
export function resolveThemeMode(mode: ThemeMode, prefersDark: boolean): ResolvedThemeMode;
export function fluentThemeFor(resolved: ResolvedThemeMode): Theme;  // webDarkTheme | webLightTheme
```

## `useThemeMode` — the hook

```tsx
import { useThemeMode } from "@meshweaver/react";

function Shell() {
  const { mode, resolved, theme, setMode } = useThemeMode();
  // mode:     the user's choice — "light" | "dark" | "system"
  // resolved: what's actually on screen — "light" | "dark"
  // theme:    the Fluent theme object for <FluentProvider>
  return <FluentProvider theme={theme}>…</FluentProvider>;
}
```

The hook keeps every instance in sync: instances in the same document coordinate through a custom `meshweaver:theme-change` event (a toggle in the header and an appearance panel never disagree), and other tabs follow via the browser's `storage` event.

`MeshAreaView` calls `useThemeMode` internally — when you don't pin a `theme` prop, the view follows the persisted preference automatically. `themeStorageKey` overrides the storage key for apps that must not share the Blazor preference.

## `ThemeToggle` — the switcher

`theme/ThemeToggle.tsx` is the React counterpart of the theme selector in the Blazor portal's site settings panel: a menu button offering Light / Dark / System with the current mode checked, persisting through `useThemeMode`:

```tsx
import { ThemeToggle } from "@meshweaver/react";

<header>
  …
  <ThemeToggle />   {/* optional: <ThemeToggle storageKey="my-app-theme" /> */}
</header>
```

The `clients/portal` app shell places it in the header next to the user avatar — the same spot the Blazor portal exposes its theme control.

## Related

- [React Frontend overview](/Doc/GUI/React)
- [Getting Started](../GettingStarted) — the `mw-frontend` cookie follows the same client-side-preference pattern as the theme.
