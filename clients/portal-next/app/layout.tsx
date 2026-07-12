// Root layout (server component): document shell + the client provider stack. The PortalShell
// chrome renders in the FIRST HTML flush; routed pages stream into <main> below it (each page
// wraps its area in a Suspense boundary — see app/[[...meshPath]]/page.tsx).

import type { Metadata } from "next";
import type { ReactNode } from "react";
// RSC-safe styles entry (pure strings — no React context in the server graph).
import { MESH_STYLES_ID, meshStylesText } from "@meshweaver/react/styles";
import { Providers } from "../src/client/Providers";
import { PortalShell } from "../src/client/PortalShell";

export const metadata: Metadata = {
  title: "MeshWeaver",
  description:
    "MeshWeaver portal — Next.js streaming-SSR shell over @meshweaver/react, with per-request server snapshots and client-side live takeover over gRPC-web.",
};

export default function RootLayout({ children }: { children: ReactNode }) {
  return (
    <html lang="en">
      <head>
        {/* The renderer stylesheet (FAST token aliases, markdown typography, nav/grid classes) —
            inlined server-side so the STREAMED first paint is styled; MeshAreaView's client-side
            ensureMeshStyles() sees the id and skips re-injecting. */}
        <style id={MESH_STYLES_ID} dangerouslySetInnerHTML={{ __html: meshStylesText() }} />
      </head>
      <body style={{ margin: 0, fontFamily: "'Segoe UI', system-ui, sans-serif" }}>
        <Providers>
          <PortalShell>{children}</PortalShell>
        </Providers>
      </body>
    </html>
  );
}
