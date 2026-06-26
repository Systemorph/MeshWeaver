# Base image for the portal-ai flavour: the ASP.NET runtime PLUS the two co-hosted CLIs
# (Node 20 + Claude Code + GitHub Copilot). This is the ONE hand-authored image artifact —
# Aspire's SDK container build can't apt-install Node, so it layers the published portal app
# ON TOP of this base via the portal's <ContainerBaseImage>. The lean `portal` flavour skips
# this and uses the default mcr.microsoft.com/dotnet/aspnet base.
#
# Build + push once per release — MULTI-ARCH (linux/amd64 + linux/arm64) so both an x86 cloud node
# and an Apple-silicon (arm64) local k3s can pull this base — and the app image layered on it —
# natively. A single-arch `docker build` is NOT enough: it must be a manifest list.
#   docker buildx build --platform linux/amd64,linux/arm64 \
#     -t ghcr.io/systemorph/memex-portal-ai-base:$TAG --push deploy/base-images/portal-ai
# CI does exactly this in .github/workflows/release-images.yml (with docker/setup-qemu-action so the
# arm64 RUN steps emulate on an amd64 runner). For ACR, the equivalent is the same buildx --push to
# meshweaver.azurecr.io, or `az acr build --platform linux/amd64 --platform linux/arm64`.
#
# Both CLIs are bundled deliberately (locked decision) rather than relying on the publish-time
# copilot fetch, which keys off the build-host RID. npm packages verified: @github/copilot
# (the GitHub Copilot CLI) + @anthropic-ai/claude-code (Claude Code has no standalone binary).
# Multi-arch is safe: @anthropic-ai/claude-code is pure JS (arch-independent), and `npm install -g
# @github/copilot` resolves the matching per-platform optional-dep binary (@github/copilot-linux-x64
# / @github/copilot-linux-arm64) inside each platform's build — so each arch bakes in its own CLI.
ARG DOTNET_VERSION=10.0
FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION}

# git is required by BOTH the co-hosted CLIs (Claude Code / Copilot operate on a working tree)
# AND the in-portal working-tree feature (GitWorkingTreeService shells `git` for clone/commit/push).
# One system git, shared by all three — so a tree the harness edits and a tree the editor edits are
# byte-identical.
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl ca-certificates gnupg git \
    && curl -fsSL https://deb.nodesource.com/setup_20.x | bash - \
    && apt-get install -y --no-install-recommends nodejs \
    && npm install -g @anthropic-ai/claude-code @github/copilot \
    && npm cache clean --force \
    && apt-get purge -y gnupg \
    && apt-get autoremove -y \
    && apt-get clean \
    && rm -rf /var/lib/apt/lists/*

# Per-user CLI config / shared state mount point (a local volume single-node; an NFS / Azure
# Files share in HA so every replica sees the same per-user .claude / copilot dirs).
ENV ClaudeCode__ConfigDirRoot=/mnt/users

# On-disk git working-tree root (the RWX workspace PVC mount). Binds to GitWorkingTreeOptions.Root
# via GitWorkspace:Root; per-user trees live at /workspace/{userId}/{repoSlug}. The deployment must
# mount the memex-workspace PVC here for checkout/commit to work.
ENV GitWorkspace__Root=/workspace
