import { readFileSync, readdirSync, statSync } from "node:fs";
import { resolve } from "node:path";
import { describe, expect, it } from "vitest";

// GUARD THE GUARD (issue #964).
//
// Several client tests are deliberately CROSS-TREE: they read a file under src/ and assert the
// client copy matches it. That only works as a guard if the workflow that runs them is TRIGGERED by
// a change to the file they read. .github/workflows/clients.yml used to filter on "clients/**"
// alone, so every cross-tree guard was blind to the side that actually changes — a server-only PR
// added a key, CI stayed green, and the gap sat latent until an unrelated PR touched the client tree
// and inherited a red it did not cause.
//
// Widening the filter once fixes today's holes; this test is what stops the next one. Adding a new
// `../../src/...` read to a client package is itself a change under clients/**, so this job runs and
// fails until the path filter covers the new input.

const repoRoot = resolve(process.cwd(), "../..");
const workflow = readFileSync(resolve(repoRoot, ".github/workflows/clients.yml"), "utf8");

/**
 * The `paths:` list under each trigger. Parsed without a YAML dependency: find a `paths:` key, then
 * take the `- "…"` items that follow at a deeper indent.
 */
function pathFilters(): string[][] {
  const lines = workflow.split("\n");
  const lists: string[][] = [];
  for (let i = 0; i < lines.length; i++) {
    const head = /^(\s*)paths:\s*$/.exec(lines[i]);
    if (!head) continue;
    const items: string[] = [];
    for (let j = i + 1; j < lines.length; j++) {
      const item = /^\s+-\s+"([^"]+)"\s*$/.exec(lines[j]);
      if (!item) break;
      items.push(item[1]);
    }
    lists.push(items);
  }
  return lists;
}

/** Repo-relative paths outside clients/ that a client source or test file reads at runtime. */
function crossTreeReads(): string[] {
  const found = new Set<string>();
  // A `../`-relative reference (import specifier, resolve() argument, buf/protoc CLI arg), and the
  // `$repo_root/...` shape the shell codegen scripts use. Both are RESOLVED against the referencing
  // file below — a literal "../../src/x" means clients/<pkg>/src/x in some packages and the repo's
  // src/ in others, so matching the text alone would report the wrong thing either way.
  const relRef = /(?:\.\.\/)+[A-Za-z0-9_.\-/]+/g;
  const rootRef = /\$(?:repo_root|repoRoot)\/([A-Za-z0-9_.\-/]+)/g;

  const walk = (dir: string, pkgRoot: string) => {
    for (const entry of readdirSync(dir)) {
      if (entry === "node_modules" || entry === "dist" || entry === ".next" || entry === "gen") continue;
      const full = resolve(dir, entry);
      // Client packages symlink node_modules; never follow a link out of the tree.
      const st = statSync(full, { throwIfNoEntry: false });
      if (!st) continue;
      if (st.isDirectory()) {
        walk(full, pkgRoot);
        continue;
      }
      if (!/\.(ts|tsx|js|mjs|cjs|json|sh|yaml|yml)$/.test(entry)) continue;
      const text = readFileSync(full, "utf8");
      const keep = (abs: string) => {
        if (!abs.startsWith(`${repoRoot}/src/`)) return; // only reads that leave clients/
        if (!statSync(abs, { throwIfNoEntry: false })) return; // and only ones that really resolve
        found.add(abs.slice(repoRoot.length + 1));
      };
      // A `../` reference is written relative to the FILE (import specifier) or to the PACKAGE ROOT
      // (`resolve(process.cwd(), …)`, an npm-script CLI arg). Try both bases and keep whichever
      // actually lands on a file in src/ — that existence check is also what filters out the noise
      // ("../..", "../../.." in prose) a purely textual match would report.
      for (const m of text.matchAll(relRef)) {
        keep(resolve(dir, m[0]));
        keep(resolve(pkgRoot, m[0]));
      }
      for (const m of text.matchAll(rootRef)) keep(resolve(repoRoot, m[1]));
    }
  };
  // Each immediate child of clients/ is a package; its root is the base for cwd-relative reads.
  const clientsDir = resolve(repoRoot, "clients");
  for (const pkg of readdirSync(clientsDir)) {
    const pkgRoot = resolve(clientsDir, pkg);
    if (statSync(pkgRoot, { throwIfNoEntry: false })?.isDirectory()) walk(pkgRoot, pkgRoot);
  }
  return [...found].sort();
}

/** Does a workflow `paths:` entry match this repo-relative file (or directory) path? */
function covers(filter: string, path: string): boolean {
  if (filter.endsWith("/**")) {
    const prefix = filter.slice(0, -3);
    // A reference to the directory ITSELF (e.g. buf generate <dir>) is covered too.
    return path === prefix || path.startsWith(`${prefix}/`);
  }
  return filter === path;
}

describe("clients.yml trigger covers every input the jobs read", () => {
  it("declares a paths filter on both the pull_request and push triggers", () => {
    const lists = pathFilters();
    expect(lists).toHaveLength(2);
    for (const list of lists) expect(list.length).toBeGreaterThan(2);
  });

  it("keeps the pull_request and push filters identical", () => {
    // A filter that fires on the PR but not the push (or vice versa) is the same blind spot in half
    // the pipeline — and the Actions parser cannot share them through a YAML anchor.
    const [pr, push] = pathFilters();
    expect(push).toEqual(pr);
  });

  it("scrapes a plausible set of cross-tree reads", () => {
    // If the scrape breaks, the coverage assertion below would vacuously pass on an empty list.
    const reads = crossTreeReads();
    expect(reads.length).toBeGreaterThan(2);
    expect(reads).toContain("src/MeshWeaver.Blazor/BlazorViewRegistry.cs");
  });

  it("every server file a client reads is in the trigger", () => {
    const [filters] = pathFilters();
    const uncovered = crossTreeReads().filter((p) => !filters.some((f) => covers(f, p)));
    expect(
      uncovered,
      `clients.yml is not triggered by: ${uncovered.join(", ")} — a change to these would leave the ` +
        `client guard latent. Add them to BOTH paths: lists in .github/workflows/clients.yml.`,
    ).toEqual([]);
  });
});
