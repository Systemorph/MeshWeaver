---
Name: View packs load as modules
Category: Feature
Description: The Radzen, Analysis, and Google Maps view packs now activate through the deployment's module list instead of compiled-in feature flags — drop a line to drop a pack.
Icon: PuzzlePiece
Order: -20260815
---

# View packs load as modules

The optional UI view packs — Radzen charts and pivot grid, the analysis views, and the Google
Maps renderer — now activate through the deployment's module list (`Modules:Assemblies`), the
same lane the AI provider packs already use. Each pack registers itself when its assembly is
listed, so a deployment picks its UI packs by editing one config list instead of toggling
feature flags, and future packs can ship without any portal change. The former
`Features:UiPacks` flags are retired.
