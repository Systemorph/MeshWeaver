import path from "node:path";
import { fileURLToPath } from "node:url";

const here = path.dirname(fileURLToPath(import.meta.url));

/**
 * Next.js + streaming-SSR variant of the React portal.
 *
 * - `output: "standalone"` — self-contained node server for the Docker image.
 * - `basePath: "/next"` — served parallel to the Vite SPA's /app (ingress routes /next here).
 * - Monorepo source aliasing, mirroring clients/portal/vite.config.ts: the renderer
 *   (@meshweaver/react) and the gRPC-web transport (@meshweaver/client-web) resolve to their
 *   SOURCE trees — no build/link step. `experimental.externalDir` lets SWC compile files
 *   outside this app directory.
 */
/** @type {import('next').NextConfig} */
const nextConfig = {
  output: "standalone",
  basePath: "/next",
  reactStrictMode: true,
  experimental: {
    // Compile the ../react and ../grpc-web sources (outside this app dir).
    externalDir: true,
    // Monorepo root for standalone output tracing (clients/) — server.js lands under
    // .next/standalone/portal-next/server.js (see Dockerfile).
    outputFileTracingRoot: path.join(here, ".."),
  },
  webpack: (config) => {
    config.resolve.alias = {
      ...config.resolve.alias,
      // Order matters: the more specific subpath before the package root.
      "@meshweaver/react/core": path.resolve(here, "../react/src/core.ts"),
      "@meshweaver/react": path.resolve(here, "../react/src/index.tsx"),
      "@meshweaver/client-web": path.resolve(here, "../grpc-web/src/index.ts"),
    };
    // The renderer source uses ESM ".js" specifiers for its own ".ts/.tsx" modules.
    config.resolve.extensionAlias = {
      ...config.resolve.extensionAlias,
      ".js": [".tsx", ".ts", ".js"],
    };
    // Vite's `dedupe` equivalent: resolve bare imports (including those made FROM the aliased
    // ../react and ../grpc-web sources) against THIS app's node_modules first, so exactly one
    // copy of react / fluent is bundled — and the Docker build needs no node_modules in
    // ../react / ../grpc-web at all.
    config.resolve.modules = [
      path.resolve(here, "node_modules"),
      ...(config.resolve.modules ?? ["node_modules"]),
    ];
    return config;
  },
};

export default nextConfig;
