"""ci_cache_signature.py — only a TRUNK run can make a CI cache entry trusted.

The CI artifact store (`file:/ci-artifacts`) is a share every job on the self-hosted runners can
write, pull-request jobs included. A path, a namespace or a workflow `if:` therefore cannot keep a
pull request from planting an entry under a key the trunk later computes. A SIGNATURE can: the
Ed25519 private key lives in a GitHub ENVIRONMENT secret (`CI_CACHE_SIGNING_KEY`) whose deployment
policy admits the default branch only, so no pull-request job can obtain it, whatever its workflow
says. Readers verify against the PUBLIC key committed in the content repository
(`.github/ci-cache-signing.pub`). A pull request that edits that file changes only its OWN run's
trust; the trunk reads with the trunk's file.

What is signed is a CLAIM made by a trunk job about bytes it produced: "<recipe> <entry> <sha256>".
The signer never reads the cached bytes back from the share (a pull request could have replaced
them in between); it signs the digests the producing job handed over through a channel only that
run can write (job outputs / the run's own GitHub artifacts). The reader then checks the bytes it
fetched against the signed digest.

Every failure — no openssl, no key, a malformed signature, a key that is not Ed25519 — is
"not trusted", never an error: a cache is fail-open, an unverifiable entry is a miss.

Uses the `openssl` CLI (OpenSSL 3, Ed25519 `pkeyutl -rawin`), which every runner image carries;
no Python package is needed.
"""
from __future__ import annotations

import base64
import os
import subprocess
import tempfile
from pathlib import Path

PUBLIC_KEY_FILE = ".github/ci-cache-signing.pub"
SIGNING_KEY_ENV = "CI_CACHE_SIGNING_KEY"


def claim(recipe: str, entry: str, sha256: str) -> bytes:
    """The exact message a trunk job signs and a reader verifies."""
    return f"{recipe}\n{entry}\n{sha256}\n".encode("utf-8")


def _openssl(args: list[str]) -> bool:
    try:
        return subprocess.run(["openssl", *args], capture_output=True, timeout=60).returncode == 0
    except (OSError, subprocess.SubprocessError):
        return False


def sign(message: bytes, private_pem: str) -> "str | None":
    """Base64 Ed25519 signature of `message`, or None when it cannot be made."""
    if not private_pem.strip():
        return None
    with tempfile.TemporaryDirectory() as tmp:
        key, msg, sig = Path(tmp, "k.pem"), Path(tmp, "m"), Path(tmp, "s")
        key.write_text(private_pem if private_pem.endswith("\n") else private_pem + "\n")
        os.chmod(key, 0o600)
        msg.write_bytes(message)
        if not _openssl(["pkeyutl", "-sign", "-inkey", str(key), "-rawin", "-in", str(msg), "-out", str(sig)]):
            return None
        return base64.b64encode(sig.read_bytes()).decode("ascii")


def public_keys(text: str) -> list[str]:
    """Every PEM public key in `text` (a file may pin more than one, for rotation)."""
    keys, cur = [], []
    for line in text.splitlines():
        if line.startswith("-----BEGIN PUBLIC KEY-----"):
            cur = [line]
        elif cur:
            cur.append(line)
            if line.startswith("-----END PUBLIC KEY-----"):
                keys.append("\n".join(cur) + "\n")
                cur = []
    return keys


def verify(message: bytes, signature_b64: str, trusted: list[str]) -> bool:
    """True only when `signature_b64` is a valid Ed25519 signature of `message` by a trusted key."""
    if not trusted or not signature_b64:
        return False
    try:
        raw = base64.b64decode(signature_b64.strip(), validate=True)
    except (ValueError, TypeError):
        return False
    if len(raw) != 64:
        return False
    with tempfile.TemporaryDirectory() as tmp:
        msg, sig = Path(tmp, "m"), Path(tmp, "s")
        msg.write_bytes(message)
        sig.write_bytes(raw)
        for i, pem in enumerate(trusted):
            pub = Path(tmp, f"p{i}.pem")
            pub.write_text(pem)
            if _openssl(["pkeyutl", "-verify", "-pubin", "-inkey", str(pub), "-rawin",
                         "-in", str(msg), "-sigfile", str(sig)]):
                return True
    return False


def test_keypair(directory: Path) -> "tuple[str, str] | None":
    """(private PEM, public PEM) of a fresh Ed25519 key — for self-tests only."""
    priv, pub = directory / "test-signing.pem", directory / "test-signing.pub"
    if not _openssl(["genpkey", "-algorithm", "ed25519", "-out", str(priv)]):
        return None
    if not _openssl(["pkey", "-in", str(priv), "-pubout", "-out", str(pub)]):
        return None
    return priv.read_text(), pub.read_text()
