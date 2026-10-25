# The set's core TEST image — `mw-core-tests` (Doc/Architecture/DeployTestSwitchLatest, decision D3).
#
# Core's test assemblies, built in main-cd from the SET's commit with the set's SDK, packed by
# deploy/core-tests/core-tests.py (content-addressed: an identical file in many test bins is stored
# once) together with that runner. It is the FIFTH digest of the image set
# (`BuildHostChannel.CoreTestsDigest`): a build-server deployment runs core's suites from it — one
# stage per shard, `.github/build-stages.json` — and never builds core's tests from source.
#
# The tree lives at /core-tests. A build host stages it READ-ONLY beside the tester and portal
# trees (by digest, like the rest of the set); the runner copies a shard's bins into the run's
# writable checkout before it runs them. The SDK base makes the image runnable on its own as well:
#   docker run --rm mw-core-tests:<version> python3 /core-tests/core-tests.py run --shard 0 --total 8
#
# Built and pushed by core's CD (`main-cd.yml` job `core-tests-image`). Build from the REPO ROOT
# after packing:
#   python3 deploy/core-tests/core-tests.py pack --repo . --out artifacts/core-tests --sha <sha> --version <v>
#   docker build -f deploy/core-tests/Dockerfile -t mw-core-tests:local .
FROM mcr.microsoft.com/dotnet/sdk:10.0

RUN apt-get update \
 && apt-get install -y --no-install-recommends python3 \
 && rm -rf /var/lib/apt/lists/* \
 && python3 --version && dotnet --version

COPY artifacts/core-tests/ /core-tests/
# The packed runner proves its refusals in the build, and the manifest must name the set it carries.
RUN python3 /core-tests/core-tests.py --self-test > /dev/null \
 && python3 -c "import json; m = json.load(open('/core-tests/manifest.json')); assert m['shardTotal'] >= 1 and len(m['coreSha']) == 40, m; print('mw-core-tests', m['version'], m['coreSha'], m['shardTotal'], 'shards')"

USER 1654
WORKDIR /work
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
