# typed: false
# frozen_string_literal: true

# memex-local — stand up the prod-like memex stack on Colima k3s (Mac), 1:1 with
# Doc/Architecture/LocalColimaMac. The formula declares the brew toolchain and
# installs the orchestration CLI; it vendors a snapshot of the deploy/helm chart
# so a standalone install works. Run-from-checkout mode uses the live deploy/helm
# (or MEMEX_REPO/MEMEX_CHART_DIR) directly — that checkout stays the single source
# of truth. NOTE: a brew install's wrapper sets MEMEX_CHART_DIR to the vendored
# snapshot, so on a brew install an *exported* MEMEX_CHART_DIR/MEMEX_REPO does NOT
# override it — use run-from-checkout for that.
#
# THIS FILE IS THE TEMPLATE. The published tap (Systemorph/homebrew-memex) carries the
# same formula RENDERED by deploy/homebrew/scripts/render-formula.sh with a stable
# `url`/`sha256` pointing at the tarball the platform's Homebrew workflow attaches to a
# release of that tap (version 0.2.<main commit count>, so every merge to main is an
# upgrade). Install from there:
#
#   brew tap systemorph/memex
#   brew install memex-local
#   memex-local registry https://memex.meshweaver.cloud --key mwr_…   # consume the cloud registry
#   memex-local up
#
# From a checkout (developing memex-local itself), a local tap over THIS file works too:
#   brew tap-new systemorph/memex-dev
#   cp deploy/homebrew/Formula/memex-local.rb "$(brew --repo systemorph/memex-dev)/Formula/"
#   brew install --HEAD systemorph/memex-dev/memex-local
#
class MemexLocal < Formula
  desc "Local prod-like memex portal on Colima k3s (Helm + ingress + Ollama)"
  homepage "https://github.com/Systemorph/MeshWeaver/blob/main/src/MeshWeaver.Documentation/Data/Architecture/LocalColimaMac.md"
  version "0.2.0"
  license "Apache-2.0"

  # The published tap's render inserts `url`/`sha256` here; the template stays HEAD-capable so a
  # checkout can install the tip of main.
  head "https://github.com/Systemorph/MeshWeaver.git", branch: "main"

  # Runtime toolchain — exactly the tools LocalColimaMac.md §1 installs, plus the Azure CLI the
  # registry-mode image pull needs (`az acr login`; the CI-built image lives in a private ACR).
  # NOTE: the .NET SDK is intentionally NOT a formula dependency. `depends_on cask:`
  # is rejected by current Homebrew ("Unsupported special dependency: :cask"), it's
  # needed only for the local-build image path (Option B, §3) — the ACR-pull path
  # (Option A, the registry-mode default) needs no SDK — and LocalColimaMac.md §1
  # treats the standalone .NET installer as equally valid. The requirement is
  # surfaced in `caveats` and enforced at runtime by `preflight()` / `doctor`.
  depends_on "azure-cli"       # az acr login (registry mode pulls the CI-built image)
  depends_on "colima"          # k3s VM
  depends_on "helm"            # chart install/upgrade
  depends_on "kubernetes-cli"  # kubectl
  depends_on :macos            # mkcert system-keychain + launchd are macOS-only
  depends_on "mkcert"          # locally-trusted TLS
  depends_on "ollama"          # host-native local LLM (Metal GPU)
  depends_on "socket_vmnet"    # Colima vmnet (host-gateway reachability)

  def install
    # The orchestration CLI + its share assets (overlay, port-forward.sh, plist).
    libexec.install "deploy/homebrew/bin/memex-local"
    (libexec/"share").install Dir["deploy/homebrew/share/*"]

    # Vendor a snapshot of the chart so a standalone install works offline.
    # A live MEMEX_REPO / MEMEX_CHART_DIR overrides this at runtime.
    (libexec/"share/helm").install Dir["deploy/helm/*"]

    # Wrapper on PATH that points the CLI at the vendored assets + chart.
    (bin/"memex-local").write_env_script libexec/"memex-local",
      MEMEX_LOCAL_SHARE: libexec/"share",
      MEMEX_CHART_DIR:   libexec/"share/helm"
  end

  def caveats
    <<~EOS
      memex-local automates Doc/Architecture/LocalColimaMac (Colima k3s on Mac).

      Recommended: consume the cloud plugin registry (LocalColimaMac §17). A platform
      admin mints a registration key on memex.meshweaver.cloud (Settings ▸
      Administration ▸ Instance grants ▸ Registration keys), then:

        memex-local registry https://memex.meshweaver.cloud --key mwr_…
        memex-local up

      That pulls the CI-built multi-arch image from ACR (`az login` first — no .NET
      SDK, no source checkout), registers this install at the registry on first
      boot, installs the packages it is granted and lands their compiled modules —
      the only way a local install gets Radzen/Analysis/EntityViews/GoogleMaps/Speech.

      Alternative (serving plugins from a source checkout, §16): set MEMEX_REPO to a
      MeshWeaver checkout with MeshWeaver.Plugins beside it, install the .NET SDK
      (10.0: https://dotnet.microsoft.com/download or `brew install --cask
      dotnet-sdk`) and run `memex-local up` — it builds a native arm64 image.

      Notes:
        * ~/.memex-local/values.local.yaml is generated on first run; set
          Authentication__DevAdminUsers to your username (DevLogin is the default).
        * Verbose logging is applied as a deployment-config override (kubectl set
          env) — no committed appsettings are changed.

      Portal: https://memex.localhost:8443
    EOS
  end

  test do
    assert_match "memex-local", shell_output("#{bin}/memex-local version")
    assert_match "USAGE", shell_output("#{bin}/memex-local help")
    # Registry mode is a state machine over one file; `status` must answer without a cluster.
    assert_match "SELF-REGISTRY", shell_output("#{bin}/memex-local registry status")
  end
end
