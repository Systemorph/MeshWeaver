# Next.js — the easiest target

**Short answer: trivial — much easier than React Native.** Next.js is React-on-the-web, so it uses the
**same `@meshweaver/react` package and the same Fluent DOM leaf pack** as the Vite demo. There is **no
new leaf pack** to write (that's the whole job for React Native). The only Next.js-specific work is two
small things every Fluent-UI-on-Next.js app does anyway:

1. **Mark the view a client component** (`"use client"`) — the renderer uses hooks + browser APIs
   (`useSyncExternalStore`, the area subscription), so it runs on the client.
2. **Fluent v9 SSR wiring** (~10 lines in `app/layout.tsx`) so styles render server-side without a flash.

That's it. Ballpark: **an afternoon**, vs React Native which is a whole native component pack.

## The page (App Router)

```tsx
// app/page.tsx
"use client";
import { MeshAreaView, StaticAreaSource } from "@meshweaver/react";
import { sampleArea } from "./sample"; // or a GrpcAreaSource for live mesh data

const source = new StaticAreaSource(sampleArea);

export default function Page() {
  return <MeshAreaView source={source} rootArea="main" />;
}
```

`MeshAreaView` already installs `FluentProvider` + the Fluent pack, so the page is one component.

## SSR styles (avoid the flash) — `app/layout.tsx`

Use Fluent's documented App-Router SSR setup so Griffel emits styles on the server:

```tsx
// app/layout.tsx  (Fluent UI v9 SSR — https://react.fluentui.dev → "Server-side rendering")
import { RendererProvider, SSRProvider, createDOMRenderer, renderToStyleElements } from "@fluentui/react-components";
import { useServerInsertedHTML } from "next/navigation";

export default function RootLayout({ children }: { children: React.ReactNode }) {
  const renderer = createDOMRenderer();
  useServerInsertedHTML(() => <>{renderToStyleElements(renderer)}</>);
  return (
    <html lang="en">
      <body>
        <RendererProvider renderer={renderer}>
          <SSRProvider>{children}</SSRProvider>
        </RendererProvider>
      </body>
    </html>
  );
}
```

## Live mesh data

Same as web — swap `StaticAreaSource` for `GrpcAreaSource` wired to `@meshweaver/client`. Run that in a
`useEffect` (client) or behind a route handler. The renderer, binding, and events are identical to the
Vite/Electron path because they all share the Fluent-free core.

## Difficulty, ranked

| Target | Leaf pack | Extra work |
|---|---|---|
| **Vite / web** | Fluent (shipped) | none |
| **Next.js** | Fluent (shipped) | `"use client"` + Fluent SSR (~10 lines) |
| **Electron** | Fluent (shipped) | a `BrowserWindow` (shipped — `electron/main.cjs`) |
| **React Native / Expo** | **write an RN pack** | Expo project + native leaf pack ([react-native.md](react-native.md)) |
