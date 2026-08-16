# MeshWeaver.Social

Social publishing for the MeshWeaver mesh: the platform-publisher abstraction with a LinkedIn
implementation, the LinkedIn connect / company-Page sync / member publish+engagement endpoint
families (riding `MeshEndpointProviderAttribute`, design #1655/#1667), and the LinkedIn/social
node-menu providers. Ships as a mesh MODULE: `Modules:Assemblies` + the thin module lane
(`memex/MeshModulesPublish.targets`).

## 🚨 Relocating to Systemorph/MeshWeaver.SocialMedia — this copy is the MASTER until the flip

(Task 63 of the 2026-08 modularization program — a program-plan task, not a GitHub issue number.)

This module's sources ALSO live in
[MeshWeaver.SocialMedia `src/MeshWeaver.Social`](https://github.com/Systemorph/MeshWeaver.SocialMedia/tree/main/src/MeshWeaver.Social),
where the SocialMedia package is the first MIXED package (node content + compiled module:
`SocialMedia/index.json` declares `content.module` and its CI builds + module-packs the bundle).
During the double-ship transition:

- **Change the module here first, then mirror the `.cs` files to the satellite verbatim** (they are
  kept byte-identical; the satellite's csproj differs — it project-references a platform checkout).
- **Do NOT delete this project yet.** The flip is blocked by, and its PR must resolve, ALL of:
  1. `Memex.Portal.Shared` compiles against `PlatformCredential`
     (`Social/ApiCredentialNodeType.cs` — deliberately host-side so existing credential nodes keep
     deserializing) and holds the ships-the-bits `ProjectReference` (PR #1681's "not flippable"
     verdict; also `AddLinkedInAuthentication` stays platform — auth schemes configure before the
     host builds).
  2. In-mesh sources call `SocialExtensions.AddSocial` — at least
     `samples/Graph/Data/Doc/DataMesh/SocialMedia/{Post,Profile}.json` configuration lambdas —
     and NodeType compilation references only TRUSTED_PLATFORM_ASSEMBLIES, so a modules/-only
     assembly is invisible to it (the #1683/#1685 `AddApprovals` breakage class). Re-sweep
     `content/ samples/*/Data`, node JSON, AND the live meshes before deleting any symbol.
  3. The modularization program's satellite-bundle rollout (program-plan task 74, not a GitHub
     issue), so consumers land the SocialMedia-built bundle
     automatically (until then the registry's thin-lane `modules/MeshWeaver.Social/` is empty by
     construction and serves nothing).
  When it flips: remove this project + the `Memex.Portal.Shared` reference + the `@(MeshModule)`
  entry in `memex/MeshModulesPublish.targets`; the `Modules:Assemblies` entries STAY (the runtime
  then loads the landed module from `modules/`).

## Features

- `SocialMeshModuleAttribute` / `SocialModuleAttribute` — the module's two activation halves
  (DI + menu providers; endpoint contributions via `MapMeshModuleEndpoints`)
- `IPlatformPublisher` — the platform-agnostic publishing abstraction
- `LinkedInPublisher` / `LinkedInPublishService` / `LinkedInPostsApi` — publish, list, and manage
  LinkedIn posts
- `LinkedInAnalytics` — per-post analytics
- `PlatformCredential` — per-user platform credentials as mesh data (registered host-side)

## Links

- [MeshWeaver.SocialMedia repository](https://github.com/Systemorph/MeshWeaver.SocialMedia)
- [Documentation](https://memex.meshweaver.cloud/Doc)
