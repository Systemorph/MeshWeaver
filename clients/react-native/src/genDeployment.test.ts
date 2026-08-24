import { execFileSync } from "node:child_process";
import { mkdtempSync, mkdirSync, readFileSync, rmSync, writeFileSync, existsSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { afterEach, describe, expect, it } from "vitest";

// The PLUGIN half of deployment composition (gen-deployment.mjs): a manifest entry
// "plugin:<RepoName>/<path>" is resolved against MEMEX_PLUGIN_REPOS and the module's directory is
// VENDORED into src/deployment.vendor/ — Metro never resolves across repos, so the copy IS the
// mechanism. Exercised here against a synthetic plugin checkout so the test is self-contained
// (the real consumer is MeshWeaver.Plugins/Chess/gui/rn/chess — see deployment/examples/chess.json).

// import.meta.url, not __dirname: vitest runs this file as ESM (module: "ESNext").
const appRoot = fileURLToPath(new URL("..", import.meta.url));
const script = join(appRoot, "scripts", "gen-deployment.mjs");
const generated = join(appRoot, "src", "deployment.generated.ts");
const vendorRoot = join(appRoot, "src", "deployment.vendor");

let tmp: string | undefined;

afterEach(() => {
  if (tmp) rmSync(tmp, { recursive: true, force: true });
  tmp = undefined;
  rmSync(vendorRoot, { recursive: true, force: true });
  // Restore the checked-in default output for whoever builds next.
  execFileSync(process.execPath, [script], { env: { ...process.env, MEMEX_DEPLOYMENT: undefined, MEMEX_PLUGIN_REPOS: undefined } });
});

function makePluginRepo(): { repo: string; manifest: string } {
  tmp = mkdtempSync(join(tmpdir(), "mw-plugmod-"));
  const repo = join(tmp, "FixturePlugins");
  mkdirSync(join(repo, "Widget", "gui", "rn"), { recursive: true });
  writeFileSync(
    join(repo, "Widget", "gui", "rn", "widget.tsx"),
    `import type { DeploymentModule } from "@meshweaver/react/core";\n` +
      `const widget: DeploymentModule = { name: "widget", pack: { controls: {} } };\n` +
      `export default widget;\n`,
  );
  // A sibling file proves the whole DIRECTORY vendors (intra-module imports keep working).
  writeFileSync(join(repo, "Widget", "gui", "rn", "helper.ts"), `export const answer = 42;\n`);
  const manifest = join(tmp, "deployment.json");
  writeFileSync(
    manifest,
    JSON.stringify({ name: "Fixture", modules: ["plugin:FixturePlugins/Widget/gui/rn/widget"] }),
  );
  return { repo, manifest };
}

describe("gen-deployment plugin modules", () => {
  it("vendors a plugin module directory and imports it from the vendor tree", () => {
    const { repo, manifest } = makePluginRepo();
    execFileSync(process.execPath, [script], {
      env: { ...process.env, MEMEX_DEPLOYMENT: manifest, MEMEX_PLUGIN_REPOS: repo },
    });
    const out = readFileSync(generated, "utf8");
    expect(out).toContain(`"./deployment.vendor/FixturePlugins/Widget/gui/rn/widget"`);
    expect(existsSync(join(vendorRoot, "FixturePlugins", "Widget", "gui", "rn", "widget.tsx"))).toBe(true);
    expect(existsSync(join(vendorRoot, "FixturePlugins", "Widget", "gui", "rn", "helper.ts"))).toBe(true);
  });

  it("fails loudly when the plugin repo is not in MEMEX_PLUGIN_REPOS", () => {
    const { manifest } = makePluginRepo();
    expect(() =>
      execFileSync(process.execPath, [script], {
        env: { ...process.env, MEMEX_DEPLOYMENT: manifest, MEMEX_PLUGIN_REPOS: "" },
        stdio: "pipe",
      }),
    ).toThrow(/MEMEX_PLUGIN_REPOS/);
  });

  it("fails loudly when the module path does not exist in the repo", () => {
    const { repo } = makePluginRepo();
    const bad = join(tmp!, "bad.json");
    writeFileSync(bad, JSON.stringify({ name: "Bad", modules: ["plugin:FixturePlugins/Widget/gui/rn/missing"] }));
    expect(() =>
      execFileSync(process.execPath, [script], {
        env: { ...process.env, MEMEX_DEPLOYMENT: bad, MEMEX_PLUGIN_REPOS: repo },
        stdio: "pipe",
      }),
    ).toThrow(/not found/);
  });
});

describe("gen-deployment plugin path containment", () => {
  it("refuses a plugin path that escapes the repo with ..", () => {
    const { repo } = makePluginRepo();
    // A real file OUTSIDE the repo that a traversal would otherwise reach.
    writeFileSync(join(tmp!, "outside.ts"), "export default {};\n");
    const evil = join(tmp!, "evil.json");
    writeFileSync(evil, JSON.stringify({ name: "Evil", modules: ["plugin:FixturePlugins/../outside"] }));
    expect(() =>
      execFileSync(process.execPath, [script], {
        env: { ...process.env, MEMEX_DEPLOYMENT: evil, MEMEX_PLUGIN_REPOS: repo },
        stdio: "pipe",
      }),
    ).toThrow(/not found|inside the repo/);
  });
});
