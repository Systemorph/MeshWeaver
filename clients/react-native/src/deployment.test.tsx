import { describe, expect, it } from "vitest";
import React from "react";
import Renderer from "react-test-renderer";
import { RegistryProvider, RenderArea, ScopeProvider, StaticAreaSource, composeDeployment } from "@meshweaver/react/core";
import { rnPack } from "./rnPack";
import acmeModule from "../deployment/examples/acme-module";

// The deployment-composition seam, end to end at the render level: a manifest-injected module's
// leaf renders through the SAME registry the base pack serves — the exact mechanism a client
// deployment uses to ship "anything of interest" (bespoke boards, charts, brand widgets) in its
// bundle. If this breaks, every custom deployment silently falls back to the Unsupported card.
describe("deployment-composed pack", () => {
  it("renders a module-contributed control $type through the composed registry", async () => {
    const pack = composeDeployment(rnPack, [acmeModule]);
    expect(pack.controls.AcmeBadge).toBeTruthy();
    let r!: Renderer.ReactTestRenderer;
    await Renderer.act(async () => {
      r = Renderer.create(
        <RegistryProvider pack={pack}>
          <ScopeProvider
            source={new StaticAreaSource({ areas: { main: { $type: "AcmeBadge", data: "Hello Acme" } as never } })}
            area="main"
          >
            <RenderArea areaKey="main" />
          </ScopeProvider>
        </RegistryProvider>,
      );
    });
    const texts = r.root.findAll((n) => String(n.type) === "Text");
    expect(texts.some((t) => String(t.props.children) === "Hello Acme")).toBe(true);
  });

  it("without the module, the same $type falls to the base pack's Unsupported fallback (no crash)", async () => {
    let r!: Renderer.ReactTestRenderer;
    await Renderer.act(async () => {
      r = Renderer.create(
        <RegistryProvider pack={rnPack}>
          <ScopeProvider
            source={new StaticAreaSource({ areas: { main: { $type: "AcmeBadge", data: "x" } as never } })}
            area="main"
          >
            <RenderArea areaKey="main" />
          </ScopeProvider>
        </RegistryProvider>,
      );
    });
    expect(r.toJSON()).toBeTruthy();
  });
});

// ---- ultra-modular: the core pack is the PLATFORM, the product is composed -------------------

describe("core pack minimality (this is what modularity means)", () => {
  const domainLeaves = [
    "ThreadChat", "ThreadMessageBubble",           // threads
    "MeshSearch", "MeshNodeCollection",            // meshBrowse
    "MeshNodeContentEditor", "Appearance",         // nodeEditing
    "PivotGrid", "Chart",                          // data
    "FileBrowser", "ExportDocument", "NodeExport", "NodeImport", "DocumentSource", // documents
    "KpiStrip", "Tower", "ComparisonBars",         // analysis
    "Video", "SlideShow",                          // media (the expo-av touchpoint)
  ];

  it("the BASE pack contains none of the domain leaves — they arrive only via modules", () => {
    for (const t of domainLeaves) expect(rnPack.controls[t], t).toBeUndefined();
  });

  it("the standard set restores every domain leaf", async () => {
    const { standardModules } = await import("./modules/standard");
    const full = composeDeployment(rnPack, standardModules);
    for (const t of domainLeaves) expect(full.controls[t], t).toBeTruthy();
  });

  // 🚨 The drift guard: default.json (what the BUILD composes) and standardModules (what the
  // TESTS compose) must be the same seven — a module added to one but not the other would test
  // a different app than ships.
  it("deployment/default.json and standardModules agree", async () => {
    const { standardModules } = await import("./modules/standard");
    const { deployment } = await import("./deployment.generated");
    expect(deployment.modules.map((m) => m.name)).toEqual(standardModules.map((m) => m.name));
  });
});
