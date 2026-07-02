// SSR smoke: a UiControl tree renders to HTML MARKUP on the server (node environment, no DOM) —
// what Next streams inside the page's Suspense boundary — and Griffel's renderer collects the
// styles for useServerInsertedHTML to flush into the stream.
import { describe, expect, it, vi } from "vitest";
import { renderToString } from "react-dom/server";
// Griffel SSR utils via Fluent's re-export — the same module instance the components use
// (see Providers.tsx).
import { createDOMRenderer, RendererProvider, renderToStyleElements, webLightTheme } from "@fluentui/react-components";
import { MeshAreaView, StaticAreaSource } from "@meshweaver/react";
import { buildInitialTree, fetchRenderedArea, SSR_ROOT_AREA } from "../src/server/snapshot";
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

  it("renders a REAL control tree from a canned wire {areas,data} render-area payload", async () => {
    // A wire-faithful first Full frame from POST /api/mesh/render-area: EntityStore $type marker,
    // JSON-ENCODED instance keys, class-name control $types ("StackControl"/"MarkdownControl" —
    // the live mesh's TypeRegistry names), and the default-area areas[""] indirection.
    const wireFrame = {
      $type: "MeshWeaver.Data.EntityStore",
      areas: {
        '""': { $type: "NamedAreaControl", area: "Overview" },
        '"Overview"': {
          $type: "StackControl",
          areas: [
            { $type: "NamedAreaControl", area: "Overview/1" },
            { $type: "NamedAreaControl", area: "Overview/2" },
          ],
          skins: [{ $type: "LayoutStackSkin", orientation: "Vertical" }],
        },
        '"Overview/1"': { $type: "LabelControl", typo: "PageTitle", data: "Pricing Cornerstone" },
        '"Overview/2"': { $type: "MarkdownControl", data: "Live **mesh** content." },
      },
      data: { '"progress"': { message: "", progress: 100 } },
    };
    const realFetch = globalThis.fetch;
    globalThis.fetch = vi.fn(() =>
      Promise.resolve(new Response(JSON.stringify(wireFrame), { status: 200 })),
    ) as unknown as typeof fetch;
    try {
      const tree = await fetchRenderedArea("https://portal.example", "mw_abc", "ACME/Pricing");
      expect(tree).not.toBeNull();

      // Seed StaticAreaSource straight from the fetched frame — NO translation — and SSR it
      // through the real registry, rooted at the same "" key the live subscription uses.
      const source = new StaticAreaSource(tree!);
      const renderer = createDOMRenderer();
      const html = renderToString(
        <RendererProvider renderer={renderer}>
          <MeshAreaView source={source} rootArea={SSR_ROOT_AREA} theme={webLightTheme} />
        </RendererProvider>,
      );

      expect(html).toContain("Pricing Cornerstone"); // the Label leaf, via the "" → Overview indirection
      expect(html).toContain("<strong>mesh</strong>"); // the Markdown leaf rendered through react-markdown
    } finally {
      globalThis.fetch = realFetch;
    }
  });

  it("SSRs the LiveArea client boundary from its snapshot seed (no connection provider)", () => {
    const renderer = createDOMRenderer();
    const html = renderToString(
      <RendererProvider renderer={renderer}>
        <LiveArea
          path="Northwind/Dashboard"
          target={{ address: "Northwind/Dashboard", area: "", id: "" }}
          initialTree={buildInitialTree(snapshot)}
        />
      </RendererProvider>,
    );

    expect(html).toContain("data-mw-live-area");
    expect(html).toContain('data-mw-live="false"'); // never live on the server — no stream exists
    expect(html).toContain("Northwind Dashboard");
  });
});
