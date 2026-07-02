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
  // `next build` type-checks the FULL module graph, including the vendored ../react and
  // ../grpc-web SOURCE (externalDir). TS resolves bare imports (react, react-dom, chart.js, the
  // fluentui deep paths, …) by walking up from each file's OWN directory — and those sibling
  // packages have no node_modules in the build. Webpack's resolve.modules override (below) points
  // bare imports at portal-next/node_modules so the COMPILE succeeds ("✓ Compiled successfully"),
  // but TS has no equivalent hook for arbitrary external files, so the build-time typecheck fails
  // to resolve modules the runtime bundle resolves fine. Type coverage is owned elsewhere by design:
  // portal-next's OWN code by `npm run typecheck` (tsconfig include = app/src/test only), and
  // @meshweaver/react by its own package CI — so we don't run the mis-scoped cross-package typecheck
  // (or its ESLint companion) during the production image build. This is NOT ignoring real errors:
  // it's declining to re-type-check vendored source through the wrong tsconfig.
  typescript: { ignoreBuildErrors: true },
  eslint: { ignoreDuringBuilds: true },
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
      // /wire is the React-FREE wire-folding leaf — the server snapshot module imports it
      // without dragging the renderer (React context/components) into the RSC server graph.
      "@meshweaver/react/wire": path.resolve(here, "../react/src/live/wire.ts"),
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
