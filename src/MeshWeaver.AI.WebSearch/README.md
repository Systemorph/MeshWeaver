# MeshWeaver.AI.WebSearch

The web-search tool family for MeshWeaver agents, shipped as a module: an agent that declares
`WebSearch` in its frontmatter gets `SearchWeb`, `FetchWebPage` and the feed/sitemap readers.

## Activating

List the DLL under `Modules:Assemblies` and configure a backend:

```json
{
  "Modules": { "Assemblies": [ "MeshWeaver.AI.WebSearch.dll" ] },
  "WebSearch": {
    "Provider": "Google",
    "Google": { "ApiKey": "…", "Cx": "…" }
  }
}
```

`Provider` defaults to `None`, which auto-detects from whatever credentials are present. With no
backend configured the plugin advertises no search tool at all, so a listed-but-unconfigured
deployment behaves exactly like one without the module.

## Why it is a module

Agent plugins resolve **by name** out of DI (`IAgentPlugin.Name`), never by type reference. The
factory that wires an agent's tools never mentions this assembly, so the whole family lives
outside `MeshWeaver.AI` with no seam to maintain — and a deployment that should not reach the
public internet from agent turns simply omits the DLL.

## Contents

- `WebSearchPlugin` — the tools, plus the Google Custom Search parsing and the RSS/Atom/sitemap
  extraction (`ExtractFeedItems`).
- `WebSearchProviders` — provider selection and the per-provider request shapes.
- `WebSearchModuleAttribute` / `WebSearchExtensions.AddWebSearch` — the two registration lanes,
  sharing one configure path.
