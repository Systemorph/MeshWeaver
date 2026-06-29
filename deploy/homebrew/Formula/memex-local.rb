# typed: false
# frozen_string_literal: true

# memex-local — stand up the prod-like memex stack on Colima k3s (Mac), 1:1 with
# Doc/Architecture/LocalColimaMac. The formula declares the brew toolchain and
# installs the orchestration CLI; it vendors a snapshot of the deploy/helm chart
# so a standalone install works, while a live MEMEX_REPO/MEMEX_CHART_DIR always
# wins (the deploy/helm chart stays the single source of truth).
#
# Tap + install:
#   brew tap systemorph/memex https://github.com/Systemorph/MeshWeaver.git
#   brew install --HEAD systemorph/memex/memex-local
#   # (or, if the tap repo is cloned locally:)
#   brew install --HEAD ./deploy/homebrew/Formula/memex-local.rb
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
  # The .NET SDK cask is needed only for the local-build image path (Option B,
  # §3); the ACR-pull path (Option A) does not require it.
  depends_on cask: "dotnet-sdk"
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
          (Option B, §3) and needs the MeshWeaver source — set MEMEX_REPO to your
          checkout, or use `memex-local up --from-acr` to pull from ACR (Option A).
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
