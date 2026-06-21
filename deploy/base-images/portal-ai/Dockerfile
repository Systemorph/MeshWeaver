# Base image for the portal-ai flavour: the ASP.NET runtime PLUS the two co-hosted CLIs
# (Node 20 + Claude Code + GitHub Copilot). This is the ONE hand-authored image artifact —
# Aspire's SDK container build can't apt-install Node, so it layers the published portal app
# ON TOP of this base via the portal's <ContainerBaseImage>. The lean `portal` flavour skips
# this and uses the default mcr.microsoft.com/dotnet/aspnet base.
#
# Build + push once per release:
#   docker build -t ghcr.io/systemorph/memex-portal-ai-base:$TAG deploy/base-images/portal-ai
#   docker push ghcr.io/systemorph/memex-portal-ai-base:$TAG
#
# Both CLIs are bundled deliberately (locked decision) rather than relying on the publish-time
# copilot fetch, which keys off the build-host RID. npm packages verified: @github/copilot
# (the GitHub Copilot CLI) + @anthropic-ai/claude-code (Claude Code has no standalone binary).
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
