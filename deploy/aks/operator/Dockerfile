# The hosting operator image: what an instance lifecycle run executes.
#
# BUILD CONTEXT IS `deploy/`, not this directory:
#     docker build -f deploy/aks/operator/Dockerfile -t $ACR/hosting-operator:<tag> deploy/
# because the image carries the CHART as well as the scripts (see the COPY below).
#
# It carries az + kubectl + helm + the PostgreSQL client, because a lifecycle run legitimately
# needs all four and the portal image carries none of them. That is the honest reason the run
# happens in a Job rather than in the portal process — and the useful one: the Job is a separate,
# short-lived identity whose credential nothing long-running holds.
FROM mcr.microsoft.com/azure-cli:2.77.0

# 🚨 THE BASE IS AZURE LINUX 3, NOT ALPINE. This file previously ran `apk add` and busybox
# `adduser -D`; neither exists here, so the image had never actually been built — every `docker
# build` of it exits 127. That is worth stating rather than quietly fixing, because an operator
# image that cannot be built is indistinguishable, from the mesh's side, from one that was never
# configured: HostingOperator refuses with "no operator image configured" either way.
ARG KUBECTL_VERSION=v1.31.4
ARG HELM_VERSION=v3.16.3
# Supplied by BuildKit. The binaries below are per-architecture, and hard-coding amd64 produces an
# image that builds fine on an arm64 laptop and then cannot exec kubectl.
ARG TARGETARCH=amd64

# postgresql gives pg_dump/pg_restore/psql. 🚨 The client MAJOR must be >= the server's, or pg_dump
# refuses with "server version mismatch". Azure Linux 3 ships 16.x and the fleet's flexible servers
# are major 16 (memexaks-pg, verified 2026-08-22) — bump both together when the fleet moves.
# tar: needed to unpack helm, and not in the base. shadow-utils: useradd, for the non-root user.
# jq: hosting-audit's detection is a jq program (bin/_audit.jq) — without it the audit cannot
# compare a single field, and it refuses rather than reporting a namespace it never read as clean.
RUN tdnf install -y --refresh postgresql tar shadow-utils ca-certificates jq \
 && tdnf clean all \
 && curl -fsSLo /usr/local/bin/kubectl \
      "https://dl.k8s.io/release/${KUBECTL_VERSION}/bin/linux/${TARGETARCH}/kubectl" \
 && chmod +x /usr/local/bin/kubectl \
 && curl -fsSL "https://get.helm.sh/helm-${HELM_VERSION}-linux-${TARGETARCH}.tar.gz" \
      | tar -xz -C /tmp "linux-${TARGETARCH}/helm" \
 && mv "/tmp/linux-${TARGETARCH}/helm" /usr/local/bin/helm \
 && rm -rf "/tmp/linux-${TARGETARCH}"

COPY aks/operator/bin/ /opt/hosting/bin/
RUN chmod +x /opt/hosting/bin/* && ln -s /opt/hosting/bin/hosting-* /usr/local/bin/

# 🚨 THE CHART IS BAKED IN, and the build context is `deploy/` for exactly this reason.
#
# hosting-deploy runs `helm upgrade` against /opt/hosting/chart, so THE OPERATOR IMAGE VERSION IS
# THE CHART VERSION IT DEPLOYS. That pinning is the point: the scripts pass `--set` flags that only
# make sense against the templates shipped beside them, and a chart fetched at run time could drift
# from them silently — rendering a release nobody reviewed, which is the failure the whole
# repo-driven deploy exists to prevent.
COPY helm/ /opt/hosting/chart/

# Non-root. Nothing here needs to be root, and a container that can delete namespaces should not
# also be able to do whatever a root process on a node could.
RUN useradd --uid 1654 --create-home --shell /sbin/nologin operator
USER 1654

ENTRYPOINT ["/bin/bash", "/opt/hosting/bin/run.sh"]
