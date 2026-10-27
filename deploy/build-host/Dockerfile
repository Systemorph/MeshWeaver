# The standing build host's RUNNER image — `mw-build-host` (Doc/Architecture/BuildHosts).
#
# What a stage needs and nothing it does not: bash, python3, git, bubblewrap (every stage runs in a
# `bwrap` sandbox — the host refuses to start without it), the .NET 10 SDK (the src/ half still
# builds with it), the PostgreSQL SERVER BINARIES (no server runs: a stage that needs a database
# starts a disposable one inside its own sandbox with sandbox-postgres.sh), and the runner with its
# helpers — module-build-key.py (the host recomputes every module cache key before promoting it),
# ci-artifact-store.py (H9: the host hands every module bundle to the shared CI artifact store)
# and core's `meshweaver-plugin-build` (module-fetch: the plugins layer from the registry's index). The PLATFORM (the compiler and the reference set) is NOT
# here: it is the set's tester and portal images, copied in by the host's init containers by digest.
#
# Built and pushed by core's CD (`main-cd.yml` job `build-host-image`) as
# `mw-build-host:<version>` for every published platform build, and handed to the hosts BY DIGEST
# with the rest of the set (`BuildHostChannel.RunnerDigest`). No moving tag names it anywhere.
#
# Build from the REPO ROOT, after publishing the plugin-build tool beside the context:
#   dotnet publish src/MeshWeaver.Plugin.Build -c Release -o artifacts/plugin-build -p:MeshWeaverRoot=$PWD/
#   docker build -f deploy/build-host/Dockerfile -t mw-build-host:local .
FROM mcr.microsoft.com/dotnet/sdk:10.0

RUN apt-get update \
 && apt-get install -y --no-install-recommends bash python3 git bubblewrap ca-certificates postgresql \
 && rm -rf /var/lib/apt/lists/* \
 && bwrap --version && python3 --version && git --version && dotnet --version \
 && find /usr/lib/postgresql -maxdepth 3 -name initdb | grep -q initdb

# The runner and the composer, from the SAME commit as the rest of this platform build — one
# implementation, versioned with the image (the chart ships no copy).
# 🚨 Every sibling a copied script LOADS is copied with it (module-build-key.py loads
# node-repo-scope.py for its linked-source matcher); `check-image-script-siblings.py` holds this list
# and the .dockerignore allow-list to that, transitively.
COPY .github/scripts/build-run.py .github/scripts/compose-gate-host.sh .github/scripts/sandbox-postgres.sh .github/scripts/module-build-key.py .github/scripts/node-repo-scope.py .github/scripts/ci-artifact-store.py /opt/build-host/
COPY artifacts/plugin-build/ /opt/build-host/plugin-build/
RUN chmod 0555 /opt/build-host/*.py /opt/build-host/*.sh \
 && test -f /opt/build-host/plugin-build/MeshWeaver.Plugin.Build.dll \
 && python3 /opt/build-host/build-run.py --self-test \
 && python3 /opt/build-host/module-build-key.py --self-test \
 && bash /opt/build-host/sandbox-postgres.sh --self-test

# The SDK image's unprivileged `app` user (uid 1654). bwrap creates its namespaces unprivileged.
USER 1654
WORKDIR /work
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
ENTRYPOINT ["/bin/bash", "-c", "set -euo pipefail; bash /opt/build-host/compose-gate-host.sh /portal/app /tester /host/app; exec python3 /opt/build-host/build-run.py --host"]
