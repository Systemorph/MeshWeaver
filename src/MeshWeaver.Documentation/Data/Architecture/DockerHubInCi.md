---
Name: Docker Hub in CI
Category: Architecture
Description: GitHub-hosted runners pull Docker Hub anonymously, against a limit counted per egress IP and shared with every other tenant of the runner pool. CI therefore never asks Docker Hub from a pull request. A job either pulls nothing from it, pulls a digest-pinned copy from the GHCR mirror with its own GITHUB_TOKEN, or pulls the ACR copy when it is already logged in to ACR.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="2" y="10" width="20" height="10" rx="2"/><path d="M6 10V6h4v4M10 10V6h4v4M14 10V6h4v4"/><path d="M6 15h.01M10 15h.01"/></svg>
---

# Docker Hub in CI

**A CI job never pulls from Docker Hub anonymously.** GitHub-hosted runners reach Docker Hub
from a shared pool of egress IPs. The anonymous pull limit is counted per IP, so other tenants
spend it before we do. A `toomanyrequests` answer is therefore a red that no commit causes and
no commit cures. Retrying does not help: the limit is a window of hours, not a blip of seconds.

## What happened

On 2026-10-09 every test shard of several core pull requests failed in the same step:
`toomanyrequests: You have reached your unauthenticated pull rate limit` on
`pgvector/pgvector:pg17`. The Chart Gate failed the same way on `cesanta/docker_auth` (#6381 among
them). How often it hit is under **Measured** below.

Two things were wrong, and only one of them was the rate limit:

1. **The test job pulled an image nothing used.** Every shard pre-pulled `pgvector/pgvector:pg17`
   for the PostgreSql collection fixtures. Those suites left core during the test move (#2847 and
   its successors). Since then no core suite has started a container. The pull had outlived its
   reason, and on that day it was the only failure.
2. **The jobs that DO need an image asked Docker Hub directly.** The Chart Gate runs the real
   `docker_auth` and `loki` binaries against the configuration the chart renders, which is the
   reason those checks exist. A pull request has no ACR credential in core: core pushes by OIDC,
   and its federated credentials match only `main`. So these jobs pulled anonymously.

## The three allowed shapes

| the job | pulls from | credential |
|---|---|---|
| needs no container | nothing | none |
| runs on a pull request and needs a Docker Hub image | `ghcr.io/systemorph/dockerhub/<name>:<tag>@<digest>` | the job's own `GITHUB_TOKEN`, `packages: read` |
| is already logged in to ACR (main CD, by OIDC) | `meshweaver.azurecr.io/dockerhub/<name>:<tag>` | the existing ACR login |

**Why GHCR for pull requests.** `ghcr.io/systemorph` already carries the fleet's mirror of its own
images (main CD copies them there by digest). The workflow's own token can read it, so the job
needs **no stored secret**. That covers forks and Dependabot too, which keeps
[the second secret store](../DependabotSecretStore) out of the picture.

**Why ACR on the delivery path.** The ACR `dockerhub/` copies are what the self-hosted runner sets
already use (`TESTCONTAINERS_HUB_IMAGE_NAME_PREFIX=meshweaver.azurecr.io/dockerhub/`, Memex
`deployments/aks/ci-runners`). A main CD job that has already run `acr-login.sh` uses those copies
and adds no second registry.

The self-hosted ARC sets are unchanged. Their Testcontainers prefix and the `docker login` the
module-pack lane performs already pull through ACR.

## How a job uses the GHCR mirror

1. **Declare** the image in `.github/dockerhub-mirror.list` as `<name>:<tag>@sha256:<digest>`.
   The file shows how to resolve the digest with a manifest `HEAD`, which does not count against
   the limit.
2. **Merge** that line first. `dockerhub-mirror.yml` runs on that push, copies the digest with
   `docker buildx imagetools create`, reads the copy back, and is red if the digest differs. It
   runs daily as well, so a deleted package is red the same day.
3. **Consume** it: log in to `ghcr.io` with `GITHUB_TOKEN` (`packages: read`), then run
   `bash .github/scripts/dockerhub-mirror.sh ref <name>:<tag>`. You get the mirror reference,
   digest-pinned. An undeclared image is **red, naming the file**. There is no fallback to
   `docker.io`: a silent fallback would bring back the exact pull this page removes.

`dockerhub-mirror.yml` is the only place CI talks to `docker.io`. It runs on `main` and on the
schedule, once for each new digest.

## What this does not cover

- **`FROM` lines of images core builds** (`deploy/node-gate`, `deploy/python-gate`,
  `deploy/s1-extract`, `deploy/whisper`, `clients/voice-gateway`). BuildKit pulls those base
  images from Docker Hub. The workflows that build them are path-filtered and run rarely, and no
  run in the measured week failed there. Moving their bases is a separate change, because it
  changes the production image's provenance, not only CI's.
- **The production registry's own `docker_auth`.** Production pulls it from Docker Hub on purpose
  (the bootstrap exception in [Fleet Registry Retention](../FleetRegistryRetention)). The mirror
  serves the CI check that executes it, not the deployment.

## Measured

The sweep read every failed job log in `Systemorph/MeshWeaver` and `Systemorph/MeshWeaver.Plugins`
over the window the run search returned, and counted the logs that contain `toomanyrequests`. The
counts, and the window each count covers, are in the pull request body.
