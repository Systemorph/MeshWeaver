# Base image for the portal-ai flavour: the ASP.NET runtime PLUS the two co-hosted CLIs
# (Node 20 + Claude Code + GitHub Copilot) PLUS a headless Chromium for document export.
# This is the ONE hand-authored image artifact — Aspire's SDK container build can't apt-install
# Node, so it layers the published portal app
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

# ── Headless Chromium: the document-export print engine ──────────────────────────────────────
# The portal prints PDFs with a headless browser. Today that serves pixel-faithful deck export,
# which is opt-in and resolves an *already-installed* browser; #1230 makes it the renderer for
# EVERY PDF, replacing a document model whose licence MeshWeaver cannot ship under. Either way
# the browser has to be IN the image: a portal whose PDF export depends on an operator having
# installed Chrome by hand has no PDF export. Three candidate sources, only one is multi-arch:
#
#   • `apt-get install chromium` / `chromium-browser` — a TRAP on this base. mcr aspnet:10.0 is
#     Ubuntu 24.04 (noble), where `chromium-browser` is version `2:1snap1-0ubuntu2`: a snap SHIM
#     that `PreDepends: snapd`. It installs no browser at all in a container, and there is no
#     plain `chromium` deb in noble. Debian has one, but .NET 10 GA publishes no Debian tag
#     (`10.0-trixie-slim` is preview-only), so switching the base is not on the table.
#   • Google Chrome / Microsoft Edge — no Linux **arm64** build exists for either. Using one
#     would build a green amd64 leg and an arm64 leg with no browser: exactly the silent
#     single-arch failure this file's multi-arch note warns about.
#   • Playwright's Chromium — published for BOTH linux x64 and linux arm64, and its
#     `install --with-deps` resolves this precise Ubuntu release's shared-library and font set
#     per architecture. That is why it is what we use, npm-fetched with the Node already here.
#
# `--only-shell` installs the **headless shell** build rather than the full desktop browser, and
# that is not merely a size trim — MEASURED in this exact image, the full browser NEVER FINISHES
# a print. It blocks in its desktop start-up path (dbus, UPower, GSettings schemas, the component
# updater — none of which exist in a container): 150 s in, no PDF, process still alive. Through
# the renderer that is a hung export that fails on the 2-minute timeout. The headless shell has
# none of that machinery, printed the same document in ~2 s, and is 340 MB instead of 641 MB. It
# is the same Blink engine and the same Chromium version, so rendering is identical; it just has
# no browser wrapped around it. Smaller, faster, and the one that works.
#
# The version is pinned: the browser is part of the image contract, and an unpinned `@latest`
# would silently change the renderer under a rebuild. PLAYWRIGHT_BROWSERS_PATH puts it in /opt
# (world-readable, uid-independent) instead of a per-HOME cache, and the `/usr/bin/chromium`
# symlink is resolved from whatever revision directory the pinned version lays down — so the
# path never encodes a revision number, and MeshWeaver's first well-known probe location finds
# it with no configuration at all. CHROME_BIN is set too: it is the convention every other tool
# in the image reads, and it makes the resolution explicit rather than incidental.
ARG PLAYWRIGHT_VERSION=1.62.1
ENV PLAYWRIGHT_BROWSERS_PATH=/opt/ms-playwright
RUN npx --yes playwright-core@${PLAYWRIGHT_VERSION} install --with-deps --only-shell chromium \
    # 🚨 The binary has a DIFFERENT NAME on each architecture, and this is precisely the
    # single-arch trap: on arm64 Playwright installs its own Chromium build, whose shell is
    # `headless_shell`; on amd64 it installs the Chrome-for-Testing shell, which is
    # `chrome-headless-shell`. Matching only the first name built a perfectly green arm64 leg
    # and an amd64 leg with no browser. Match both — and if neither is present, print the tree
    # and fail, because a `find` that returns nothing otherwise degrades into a bare `ln` error
    # that names nothing useful.
    # `-executable -print -quit` rather than `| head -n 1`: stop at the first hit instead of
    # walking the whole ~350 MB browser tree and throwing the rest away (it is emulated on one
    # of the two legs), and require the match to actually BE a program so a same-named data
    # file could never be symlinked in as the browser.
    && BROWSER="$(find /opt/ms-playwright -type f -executable \
         \( -name headless_shell -o -name chrome-headless-shell \) -print -quit)" \
    && { [ -n "$BROWSER" ] || { \
           echo "No headless-shell binary under /opt/ms-playwright for $(uname -m):"; \
           find /opt/ms-playwright -maxdepth 3; exit 1; }; } \
    && ln -s "$BROWSER" /usr/bin/chromium \
    # Fail the BUILD, not a user's export, if that symlink does not resolve to a browser that
    # actually RUNS on this architecture — neither leg may ship a base image without one.
    && /usr/bin/chromium --version \
    && npm cache clean --force \
    && rm -rf /root/.npm /root/.cache \
    && apt-get clean \
    && rm -rf /var/lib/apt/lists/*
ENV CHROME_BIN=/usr/bin/chromium

# Make the generic font families resolve to faces that actually have a bold. Its own layer, and
# deliberately AFTER the browser install: changing a font default must not invalidate a 20-minute
# multi-arch browser layer. See the file for what goes wrong without it.
COPY fonts-local.conf /etc/fonts/local.conf

# Per-user CLI config / shared state mount point (a local volume single-node; an NFS / Azure
# Files share in HA so every replica sees the same per-user .claude / copilot dirs).
ENV ClaudeCode__ConfigDirRoot=/mnt/users

# On-disk git working-tree root (the RWX workspace PVC mount). Binds to GitWorkingTreeOptions.Root
# via GitWorkspace:Root; per-user trees live at /workspace/{userId}/{repoSlug}. The deployment must
# mount the memex-workspace PVC here for checkout/commit to work.
ENV GitWorkspace__Root=/workspace
