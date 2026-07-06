# typed: false
# frozen_string_literal: true

# memex-local — stand up the prod-like memex stack on Colima k3s (Mac), 1:1 with
# Doc/Architecture/LocalColimaMac. The formula declares the brew toolchain and
# installs the orchestration CLI; it vendors a snapshot of the deploy/helm chart
# so a standalone install works (refreshed by `brew reinstall`). Run-from-checkout
# mode uses the live deploy/helm (or MEMEX_REPO/MEMEX_CHART_DIR) directly — that
# checkout stays the single source of truth. NOTE: a brew install's wrapper sets
# MEMEX_CHART_DIR to the vendored snapshot, so on a brew install an *exported*
# MEMEX_CHART_DIR/MEMEX_REPO does NOT override it — use run-from-checkout for that.
#
# Install (brew) — requires a local tap: current Homebrew only discovers formulae
# at a tap's root/Formula, and this formula lives at the nested
# deploy/homebrew/Formula/, so `brew install ./…/memex-local.rb` and a two-arg tap
# of the monorepo are both rejected. Until a real tap repo
# (Systemorph/homebrew-memex) is published, create a local tap from the checkout:
#   brew tap-new systemorph/memex
#   cp deploy/homebrew/Formula/memex-local.rb "$(brew --repo systemorph/memex)/Formula/"
#   brew install --HEAD systemorph/memex/memex-local
#
# Or skip brew entirely — the CLI runs straight from the checkout (its designed
# run-from-checkout mode resolves the chart + share assets from the repo):
#   ln -s "$PWD/deploy/homebrew/bin/memex-local" ~/.local/bin/memex-local   # (~/.local/bin on PATH)
#   # …or just call ./deploy/homebrew/bin/memex-local directly.
#
# Then:
#   memex-local up        # full stack
#   memex-local status
#   memex-local logs
#
class MemexLocal < Formula
  desc "Local prod-like memex portal on Colima k3s (Helm + ingress + Ollama)"
  homepage "https://github.com/Systemorph/MeshWeaver/blob/main/src/MeshWeaver.Documentation/Data/Architecture/LocalColimaMac.md"
  version "0.1.0"
  license "Apache-2.0"

  # HEAD-only: this is an internal operator tool that tracks the repo. For a
  # tagged release, add a stable `url`/`sha256` pointing at a release tarball.
  head "https://github.com/Systemorph/MeshWeaver.git", branch: "main"

  # Runtime toolchain — exactly the tools LocalColimaMac.md §1 installs.
  # NOTE: the .NET SDK is intentionally NOT a formula dependency. `depends_on cask:`
  # is rejected by current Homebrew ("Unsupported special dependency: :cask"), it's
  # needed only for the local-build image path (Option B, §3) — the ACR-pull path
  # (Option A) needs no SDK — and LocalColimaMac.md §1 treats the standalone .NET
  # installer as equally valid. The requirement is surfaced in `caveats` and enforced
  # at runtime by `preflight()` / `doctor` (which check for `dotnet` on the build path).
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

      First run (brings up colima → image → ingress → mkcert → helm → ollama →
      launchd port-forward):

        memex-local up

      Notes:
        * The default image path builds a native arm64 portal image locally
          (Option B, §3) and needs the MeshWeaver source AND the .NET SDK (10.0):
          install it from https://dotnet.microsoft.com/download or
          `brew install --cask dotnet-sdk`, then set MEMEX_REPO to your checkout.
          Or use `memex-local up --from-acr` to pull from ACR (Option A) — that
          path needs no SDK (run `az acr login -n meshweaver` first).
        * Fill in your Microsoft Entra app values in ~/.memex-local/values.local.yaml
          (generated on first run), or uncomment Authentication__EnableDevLogin
          for a no-Azure login.
        * Verbose logging is applied as a deployment-config override (kubectl set
          env) — no committed appsettings are changed.

      Portal: https://memex.localhost:8443
    EOS
  end

  test do
    assert_match "memex-local", shell_output("#{bin}/memex-local version")
    assert_match "USAGE", shell_output("#{bin}/memex-local help")
  end
end
