#!/usr/bin/env python3
"""EXECUTE the bundle-registry scripts against real registries, and prove what they do.

WHY
---
Doc/Architecture/PluginBundlesInTheRegistry puts plugin bundles into the fleet registry as OCI
artifacts, authorized per repository at the edge by memex's licence answer, and materialised into
the pre-warm's directory by an init container before the portal starts. Three pieces of shell do
all of it and nothing else executes them before a deploy:

  * .github/scripts/push-bundle-publication.sh   the publisher (standalone; a lane will call it)
  * deploy/helm/files/bundle-fetch.sh            the `bundle-fetch` init container's script
  * deploy/helm/files/registry-validate.sh       docker_auth's ext_auth hook, emitting the labels
                                                 the ACL in templates/registry/configmap.yaml
                                                 authorizes plugin repositories with

The first execution of an edit to any of them would otherwise be a production publish, a portal
that boots on a half-fetched shelf, or a registry that either denies every installation or lets a
plan-scoped licence pull a whole publication. This harness runs the REAL scripts, the REAL
distribution image and the REAL docker_auth image with the ConfigMap the chart RENDERS (helm
template, the registry example values), against a stub of memex's key→token exchange.

HOW IT STAYS HONEST
-------------------
* The scripts run from their real paths; a copy passes while the real thing rots.
* The docker_auth config and the validator are taken from the RENDERED chart, so the ACL under
  test is the ACL a deploy ships — an edit to the template is what this harness judges.
* Verdicts are read off the REGISTRY (what resolves, what lands on disk, byte for byte), never
  off a script's own log.
* Both directions are asserted for every account: what it MAY pull and what it MAY NOT. A matrix
  with only allows cannot tell "the ACL grants correctly" from "the ACL grants everything".
* The stub records every exchange it saw; the harness asserts the validator POSTed with the
  bearer, and that a wrong publisher password never reached it (docker_auth's static account
  short-circuits — if it did not, the publisher's password would be presented to memex).
* Every case prints its denominator, and a run that asserted nothing FAILS.

WHAT THIS HARNESS CANNOT PROVE
------------------------------
The stub is not memex: it answers the `scope` array per key from a table. Which entries memex
puts there for a real grant is memex's contract (InstanceTokenEndpoints.EffectiveScope), asserted
in the Plugins repo. Here the entries are the documented shapes — `Plugins/*`,
`Reinsurance/UWDeepfield`, `Plugins/Edu`, `Plugins/*@pro` — and the assertion is what the EDGE
makes of each. No Kubernetes is involved either: the init container's mounts and environment are
asserted by check-chart-invariants (invariant 13); this harness runs the script those mounts feed.

Needs: docker, helm, openssl, shellcheck, PyYAML; ORAS is installed from a PINNED release
(version + sha256 per platform) unless $ORAS names a binary.
"""

from __future__ import annotations

import base64
import hashlib
import http.server
import io
import json
import os
import platform
import shutil
import socket
import subprocess
import sys
import tarfile
import tempfile
import threading
import urllib.error
import urllib.request
from pathlib import Path

try:
    import yaml
except ImportError:  # pragma: no cover — asserted below as a preflight, with the install hint
    yaml = None

HERE = Path(__file__).resolve().parent
REPO = HERE.parent.parent
PUSH = HERE / "push-bundle-publication.sh"
FETCH = REPO / "deploy/helm/files/bundle-fetch.sh"
VALIDATE = REPO / "deploy/helm/files/registry-validate.sh"
CHART = REPO / "deploy/helm"

# The images the chart pins (values.registry.example.yaml / values.yaml). Read from the values so
# the harness cannot drift from the chart: it tests whatever the chart would deploy.
ORAS_VERSION = "1.3.4"
ORAS_SHA256 = {
    ("linux", "x86_64"): "f27adb935022d94df8dc77719c322dda592c78a0d57a6f7dcdd8d900b248c454",
    ("linux", "aarch64"): "15702c6e3a4a56a8bd8ac5c17efdbcab56d9bada661ccbcf017f5b10c1d89399",
    ("darwin", "arm64"): "217761a9500242ff473de8656b5aca21136ff39e17e9e61fd8936bbfd902704c",
    ("darwin", "x86_64"): "5e964f3d5a36eb9499a9d3e252a86b09e7adf3e6f6447eec56fd249c6702af7e",
}
ORAS_ARCH = {"x86_64": "amd64", "aarch64": "arm64", "arm64": "arm64"}

IDENTITY = "s0123456789abcdef0123456789abcdef"
PUBLISHER_PASSWORD = "example-publisher-password"  # the hash in values.registry.example.yaml
KEYS = {
    "full": "mwi_full",  # Plugins/* plan-less + Reinsurance/UWDeepfield
    "pro": "mwi_pro",    # Plugins/*@pro — packages by tier, never the index
    "edu": "mwi_edu",    # Plugins/Edu only
    "none": "mwi_none",  # licensed for nothing → the exchange answers 403
}
SCOPES = {
    "mwi_full": ["Plugins/*", "Reinsurance/UWDeepfield"],
    "mwi_pro": ["Plugins/*@pro"],
    "mwi_edu": ["Plugins/Edu"],
}

checks = 0
failures: list[str] = []


def ok(msg: str) -> None:
    global checks
    checks += 1
    print(f"  ✅ {msg}")


def bad(msg: str) -> None:
    global checks
    checks += 1
    failures.append(msg)
    print(f"  ❌ {msg}")


def expect(cond: bool, msg: str) -> None:
    (ok if cond else bad)(msg)


def run(cmd: list[str], **kw) -> subprocess.CompletedProcess:
    return subprocess.run(cmd, capture_output=True, text=True, **kw)


def free_port() -> int:
    with socket.socket() as s:
        s.bind(("127.0.0.1", 0))
        return s.getsockname()[1]


def sha256_of(path: Path) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as fh:
        for chunk in iter(lambda: fh.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


# ---------------------------------------------------------------------------------------------
# Preflight: every input asserted, RED naming what to provide. No skip-trapdoor.
# ---------------------------------------------------------------------------------------------
def preflight(work: Path) -> str:
    missing = []
    for tool, hint in (("docker", "docker engine (Colima locally)"), ("helm", "helm"),
                       ("openssl", "openssl"), ("shellcheck", "shellcheck"), ("tar", "tar")):
        if shutil.which(tool) is None:
            missing.append(f"{tool:10} not on PATH — {hint}")
    if yaml is None:
        missing.append("PyYAML     python3 has no 'yaml' module — pip install pyyaml")
    for f in (PUSH, FETCH, VALIDATE):
        if not f.is_file():
            missing.append(f"{f} is missing")
    if missing:
        print("::error::test-bundle-registry cannot run — provide the following:")
        for m in missing:
            print(f"  • {m}")
        sys.exit(1)
    info = run(["docker", "info", "--format", "{{.ServerVersion}}"])
    if info.returncode != 0:
        print(f"::error::docker is on PATH but the engine does not answer: {info.stderr.strip()}")
        sys.exit(1)
    oras = os.environ.get("ORAS")
    if oras and shutil.which(oras):
        return oras
    system = platform.system().lower()
    machine = platform.machine()
    key = (system, "arm64" if machine in ("arm64", "aarch64") and system == "darwin" else machine)
    if key not in ORAS_SHA256:
        print(f"::error::no pinned ORAS checksum for {system}/{machine}; set $ORAS to a binary")
        sys.exit(1)
    arch = ORAS_ARCH[machine]
    name = f"oras_{ORAS_VERSION}_{system}_{arch}.tar.gz"
    url = f"https://github.com/oras-project/oras/releases/download/v{ORAS_VERSION}/{name}"
    tgz = work / name
    with urllib.request.urlopen(url, timeout=120) as resp, open(tgz, "wb") as out:
        shutil.copyfileobj(resp, out)
    actual = sha256_of(tgz)
    if actual != ORAS_SHA256[key]:
        print(f"::error::{name} sha256 {actual} != pinned {ORAS_SHA256[key]} — refusing to run an unverified binary")
        sys.exit(1)
    with tarfile.open(tgz) as tf:
        member = tf.getmember("oras")
        with tf.extractfile(member) as src, open(work / "oras", "wb") as dst:
            shutil.copyfileobj(src, dst)
    (work / "oras").chmod(0o755)
    print(f"oras {ORAS_VERSION} installed from {url} (sha256 verified)")
    return str(work / "oras")


# ---------------------------------------------------------------------------------------------
# The fixture publication — the layout publish-bake-bundles.sh writes, with content that names
# itself so a byte-level diff has something to say.
# ---------------------------------------------------------------------------------------------
def make_publication(root: Path, tag: str = "one") -> Path:
    pub = root / f"publication-{tag}"
    (pub / "modules").mkdir(parents=True)
    (pub / "Pkg.A.zip").write_bytes(f"zip-a-{tag}\n".encode())
    (pub / "Pkg.B.zip").write_bytes(f"zip-b-{tag}\n".encode())
    (pub / "modules" / "Pkg.A.module.nupkg").write_bytes(f"nupkg-a-{tag}\n".encode())
    (pub / "modules" / "_index").write_text("Pkg.A.module.nupkg\n")
    (pub / "source-commit.txt").write_text(f"{tag}0123456789abcdef\n")
    (pub / "repository.txt").write_text("Systemorph/MeshWeaver.Plugins\n")
    (pub / "architecture.txt").write_text("linux-x64\n")
    (pub / "platform-surface.json").write_text('{"assemblies":[{"name":"MeshWeaver.Mesh.Contract","types":["A.B"]}]}')
    # A sidecar no reader knows yet: it must ride along and land, or the "new sidecar needs no
    # chart change" promise is prose.
    (pub / "new-sidecar.txt").write_text("a file a later publisher added\n")
    (pub / "_complete").write_text("Pkg.A.zip\nPkg.B.zip\n")
    return pub


def tree(root: Path) -> dict[str, bytes]:
    return {str(p.relative_to(root)): p.read_bytes() for p in sorted(root.rglob("*")) if p.is_file()}


# ---------------------------------------------------------------------------------------------
# Containers
# ---------------------------------------------------------------------------------------------
def image_pins() -> tuple[str, str]:
    example = yaml.safe_load((CHART / "values.registry.example.yaml").read_text())
    return example["registry"]["image"], example["registry"]["authImage"]


def wait_http(url: str, want: set[int], tries: int = 60) -> bool:
    import time
    for _ in range(tries):
        try:
            with urllib.request.urlopen(url, timeout=2) as r:
                if r.status in want:
                    return True
        except urllib.error.HTTPError as e:
            if e.code in want:
                return True
        except Exception:
            pass
        time.sleep(0.5)
    return False


class Containers:
    def __init__(self) -> None:
        self.names: list[str] = []

    def start(self, name: str, args: list[str]) -> None:
        run(["docker", "rm", "-f", name])
        r = run(["docker", "run", "-d", "--name", name] + args)
        if r.returncode != 0:
            print(f"::error::docker run {name} failed: {r.stderr.strip()}")
            self.stop()
            sys.exit(1)
        self.names.append(name)

    def logs(self, name: str) -> str:
        r = run(["docker", "logs", name])
        return r.stdout + r.stderr

    def stop(self) -> None:
        for n in self.names:
            run(["docker", "rm", "-f", n])


# ---------------------------------------------------------------------------------------------
# The stub of POST /api/instances/token
# ---------------------------------------------------------------------------------------------
class Stub(http.server.BaseHTTPRequestHandler):
    seen: list[dict] = []

    def log_message(self, *a):  # quiet
        pass

    def do_GET(self):
        Stub.seen.append({"method": "GET", "path": self.path})
        self.send_response(405)
        self.end_headers()

    def do_POST(self):
        length = int(self.headers.get("Content-Length") or 0)
        body = self.rfile.read(length)
        auth = self.headers.get("Authorization") or ""
        Stub.seen.append({"method": "POST", "path": self.path, "auth": auth,
                          "content_type": self.headers.get("Content-Type"), "body": body.decode()})
        key = auth.removeprefix("Bearer ").strip() if auth.startswith("Bearer ") else ""
        if self.path != "/api/instances/token":
            self.send_response(404); self.end_headers(); return
        if key in SCOPES:
            payload = {"accessToken": "mwa_x", "tokenType": "Bearer", "expiresIn": 900, "scope": SCOPES[key]}
            data = json.dumps(payload, separators=(",", ":")).encode()
            self.send_response(200)
            self.send_header("Content-Type", "application/json; charset=utf-8")
            self.send_header("Content-Length", str(len(data)))
            self.end_headers()
            self.wfile.write(data)
        elif key == KEYS["none"]:
            data = b'{"error":"This instance holds no current sync licence for the requested scope."}'
            self.send_response(403)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(data)))
            self.end_headers()
            self.wfile.write(data)
        else:
            self.send_response(401); self.end_headers()


def docker_config(path: Path, host: str, user: str, password: str) -> Path:
    token = base64.b64encode(f"{user}:{password}".encode()).decode()
    path.write_text(json.dumps({"auths": {host: {"auth": token}}}))
    return path


# ---------------------------------------------------------------------------------------------
# Case A — the publisher and the fetch, against a plain registry (no auth).
# ---------------------------------------------------------------------------------------------
def case_plain(oras: str, work: Path, containers: Containers, reg_image: str) -> None:
    print("\n== A. push-bundle-publication.sh and bundle-fetch.sh against a plain registry")
    port = free_port()
    containers.start(f"mw-bundle-plain-{os.getpid()}", [
        "-p", f"127.0.0.1:{port}:5000", "-e", "REGISTRY_STORAGE_DELETE_ENABLED=true",
        "-e", "OTEL_TRACES_EXPORTER=none", reg_image])
    host = f"localhost:{port}"
    expect(wait_http(f"http://{host}/v2/", {200}), f"distribution answers /v2/ on {host}")
    pub = make_publication(work, "one")
    env = dict(os.environ, ORAS=oras)

    def push(pubdir: Path, identity: str, *extra: str) -> subprocess.CompletedProcess:
        return run(["bash", str(PUSH), "--registry", host, "--source", "Plugins", "--identity", identity,
                    "--dir", str(pubdir), "--plain-http", *extra], env=env)

    r = push(pub, IDENTITY, "--tag-run", "4242", "--release", "3.1.0")
    expect(r.returncode == 0, f"publisher exits 0 (stderr: {r.stderr.strip()[:300]})")
    facts = [l for l in r.stdout.splitlines() if l.startswith("bundle-publication: ")]
    index_line = next((l for l in facts if " index=" in l), "")
    index_digest = next((t.split("=", 1)[1] for t in index_line.split() if t.startswith("index=")), "")
    expect(index_digest.startswith("sha256:"), f"publisher printed the index digest ({index_digest})")
    bundle_lines = [l for l in facts if l.startswith("bundle-publication: bundle=")]
    expect(len(bundle_lines) == 2, f"publisher printed one fact per bundle ({len(bundle_lines)} of 2)")
    expect("bundles=2" in index_line, "publisher printed the bundle count")
    for name, module in (("Pkg.A", "yes"), ("Pkg.B", "no")):
        line = next((l for l in bundle_lines if f"bundle={name} " in l), "")
        expect(f"module={module}" in line and "digest=sha256:" in line, f"bundle {name}: digest and module={module}")

    def resolve(ref: str) -> str:
        rr = run([oras, "resolve", "--plain-http", f"{host}/{ref}"])
        return rr.stdout.strip() if rr.returncode == 0 else ""

    expect(resolve(f"plugins/plugins:{IDENTITY}") == index_digest, "the identity tag resolves to the index")
    expect(resolve(f"plugins/plugins:{IDENTITY}-4242") == index_digest, "the immutable run tag resolves to the same index")
    for name in ("pkg.a", "pkg.b"):
        d = resolve(f"plugins/plugins/{name}:{IDENTITY}")
        expect(d.startswith("sha256:"), f"bundle repository plugins/plugins/{name} is tagged {IDENTITY}")
        expect(any(f"digest={d}" in l for l in bundle_lines), f"…and its digest is the one the publisher printed")
        expect(resolve(f"plugins/plugins/{name}:{IDENTITY}-4242") == d, f"…and carries the run tag")
    expect(resolve("plugins/releases:3.1.0").startswith("sha256:"), "the release identity is recorded at plugins/releases:3.1.0")

    # The index names its members by digest and the sidecar manifest's config carries the facts.
    idx = run([oras, "manifest", "fetch", "--plain-http", f"{host}/plugins/plugins@{index_digest}"])
    index = json.loads(idx.stdout)
    expect(index.get("mediaType") == "application/vnd.oci.image.index.v1+json", "the publication is an OCI image index")
    roles = [m.get("annotations", {}).get("io.meshweaver.role") for m in index["manifests"]]
    pkgs = sorted(m.get("annotations", {}).get("io.meshweaver.package", "") for m in index["manifests"] if "io.meshweaver.package" in m.get("annotations", {}))
    expect(roles.count("sidecars") == 1 and pkgs == ["Pkg.A", "Pkg.B"], f"the index names the sidecar manifest and both bundles ({pkgs})")
    sidecar = next(m for m in index["manifests"] if m.get("annotations", {}).get("io.meshweaver.role") == "sidecars")
    cfg = run([oras, "manifest", "fetch-config", "--plain-http", f"{host}/plugins/plugins@{sidecar['digest']}"])
    config = json.loads(cfg.stdout)
    expect(config.get("sourceCommit") == "one0123456789abcdef" and config.get("architecture") == "linux-x64"
           and config.get("release") == "3.1.0" and config.get("identity") == IDENTITY,
           f"the config blob carries the publication's facts ({ {k: config.get(k) for k in ('sourceCommit', 'architecture', 'release')} })")
    expect(sorted(config.get("files", [])) == ["_complete", "architecture.txt", "modules/_index", "new-sidecar.txt",
                                                "platform-surface.json", "repository.txt", "source-commit.txt"],
           f"the config blob lists every sidecar, the unknown one included ({config.get('files')})")
    expect([b["package"] for b in config.get("bundles", [])] == ["Pkg.A", "Pkg.B"], "the config blob lists the bundles with digests")

    # Idempotence: the same bytes with the same arguments push the same index digest.
    r2 = push(pub, IDENTITY, "--tag-run", "4243", "--release", "3.1.0")
    d2 = next((t.split("=", 1)[1] for l in r2.stdout.splitlines() for t in l.split() if t.startswith("index=")), "")
    expect(r2.returncode == 0 and d2 == index_digest, f"a second push of the same bytes yields the same index digest ({d2 == index_digest})")

    # Refusals: nothing reaches the registry.
    unsealed = make_publication(work, "unsealed"); (unsealed / "_complete").unlink()
    r = push(unsealed, "sunsealed")
    expect(r.returncode == 1 and "carries no _complete" in r.stdout, "a directory without _complete is refused (exit 1)")
    expect(resolve("plugins/plugins:sunsealed") == "", "…and no tag was written")
    torn = make_publication(work, "torn"); (torn / "Pkg.B.zip").unlink()
    r = push(torn, "storn")
    expect(r.returncode == 1 and "Pkg.B.zip" in r.stdout and "absent" in r.stdout, "a _complete listing an absent bundle is refused (exit 1)")
    expect(resolve("plugins/plugins:storn") == "", "…and no tag was written")
    tornmod = make_publication(work, "tornmod"); (tornmod / "modules" / "Pkg.A.module.nupkg").unlink()
    r = push(tornmod, "stornmod")
    expect(r.returncode == 1 and "modules/_index lists" in r.stdout, "a modules/_index listing an absent module is refused (exit 1)")
    r = run(["bash", str(PUSH), "--registry", host, "--source", "Releases", "--identity", IDENTITY, "--dir", str(pub), "--plain-http"], env=env)
    expect(r.returncode == 1 and "reserved" in r.stdout, "the source name 'releases' is refused as reserved")

    # The fetch: byte-identical layout, one source unsealed, nothing written for it.
    root = work / "root-plain"
    fenv = dict(os.environ, BUNDLES_REGISTRY=host, BUNDLES_SOURCES="Plugins Education", BUNDLES_ROOT=str(root),
                BUNDLES_IDENTITY=IDENTITY, BUNDLES_PLAIN_HTTP="true", ORAS=oras)
    r = run(["sh", str(FETCH)], env=fenv)
    expect(r.returncode == 0, f"bundle-fetch exits 0 with one sealed and one unsealed source (stderr: {r.stderr.strip()[:300]})")
    expect("unsealed: no publication of 'Education'" in r.stdout, "…the unsealed source is named in the log")
    got = tree(root / IDENTITY / "Plugins") if (root / IDENTITY / "Plugins").is_dir() else {}
    want = tree(pub)
    expect(got == want and len(want) == 10, f"the materialised layout is byte-identical to the publication ({len(got)} of {len(want)} files)")
    expect(not (root / IDENTITY / "Education").exists(), "nothing was written for the unsealed source")
    expect(not (root / ".bundle-fetch").exists(), "the staging directory is gone")
    expect("materialised 'Plugins'" in r.stdout and f"index {index_digest}" in r.stdout, "…the log names the index digest it materialised")

    # An identity that was never published: exit 0, nothing at all under the root.
    root2 = work / "root-none"
    r = run(["sh", str(FETCH)], env=dict(fenv, BUNDLES_ROOT=str(root2), BUNDLES_IDENTITY="snever"))
    expect(r.returncode == 0 and not (root2 / "snever").exists(), "an identity with no publication exits 0 and writes no identity directory")

    # identityFile: the identity read at run time.
    idfile = work / "identity.txt"; idfile.write_text(f"\n  {IDENTITY}  \n")
    root3 = work / "root-idfile"
    r = run(["sh", str(FETCH)], env=dict({k: v for k, v in fenv.items() if k != "BUNDLES_IDENTITY"},
                                         BUNDLES_ROOT=str(root3), BUNDLES_IDENTITY_FILE=str(idfile), BUNDLES_SOURCES="Plugins"))
    expect(r.returncode == 0 and tree(root3 / IDENTITY / "Plugins") == want, "BUNDLES_IDENTITY_FILE is read at run time (first non-blank line, trimmed)")

    # A re-run over an existing shelf (the kubelet re-running the init container) replaces it.
    (root / IDENTITY / "Plugins" / "stale.txt").write_text("left over\n")
    r = run(["sh", str(FETCH)], env=dict(fenv, BUNDLES_SOURCES="Plugins"))
    expect(r.returncode == 0 and tree(root / IDENTITY / "Plugins") == want, "a re-run replaces the shelf wholesale (no stale file survives)")


# ---------------------------------------------------------------------------------------------
# Case B — authorization at the edge: distribution + docker_auth (the RENDERED config) + the stub.
# ---------------------------------------------------------------------------------------------
def case_authz(oras: str, work: Path, containers: Containers, reg_image: str, auth_image: str) -> None:
    print("\n== B. per-repository authorization: docker_auth with the rendered ACL, labels from the validator")
    # The chart's own render is the config under test.
    r = run(["helm", "template", "release", str(CHART), "--namespace", "check", "-f", str(CHART / "values.yaml"),
             "-f", str(REPO / "deploy/aks/values.aks.yaml"), "-f", str(CHART / "values.registry.example.yaml")])
    if r.returncode != 0:
        print(f"::error::helm template failed: {r.stderr}")
        sys.exit(1)
    docs = [d for d in yaml.safe_load_all(r.stdout) if d]
    cm = next(d for d in docs if d.get("kind") == "ConfigMap" and d["metadata"]["name"] == "memex-registry-auth-config")
    auth_dir = work / "auth"; auth_dir.mkdir()
    (auth_dir / "auth_config.yml").write_text(cm["data"]["auth_config.yml"])
    (auth_dir / "validate.sh").write_text(cm["data"]["validate.sh"])
    (auth_dir / "validate.sh").chmod(0o755)
    expect(cm["data"]["validate.sh"] == VALIDATE.read_text(), "the rendered validate.sh is deploy/helm/files/registry-validate.sh verbatim")
    dep = next(d for d in docs if d.get("kind") == "Deployment" and d["metadata"]["name"] == "memex-registry-auth-deployment")
    auth_container = dep["spec"]["template"]["spec"]["containers"][0]
    auth_args = auth_container.get("args") or []
    env_names = {e["name"] for e in auth_container.get("env", [])}
    expect(auth_args[-1:] == ["/config/auth_config.yml"], f"the auth Deployment's args end in the rendered config ({auth_args})")
    expect("MEMEX_VALIDATION_URL" in env_names, "the auth Deployment hands the validator MEMEX_VALIDATION_URL")
    acl = yaml.safe_load(cm["data"]["auth_config.yml"])["acl"]
    expect(any(e["match"].get("name") == "plugins/${labels:source}" for e in acl)
           and any(e["match"].get("name") == "plugins/${labels:package}" for e in acl),
           "the rendered ACL authorizes plugin repositories through the source/package labels")

    # Token-signing certificate for both containers.
    cert_dir = work / "token"; cert_dir.mkdir()
    r = run(["openssl", "req", "-x509", "-newkey", "rsa:2048", "-nodes", "-days", "2", "-subj", "/CN=memex-registry",
             "-keyout", str(cert_dir / "key.pem"), "-out", str(cert_dir / "cert.pem")])
    if r.returncode != 0:
        print(f"::error::openssl failed: {r.stderr}"); sys.exit(1)
    os.chmod(cert_dir / "key.pem", 0o644)

    stub_port = free_port()
    Stub.seen = []
    server = http.server.ThreadingHTTPServer(("0.0.0.0", stub_port), Stub)
    threading.Thread(target=server.serve_forever, daemon=True).start()

    auth_port = free_port()
    reg_port = free_port()
    pid = os.getpid()
    containers.start(f"mw-bundle-auth-{pid}", [
        "-p", f"127.0.0.1:{auth_port}:5001", "--add-host", "host.docker.internal:host-gateway",
        "-e", f"MEMEX_VALIDATION_URL=http://host.docker.internal:{stub_port}/api/instances/token",
        "-v", f"{auth_dir / 'auth_config.yml'}:/config/auth_config.yml:ro",
        "-v", f"{auth_dir / 'validate.sh'}:/etc/docker_auth/validate.sh:ro",
        "-v", f"{cert_dir}:/etc/registry/token:ro",
        auth_image, *auth_args])  # the DEPLOYED arguments, so the log flag is proved as shipped
    expect(wait_http(f"http://localhost:{auth_port}/", {200}), f"docker_auth answers on {auth_port}")
    reg_cfg = work / "registry.yml"
    reg_cfg.write_text(f"""version: 0.1
log: {{level: warn}}
storage:
  filesystem: {{rootdirectory: /var/lib/registry}}
  delete: {{enabled: true}}
http: {{addr: ":5000"}}
auth:
  token:
    realm: "http://localhost:{auth_port}/auth"
    service: "cr.example.test"
    issuer: "memex-registry"
    rootcertbundle: /etc/registry/token/cert.pem
""")
    containers.start(f"mw-bundle-reg-{pid}", [
        "-p", f"127.0.0.1:{reg_port}:5000", "-e", "OTEL_TRACES_EXPORTER=none",
        "-v", f"{reg_cfg}:/etc/distribution/config.yml:ro", "-v", f"{cert_dir}:/etc/registry/token:ro",
        reg_image, "serve", "/etc/distribution/config.yml"])
    host = f"localhost:{reg_port}"
    expect(wait_http(f"http://{host}/v2/", {401}), f"distribution on {host} challenges with 401 (token auth is on)")

    cfgs = {name: docker_config(work / f"docker-{name}.json", host, name, key) for name, key in KEYS.items()}
    cfgs["publisher"] = docker_config(work / "docker-publisher.json", host, "publisher", PUBLISHER_PASSWORD)
    cfgs["publisher-wrong"] = docker_config(work / "docker-publisher-wrong.json", host, "publisher", "not-the-password")

    def attempt(account: str, ref: str) -> str:
        """allowed | denied | notfound | error — read off the registry's answer, never a log."""
        args = [oras, "resolve", "--plain-http", f"{host}/{ref}"]
        if account != "anonymous":
            args[2:2] = ["--registry-config", str(cfgs[account])]
        rr = run(args)
        if rr.returncode == 0:
            return "allowed"
        err = rr.stderr.lower()
        if "not found" in err:
            return "notfound"
        if "unauthorized" in err or "denied" in err or "403" in err or "401" in err or "forbidden" in err:
            return "denied"
        return f"error:{rr.stderr.strip()[:160]}"

    # Publish as the publisher: the publication, a release identity, and one "image".
    pub = make_publication(work, "authz")
    env = dict(os.environ, ORAS=oras)
    r = run(["bash", str(PUSH), "--registry", host, "--source", "Plugins", "--identity", IDENTITY, "--dir", str(pub),
             "--tag-run", "7", "--release", "3.1.0", "--plain-http", "--registry-config", str(cfgs["publisher"])], env=env)
    expect(r.returncode == 0, f"the publisher account pushes the publication through token auth (stderr: {r.stderr.strip()[:300]})")
    pub2 = make_publication(work, "reins")
    r = run(["bash", str(PUSH), "--registry", host, "--source", "Reinsurance", "--identity", IDENTITY, "--dir", str(pub2),
             "--plain-http", "--registry-config", str(cfgs["publisher"])], env=env)
    expect(r.returncode == 0, "…and a second source (Reinsurance)")
    pub3 = make_publication(work, "edu")
    r = run(["bash", str(PUSH), "--registry", host, "--source", "Education", "--identity", IDENTITY, "--dir", str(pub3),
             "--plain-http", "--registry-config", str(cfgs["publisher"])], env=env)
    expect(r.returncode == 0, "…and a third source (Education) nobody in the matrix is licensed for")
    image_file = work / "layer.bin"; image_file.write_bytes(b"not really an image\n")
    r = run([oras, "push", "--plain-http", "--registry-config", str(cfgs["publisher"]), f"{host}/memex-portal-ai:t",
             "layer.bin:application/octet-stream"], cwd=work)
    expect(r.returncode == 0, "the publisher pushes an image repository (memex-portal-ai)")

    # The matrix. Every row is a fact about the RENDERED ACL and the validator's labels.
    matrix = [
        # account, repository:tag, expected
        ("full", f"plugins/plugins:{IDENTITY}", "allowed"),                 # plan-less Plugins/* → the index
        ("full", f"plugins/plugins/pkg.a:{IDENTITY}", "allowed"),
        ("full", f"plugins/reinsurance/pkg.a:{IDENTITY}", "denied"),        # licensed for UWDeepfield only
        ("full", f"plugins/reinsurance:{IDENTITY}", "denied"),              # no whole-source entry → no index
        ("full", f"plugins/education:{IDENTITY}", "denied"),
        ("full", f"plugins/education/pkg.a:{IDENTITY}", "denied"),
        ("full", "plugins/releases:3.1.0", "allowed"),
        ("full", "memex-portal-ai:t", "allowed"),
        ("pro", f"plugins/plugins:{IDENTITY}", "denied"),                   # @pro never licenses the index
        ("pro", f"plugins/plugins/pkg.a:{IDENTITY}", "allowed"),            # its source's bundle repositories
        ("pro", f"plugins/education/pkg.a:{IDENTITY}", "denied"),
        ("pro", "plugins/releases:3.1.0", "allowed"),
        ("pro", "memex-portal-ai:t", "allowed"),
        ("edu", f"plugins/plugins:{IDENTITY}", "denied"),
        ("edu", f"plugins/plugins/edu:{IDENTITY}", "notfound"),             # authorized (no such bundle pushed) — the ACL let it through to a 404
        ("edu", f"plugins/plugins/pkg.a:{IDENTITY}", "denied"),
        ("edu", "memex-portal-ai:t", "allowed"),
        ("anonymous", "memex-portal-ai:t", "denied"),
        ("anonymous", "plugins/releases:3.1.0", "denied"),
    ]
    for account, ref, want in matrix:
        got = attempt(account, ref)
        expect(got == want, f"{account:9} {ref:60} → {got} (expected {want})")
    # Logins the exchange refuses.
    for account in ("none",):
        got = attempt(account, "memex-portal-ai:t")
        expect(got == "denied", f"{account:9} (exchange answers 403) is refused: {got}")
    got = attempt("publisher-wrong", "memex-portal-ai:t")
    expect(got == "denied", f"publisher with a wrong password is refused: {got}")
    # Pushes: only the publisher.
    r = run([oras, "push", "--plain-http", "--registry-config", str(cfgs["full"]), f"{host}/plugins/plugins/pkg.c:{IDENTITY}",
             "layer.bin:application/octet-stream"], cwd=work)
    expect(r.returncode != 0, "an installation account cannot push")
    r = run([oras, "push", "--plain-http", "--registry-config", str(cfgs["full"]), f"{host}/memex-portal-ai:t2",
             "layer.bin:application/octet-stream"], cwd=work)
    expect(r.returncode != 0, "…not even an image repository")

    # What the validator presented to the exchange.
    posts = [s for s in Stub.seen if s["method"] == "POST"]
    gets = [s for s in Stub.seen if s["method"] == "GET"]
    expect(len(posts) > 0 and not gets, f"the validator POSTs to the exchange ({len(posts)} POSTs, {len(gets)} GETs)")
    expect(all(s["auth"].startswith("Bearer mwi_") for s in posts), "…every POST carries the key as a bearer")
    expect(all((s["content_type"] or "").startswith("application/json") for s in posts), "…with Content-Type application/json")
    expect(not any("not-the-password" in s["auth"] for s in posts), "the publisher's wrong password never reached the exchange (static account short-circuits)")
    expect(not any(PUBLISHER_PASSWORD in s["auth"] for s in posts), "…nor its right one")
    auth_log = containers.logs(f"mw-bundle-auth-{pid}")
    expect("bad return code" not in auth_log, "docker_auth reported no validator error (exit 3) during the matrix")
    expect("New token for" in auth_log, f"docker_auth's stderr names the tokens it minted (the chart's log flag works): {auth_log[-300:]!r}")
    expect("mwi_" not in auth_log,
           "no instance key appears in docker_auth's log (keys are presented, never logged)")

    # The init container's script through the edge: denied → exit 1, nothing written; allowed → identical.
    root = work / "root-authz"
    fenv = dict(os.environ, BUNDLES_REGISTRY=host, BUNDLES_SOURCES="Plugins", BUNDLES_ROOT=str(root), BUNDLES_IDENTITY=IDENTITY,
                BUNDLES_PLAIN_HTTP="true", ORAS=oras)
    r = run(["sh", str(FETCH)], env=dict(fenv, BUNDLES_REGISTRY_CONFIG=str(cfgs["edu"])))
    expect(r.returncode == 1 and not (root / IDENTITY).exists(), f"bundle-fetch with a licence that does not cover the source exits 1 and writes nothing (exit {r.returncode})")
    r = run(["sh", str(FETCH)], env=dict(fenv, BUNDLES_REGISTRY_CONFIG=str(cfgs["pro"])))
    expect(r.returncode == 1 and not (root / IDENTITY).exists(), f"bundle-fetch with a plan-scoped licence cannot take the publication whole (exit {r.returncode})")
    r = run(["sh", str(FETCH)], env=dict(fenv, BUNDLES_REGISTRY_CONFIG=str(cfgs["full"])))
    expect(r.returncode == 0 and tree(root / IDENTITY / "Plugins") == tree(pub), f"bundle-fetch with the pod's credential materialises the publication byte for byte (exit {r.returncode}, stderr: {r.stderr.strip()[:200]})")
    r = run(["sh", str(FETCH)], env=dict(fenv, BUNDLES_ROOT=str(work / "root-anon")))
    expect(r.returncode == 1, "bundle-fetch without a credential exits 1")
    server.shutdown()


def main() -> int:
    # A directory Docker can BIND-MOUNT from: on macOS, Colima shares $HOME but not the system
    # TMPDIR under /var/folders, where a mount silently lands on an empty path; on a runner,
    # RUNNER_TEMP is on the same filesystem as the workspace.
    base = Path(os.environ.get("RUNNER_TEMP") or (Path.home() / ".cache" / "meshweaver-tests"))
    base.mkdir(parents=True, exist_ok=True)
    work = Path(tempfile.mkdtemp(prefix="bundle-registry-", dir=base))
    containers = Containers()
    try:
        oras = preflight(work)
        r = run(["shellcheck", "-S", "warning", str(PUSH), str(FETCH), str(VALIDATE)])
        expect(r.returncode == 0, f"shellcheck -S warning is clean on the three scripts{(': ' + r.stdout.strip()[:400]) if r.returncode else ''}")
        reg_image, auth_image = image_pins()
        print(f"images under test: {reg_image}\n                   {auth_image}")
        case_plain(oras, work, containers, reg_image)
        case_authz(oras, work, containers, reg_image, auth_image)
    finally:
        containers.stop()
        shutil.rmtree(work, ignore_errors=True)
    print(f"\n{checks} assertion(s), {len(failures)} failure(s)")
    if checks < 40:
        print(f"::error::only {checks} assertions ran — the harness did not reach its cases; treating as FAILURE")
        return 1
    if failures:
        for f in failures:
            print(f"::error::{f}")
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
