---
Name: Docker Hub in CI
Category: Architecture
Description: GitHub-hosted runners pull Docker Hub anonymously, against a limit counted per egress IP and shared with every other tenant of the runner pool. A CI job that runs a Docker Hub image therefore declares it and pulls it from a digest-pinned GHCR mirror with its own GITHUB_TOKEN, or from the ACR copy when it is already logged in to ACR. The exceptions still standing are listed on the page.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="2" y="10" width="20" height="10" rx="2"/><path d="M6 10V6h4v4M10 10V6h4v4M14 10V6h4v4"/><path d="M6 15h.01M10 15h.01"/></svg>
---

# Docker Hub in CI

**A CI job does not pull the Docker Hub images it runs anonymously.** It declares them and pulls
them through a mirror. The exceptions still standing are listed under *What this does not cover*.
GitHub-hosted runners reach Docker Hub
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
   and its federated credentials match only `main`. So these jobs pulled anonymously. Both now resolve
   their image with `dockerhub-mirror.sh ref`. They do this only on a GitHub runner, and the mirror's
   digest must equal the chart's pin. On a laptop the Docker Hub name is used as written.

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
   runs daily as well. A deleted package or manifest is restored the same day, and the run is red
   only when it cannot be restored. Each `<name>:<tag>` gets one line: a second line is red, never
   "first wins".
3. **Consume** it: log in to `ghcr.io` with `GITHUB_TOKEN` (`packages: read`), then run
   `bash .github/scripts/dockerhub-mirror.sh ref <name>:<tag>`. You get the mirror reference,
   digest-pinned. An undeclared image is **red, naming the file**. There is no fallback to
   `docker.io`: a silent fallback would bring back the exact pull this page removes.

For a declared image, `dockerhub-mirror.yml` is the only place CI talks to `docker.io`. It runs
on `main` and on the schedule, once for each new digest.

## What this does not cover

- **`FROM` lines of images core builds** (`deploy/node-gate`, `deploy/python-gate`,
  `deploy/s1-extract`, `deploy/whisper`, `clients/voice-gateway`). BuildKit pulls those base
  images from Docker Hub. The workflows that build them are path-filtered and run rarely, and no
  measured failure was there. Moving their bases is a separate change, because it
  changes the production image's provenance, not only CI's.
- **The production registry's own `docker_auth`.** Production pulls it from Docker Hub on purpose
  (the bootstrap exception in [Fleet Registry Retention](../FleetRegistryRetention)). The mirror
  serves the CI check that executes it, not the deployment.

## Measured

The sweep read the logs of failed jobs in `Systemorph/MeshWeaver` and counted the ones that contain
`toomanyrequests`. It completed only a narrow window: failed runs created on 2026-10-09 between
20:46:30Z and 20:58:57Z. In that window **7 of the 8 failed runs** carried the 429:

- 3 `MeshWeaver Build and Test` runs, 23 `Run tests` shard jobs;
- 4 `Chart Gate` runs, 5 `Bundle registry scripts (executed)` jobs.

From about 20:58Z every core test shard failed the same way.

The sweep was **stopped before it read anything older**, so it says nothing about earlier days.
It was stopped because downloading logs at that rate fed GitHub's secondary rate limit, which the
concurrent agents on this account were already hitting. Before it stopped, it had listed the
week's failed runs: 1,758 in core and 1,298 in MeshWeaver.Plugins. Their logs were not read, so
this page claims no count, and no zero, for the rest of the week or for MeshWeaver.Plugins.
