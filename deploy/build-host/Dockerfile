# The standing build host's RUNNER image — `mw-build-host` (Doc/Architecture/BuildHosts).
#
# What a stage needs and nothing it does not: bash, python3, git, bubblewrap (every stage runs in a
# `bwrap` sandbox — the host refuses to start without it), the .NET 10 SDK (the src/ half still
# builds with it), and the runner itself. The PLATFORM (the compiler and the reference set) is NOT
# here: it is the set's tester and portal images, copied in by the host's init containers by digest.
#
# Built and pushed by core's CD (`main-cd.yml` job `build-host-image`) as
# `mw-build-host:<version>` for every published platform build, and handed to the hosts BY DIGEST
# with the rest of the set (`BuildHostChannel.RunnerDigest`). No moving tag names it anywhere.
#
# Build from the REPO ROOT (the runner and the gate-host composer live in .github/scripts):
#   docker build -f deploy/build-host/Dockerfile -t mw-build-host:local .
FROM mcr.microsoft.com/dotnet/sdk:10.0

RUN apt-get update \
 && apt-get install -y --no-install-recommends bash python3 git bubblewrap ca-certificates \
 && rm -rf /var/lib/apt/lists/* \
 && bwrap --version && python3 --version && git --version && dotnet --version

# The runner and the composer, from the SAME commit as the rest of this platform build — one
# implementation, versioned with the image (the chart ships no copy).
COPY .github/scripts/build-run.py .github/scripts/compose-gate-host.sh /opt/build-host/
RUN chmod 0555 /opt/build-host/build-run.py /opt/build-host/compose-gate-host.sh \
 && python3 /opt/build-host/build-run.py --self-test

# The SDK image's unprivileged `app` user (uid 1654). bwrap creates its namespaces unprivileged.
USER 1654
WORKDIR /work
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
ENTRYPOINT ["/bin/bash", "-c", "set -euo pipefail; bash /opt/build-host/compose-gate-host.sh /portal/app /tester /host/app; exec python3 /opt/build-host/build-run.py --host"]
