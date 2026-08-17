---
Name: Install the content compiler as a dotnet tool
Category: Feature
Description: The content compiler that gates and bakes mesh content in CI is now installable as the MeshWeaver.Compiler.Cli dotnet tool (command mw-compiler) — content repos run the exact binary the platform runs, with no platform-repo download.
Icon: Sparkle
Order: -20260817
---

# Install the content compiler as a dotnet tool

The compiler that CI uses to gate and bake mesh content — the same code path the portals compile
with at runtime — can now be installed directly:

```
dotnet tool install -g MeshWeaver.Compiler.Cli
mw-compiler <checkout-root> --bake-output <dir>
mw-compiler --print-framework-identity
```

A content repository's CI installs the tool version matching its platform, compiles its node
trees, and publishes prebuilt-assembly bundles the portals adopt at boot instead of recompiling.
The framework build identity resolves from the manifest packed inside the tool, so what the tool
bakes and what a portal would accept can never disagree. The same binary also ships as the
existing `mw-plugin-test` container image for docker-based pipelines.
