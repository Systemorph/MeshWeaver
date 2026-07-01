"use client";

// SSR style extraction + session-scoped providers.
//
// Fluent UI v9 styles with Griffel; under streaming SSR the CSS must be flushed INTO the HTML
// stream or the server-rendered markup arrives unstyled. This is the documented App-Router
// pattern: a request-scoped DOM renderer + useServerInsertedHTML, which Next calls on every
// stream flush — renderToStyleElements emits the styles accumulated so far, so Suspense content
// flushed late still carries its CSS (duplicate <style> elements across flushes are idempotent).
// <RendererProvider> makes every makeStyles/FluentProvider below use this renderer; <SSRProvider>
// keeps Fluent's generated ids stable across server and client.

import { useState, type ReactNode } from "react";
import { useServerInsertedHTML } from "next/navigation";
// Griffel SSR utils via Fluent's OWN re-export — guarantees the renderer context is the same
// module instance the Fluent components read (a direct @griffel/react import can resolve to a
// second copy under some loaders — the dual-package hazard).
import { createDOMRenderer, renderToStyleElements, RendererProvider, SSRProvider } from "@fluentui/react-components";
import { LiveConnectionProvider } from "./LiveConnection";

export function Providers({ children }: { children: ReactNode }) {
  const [renderer] = useState(() => createDOMRenderer());

  useServerInsertedHTML(() => <>{renderToStyleElements(renderer)}</>);

  return (
    <RendererProvider renderer={renderer}>
      <SSRProvider>
        <LiveConnectionProvider>{children}</LiveConnectionProvider>
      </SSRProvider>
    </RendererProvider>
  );
}
