# The hosting operator image: what an instance lifecycle run executes.
#
# It carries az + kubectl + helm + the PostgreSQL client, because a lifecycle run legitimately
# needs all four and the portal image carries none of them. That is the honest reason the run
# happens in a Job rather than in the portal process — and the useful one: the Job is a separate,
# short-lived identity whose credential nothing long-running holds.
FROM mcr.microsoft.com/azure-cli:2.77.0

ARG KUBECTL_VERSION=v1.31.4
ARG HELM_VERSION=v3.16.3

# postgresql17-client gives pg_dump/pg_restore/psql. The MAJOR version must be >= the server's, or
# pg_dump refuses with "server version mismatch" — bump this when the fleet's server moves.
RUN apk add --no-cache postgresql17-client bash curl coreutils jq \
 && curl -fsSLo /usr/local/bin/kubectl \
      "https://dl.k8s.io/release/${KUBECTL_VERSION}/bin/linux/amd64/kubectl" \
 && chmod +x /usr/local/bin/kubectl \
 && curl -fsSL "https://get.helm.sh/helm-${HELM_VERSION}-linux-amd64.tar.gz" \
      | tar -xz -C /tmp linux-amd64/helm \
 && mv /tmp/linux-amd64/helm /usr/local/bin/helm \
 && rm -rf /tmp/linux-amd64

COPY bin/ /opt/hosting/bin/
RUN chmod +x /opt/hosting/bin/* && ln -s /opt/hosting/bin/hosting-* /usr/local/bin/

# Non-root. Nothing here needs to be root, and a container that can delete namespaces should not
# also be able to do whatever a root process on a node could.
RUN adduser -D -u 1654 operator
USER 1654

ENTRYPOINT ["/bin/bash", "/opt/hosting/bin/run.sh"]
