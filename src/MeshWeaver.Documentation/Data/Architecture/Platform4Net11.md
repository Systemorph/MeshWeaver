---
Name: Platform 4 on .NET 11
Category: Architecture
Description: The declared break that moves the platform to .NET 11 and version line 4.0 — what changed in core, the order the fleet follows, and how instances move from updatePattern 3.* to 4.*.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M12 19V5"/><path d="m5 12 7-7 7 7"/></svg>
---

# Platform 4 on .NET 11

Line 4.0 is ONE declared break (policy `platform-4-net11`): the target framework moves `net10.0` →
`net11.0`, every package pin moves to its newest stable release, and `PlatformVersion` moves
`3.1.0` → `4.0.0`. A major bump is the declared break [Module Versioning](../ModuleVersioning) and
[Deploying Across Platform Versions](../DeployingAcrossPlatformVersions) describe. The new framework
identity makes every 3.x module bundle foreign to a 4.x replica. Adoption therefore refuses those
bundles, and each NodeType compiles its live source until its repository re-bakes against a sealed
4.0 set.

## What changed in core

| Area | Change |
|---|---|
| Target framework | `Directory.Build.props` and `test/Directory.Build.props` → `net11.0`, plus every generated project, script path and test fixture that spelled the TFM |
| SDK | every `setup-dotnet` step → `11.0.x`; `deploy/core-tests` → `mcr.microsoft.com/dotnet/sdk:11.0`; the `portal-ai` base → `aspnet:11.0` |
| Packages | every `Microsoft.Extensions.*`, `System.*`, `Microsoft.AspNetCore.TestHost` pin → `11.0.0`; OpenTelemetry → 1.21.0; Azure.Core, System.ClientModel, MSAL, Test SDK → newest stable; two independent majors: **NuGet.\* 7 → 8** and **HtmlSanitizer 9 → 11** (ledger in [Dependency Major Upgrades](../DependencyMajorUpgrades)) |
| Version | `PlatformVersion` → `4.0.0`; main CD now mints `4.0.<run>` and moves `4-latest` / `4.0-latest`. The channel file is templated on `{major}`, so no label is spelled by hand |
| Source | one fix: an XML `cref` to `ChangeToken.OnChange{TState}` became ambiguous (CS0419) when Primitives 11 added an overload. It now names the non-generic overload the code calls |

Orleans stays on 10.4.2, its newest release, which targets `net8.0+` and runs on the 11 runtime.

## The order the fleet follows

The break lands dependency-first. Each step waits for the previous one's sealed output, and none
builds against an older platform (policy `request-min-version-wait-until-deployed`):

1. **Core** merges and seals the first `4.0.<run>` set.
2. **MeshWeaver.Plugins** retargets, updates its own pins and raises its platform floor to that
   sealed set in the same pull request. Its run waits until a build server carrying 4.0 is deployed.
3. **The satellites** (Education, Reinsurance, SocialMedia, Manufacturing, Crm, FundReporting) do
   the same against the sealed Plugins publication, then re-bake their modules.
4. **MeshWeaver.Deploy** and **Memex** retarget their tooling.
5. **Instances.** The control instance's record moves to `updatePattern: "4.*"` first and is
   verified there: the NodeType compile sweep, the bake report and the end-to-end suite. Only then
   does every other `Deployments/*` record move to `4.*` in one Memex pull request
   (`platform-deploy-control-first`).

An instance whose record still reads `3.*` never admits a 4.x tag. The record change is what
releases the break to an instance, not the merge.

## Local machines

`net11.0` needs SDK 11. Install it next to the 10.x SDKs with `dotnet-install.sh --version 11.0.100
--install-dir ~/.dotnet`. 🚨 On macOS the installer overwrites the shared `dotnet` host in place.
The kernel keeps the old binary's cached code signature, so every later `dotnet` call is killed
with exit 137 and prints nothing. Replace the file through a new inode:
`cp ~/.dotnet/dotnet ~/.dotnet/dotnet.new && mv -f ~/.dotnet/dotnet.new ~/.dotnet/dotnet`.
