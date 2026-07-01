// SSR smoke: a UiControl tree renders to HTML MARKUP on the server (node environment, no DOM) —
// what Next streams inside the page's Suspense boundary — and Griffel's renderer collects the
// styles for useServerInsertedHTML to flush into the stream.
import { describe, expect, it } from "vitest";
import { renderToString } from "react-dom/server";
// Griffel SSR utils via Fluent's re-export — the same module instance the components use
// (see Providers.tsx).
import { createDOMRenderer, RendererProvider, renderToStyleElements, webLightTheme } from "@fluentui/react-components";
import { MeshAreaView, StaticAreaSource } from "@meshweaver/react";
import { buildInitialTree, SSR_ROOT_AREA } from "../src/server/snapshot";
import { LiveArea } from "../src/client/LiveArea";

const snapshot = {
  path: "Northwind/Dashboard",
  name: "Northwind Dashboard",
  nodeType: "Space",
  markdown: "Sales overview for the current quarter.",
};

describe("server-side rendering", () => {
  it("renders a control tree to markup via renderToString", () => {
    const source = new StaticAreaSource(buildInitialTree(snapshot));
    const renderer = createDOMRenderer();

    const html = renderToString(
      <RendererProvider renderer={renderer}>
        <MeshAreaView source={source} rootArea={SSR_ROOT_AREA} theme={webLightTheme} />
      </RendererProvider>,
    );

    expect(html).toContain("Northwind Dashboard");
    expect(html).toContain("Space · Northwind/Dashboard");
    expect(html).toContain("Sales overview for the current quarter.");

    // Griffel collected the makeStyles CSS server-side — the style elements
    // useServerInsertedHTML flushes into the HTML stream.
    const styles = renderToStyleElements(renderer);
    expect(styles.length).toBeGreaterThan(0);
  });

  it("SSRs the LiveArea client boundary from its snapshot seed (no connection provider)", () => {
    const renderer = createDOMRenderer();
    const html = renderToString(
      <RendererProvider renderer={renderer}>
        <LiveArea path="Northwind/Dashboard" initialTree={buildInitialTree(snapshot)} />
      </RendererProvider>,
    );

    expect(html).toContain("data-mw-live-area");
    expect(html).toContain('data-mw-live="false"'); // never live on the server — no stream exists
    expect(html).toContain("Northwind Dashboard");
  });
});
