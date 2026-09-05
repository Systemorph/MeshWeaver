---
NodeType: Markdown
Name: "Platform Build Identity"
Abstract: "How a running portal knows WHICH build it is: the two stamps (assembly metadata for the commit and base version, an image-config environment variable for the run number), the one selection rule both version surfaces share, the delivery contract that bakes the version rather than only tagging it, and the gate that reads it back out of the published image."
Icon: "<svg viewBox='0 0 24 24' xmlns='http://www.w3.org/2000/svg'><rect width='24' height='24' rx='4' fill='#4338ca'/><path d='M12 4.2 19 8v8l-7 3.8L5 16V8z' fill='none' stroke='white' stroke-width='1.6' stroke-linejoin='round'/><path d='M12 11.4 19 8M12 11.4 5 8m7 3.4v8.4' fill='none' stroke='white' stroke-width='1.4' stroke-linejoin='round'/></svg>"
Authors:
  - "Samuel Glauser"
Tags:
  - "Architecture"
  - "Release"
  - "Deployment"
  - "CI/CD"
---

# Platform Build Identity

A running portal has to be able to answer **which build am I**. Three things depend on the answer,
and only one of them is a display:

1. **The self-updater.** `VersionSelect.IsNewer` compares registry tags against the running
   version to decide whether a newer release exists. This is the load-bearing consumer.
2. **Settings → About**, and the build chip beside the alerts bell in the header.
3. **`/api/version`**, the anonymous endpoint that lets a deploy check, a monitor or a person
   verify a rollout from outside the portal.

They must all name the same build. This page is how that is arranged, and what happened when it
was not.

## The two stamps, and why there are two

| What | Where it lives | Produced by |
|---|---|---|
| Base version + commit sha (`3.0.0-rc9+<sha>`) | **Assembly metadata** on every assembly of the build | `PlatformVersion` and the `AddCommitHashMetadata` target in the root `Directory.Build.props` |
| Full run-numbered version (`3.0.0-rc9.ci.7231`) | **Container image config**, as `MESHWEAVER_PLATFORM_VERSION` | `-p:Version=` on the container publish → the `ContainerEnvironmentVariable` item in `Memex.Portal.Distributed.csproj` |

The split is not incidental. CI compile inputs are **commit-deterministic** (#1660 WS3): the
framework identity that lets CI-baked NodeType assemblies seed at boot must be equal between the
run that bakes them and the run that builds the image, so the CI run number cannot be compiled into
any assembly. It therefore rides the image configuration instead — which means it exists only if
the publish is told what it is.

## The one selection rule

`MeshWeaver.Mesh.PlatformBuildInfo` owns it, and every version surface reads it:

- `SelectBuildAssembly(entry)` — the entry assembly **if this build stamped it** (it carries a
  `CommitHash`), otherwise an assembly that is part of this build.
- `AssemblyVersion` / `CommitHash` — read off that assembly. `/api/version` reports exactly these,
  because it promises assembly metadata and nothing else.
- `PlatformVersion` — `MESHWEAVER_PLATFORM_VERSION` if the container publish supplied one, else
  `AssemblyVersion`. This is what About shows and what the self-updater compares.

> 🚨 **The entry assembly is not the build.** It is whatever host started the process, and on two
> hosts that matter it is not part of the platform build at all: a test runner
> (`test/Directory.Build.props` deliberately does not import the root props) and the deployed
> portal executable, which lives in **MeshWeaver.Plugins**, whose `src/Directory.Build.props` does
> not import core's root props either. Both report the SDK default `1.0.0` and no commit.

## The delivery contract

The version is **baked, not merely tagged**. `main-cd.yml` resolves `$(Version)` from a core
project and passes it to the portal publish as `-p:Version=`; `edge-images.yml` does the same, and
says why in the same words. A clean release (`release.yml`) bakes nothing: it retags a continuous
image, so the bytes inside report the `-ci.<n>` build they are — by design, the release IS that
build. A tag is applied to an image; it is not a property of the bytes inside it, and the two can
disagree.

Because they can disagree, they are compared. `.github/scripts/check-image-build-identity.sh` reads
`MESHWEAVER_PLATFORM_VERSION` back out of the **published image config**, for **every**
architecture in the manifest list, and fails the run if it is absent or is not the version that run
built. It runs in `portal-image` immediately after the push and **before `promote`**, so an image
that misreports itself never receives a tag any install can roll to. It fails closed: an unreadable
manifest, an absent variable and a config it cannot parse are all exit 1, because "could not check"
must never render as "checked and fine".

## The incident this page exists because of (2026-08-25 → 2026-09-01)

The portal hosts moved to MeshWeaver.Plugins on 2026-08-25. `main-cd.yml` was repointed at the
relocated project — and the `-p:Version=` half of the contract was not carried over. `$(Version)`
in that repo is the SDK default, so every `memex-portal-ai` image published for a week was **tagged**
`3.0.0-rc9.ci.<n>` and **reported itself** as `1.0.0`. Read straight out of the registry:

```
memex-portal-ai:3.0.0-rc9.ci.7231 (linux/amd64) MESHWEAVER_PLATFORM_VERSION=1.0.0
memex-portal-ai:3.0.0-rc9.ci.7231 (linux/arm64) MESHWEAVER_PLATFORM_VERSION=1.0.0
```

At the same time the About page and the self-updater were reading `Assembly.GetEntryAssembly()`
raw, while `/api/version` already had the fallback — so the same process answered
`3.0.0-rc9+0a1eabdc…` on the endpoint and `Version: 1.0.0`, `Build commit: not recorded for this
build` on the page.

**Nothing was red.** Every CI gate passed, `promote` applied a correct tag, and the one surface a
monitor would poll answered correctly. What broke was downstream of the number: with the running
version pinned at `1.0.0`, every registry tag was newer forever. The install could never reach "up
to date", it re-armed a roll on every check floor, and the header build chip stayed in its
update-available state — where a click is a hard page reload rather than a link to About, so the
page that would have shown the wrong version was the page the header could no longer reach.

Two lessons are encoded above rather than left as narrative:

- **A duplicated derivation drifts, and drifts silently.** The fallback existed in one of two
  readers for three weeks and nobody could see the difference until a third thing (the repo move)
  made the two branches diverge. The selection now lives below both readers, and a test asserts the
  two surfaces report the same build.
- **A relocation carries contracts, not just files.** This is the second time: `AssemblyVersion`
  had already been left behind by the same move (relocated assemblies built `1.0.0.0` into a
  `3.0.0.0` process, fixed 2026-08-21). Both halves were invisible to every gate because a version
  that is merely *wrong* still builds, still runs, and still looks like a version.

## Related

- [Release & Self-Update Strategy](../ReleaseStrategy) — what the compared version is compared
  *against*, and the policy that acts on the answer.
- [Module Versioning](../ModuleVersioning) — the module half: what you author, what the build
  derives.
- [Deployment](../Deployment) — "verify the IMAGE, never the green tick", of which the gate above
  is one instance.
