---
Name: Portal-AI Base Image
Category: Architecture
Description: How the base image under every portal-ai and control image is named by its content, built by CD when absent, and held to the platform's target framework
Icon: Server
---

# Portal-AI Base Image

The portal-ai and control images are **framework-dependent** publishes (`--no-self-contained`) of the
portal host. Their .NET runtime is not in the publish: it comes from the image they are layered on,
`memex-portal-ai-base`, built from `deploy/base-images/portal-ai/Dockerfile` (ASP.NET runtime + Node
+ the co-hosted agent CLIs + Chromium). So the base and the platform must agree on the runtime, and
the base CD layers on must be the one the tree being built describes.

## The rule

1. **The base is named by its content.** Its tag is `src-<16 hex>`: a SHA-256 over every file under
   `deploy/base-images/portal-ai` (relative path and bytes). `.github/scripts/portal-ai-base.py tag`
   prints it, with no network.
2. **CD builds it when it is absent.** main-cd's `portal-ai-base` job (and `edge-images.yml`) runs
   `.github/scripts/ensure-portal-ai-base.sh`. It lists the repository's tags, builds the base
   multi-arch with buildx when this tree's tag is missing, and refuses an index without both a
   `linux/amd64` and a `linux/arm64` child. A tag listing it cannot read is red, never read as
   "absent" or "present". The portal-ai and control publishes take
   `ContainerBaseImage=…/memex-portal-ai-base:${{ needs.portal-ai-base.outputs.tag }}`.
3. **The Dockerfile's runtime equals the platform's target framework.** `ARG DOTNET_VERSION` must
   equal `<TargetFramework>` in `Directory.Build.props` (`11.0` = `net11.0`).
   `portal-ai-base.py check` makes a PR red when they differ, and it also refuses any workflow
   `ContainerBaseImage` that names the base by a literal tag. The check runs in the platform PR
   gate, and `ensure-portal-ai-base.sh` runs it again before it builds anything.

Each delivery line (policy `major-lines-run-separately`) therefore layers on exactly the base its
own tree describes. Rebuilding the base for one line cannot change what another line's build
layers on.

## Why: the first v4 CD run

Run 43592014876, the first CD run on the v4 line (.NET 11), published a control image that exited
at once in the control image acceptance test (leg a, empty database). Reproduced locally with the
same host on an aspnet:10.0 base:

```
You must install or update .NET to run this application.
App: /app/Memex.Portal.Distributed.dll
Framework: 'Microsoft.NETCore.App', version '11.0.0' (arm64)
The following frameworks were found:
  10.0.15 at [/usr/share/dotnet/shared/Microsoft.NETCore.App]
```

The exit code was 150, before `Main`. The v4 change moved the Dockerfile to `aspnet:11.0`. But
main-cd layered on `memex-portal-ai-base:latest`, and only the hand-dispatched
`base-image-acr.yml` wrote that tag. Its last run (2026-09-16, commit `eb5ee75b7d`) built from a
Dockerfile that said `10.0`. The tag did not change when the tree did, so the build used bytes the
tree no longer described. The same host on an aspnet:11.0 base answered `/health` and `/ready`
in about 5 s. It loaded `MeshWeaver.Fleet.Control` from `modules/` and reported the adopt-only boot
over 0 dynamic NodeTypes, which is the evidence leg (a) asks for.

## The hand runbook

`base-image-acr.yml` (workflow_dispatch) remains for refreshing the bundled CLIs. It writes the
content tag as well as `latest` and `git-<sha>`. `latest` is kept for consumers outside this
repository. No core workflow layers on it.
