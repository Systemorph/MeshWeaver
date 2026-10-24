#!/usr/bin/env python3
"""Ask a running .NET process to write a dump of ITSELF, over its diagnostics socket.

    diag-dump.py <pid> <dump-path> [--type heap|full|mini|triage] [--timeout <seconds>]

Why not `createdump <pid>` or `dotnet-dump collect`: createdump attaches with ptrace, and
under the Yama `ptrace_scope=1` that hosted Linux runners ship, a process may only trace its
own descendants — a CI step is not the test host's ancestor in that sense once `dotnet test`
sits between them. `dotnet-dump` would have to be installed at the moment of the wedge, over
the network. The diagnostics IPC protocol needs neither: the RUNTIME receives the request and
spawns its own createdump as its own child, which is what an unhandled-crash dump does too.
A host wedged on locks still answers it, because the diagnostics server runs on its own
native thread (that is the whole point of the server).

Protocol (dotnet/diagnostics, documentation/design-docs/ipc-protocol.md):
  header  = magic "DOTNET_IPC_V1\\0" (14 bytes) | uint16 size (header + payload)
            | uint8 command_set | uint8 command_id | uint16 reserved
  Dump command set = 0x01, GenerateCoreDump = 0x01,
  payload = string dump_name | uint32 dump_type | uint32 diagnostics
  string  = uint32 length in UTF-16 code units INCLUDING the terminator, then UTF-16LE
  reply   = header with command_set 0xFF; command_id 0x00 = OK, 0xFF = error (+ uint32 hresult)

`--timeout` is ONE deadline for the whole request — connect, send and every read of the reply —
never a per-operation one: a socket that accepts and then stalls, or trickles bytes, is cut when
the deadline passes. The caller hands it what is left of ITS budget (run-bounded-suite.sh shares
one dump budget over every process it asks), so a stalled runtime cannot push the job past its cap.

Exit codes: 0 dump written; 2 no diagnostics socket for that pid; 3 the runtime refused
(hresult printed); 4 the socket closed, answered something unreadable, or the deadline passed.

`--self-test` encodes a request and parses canned replies; it needs no .NET process.
"""
from __future__ import annotations

import glob
import os
import socket
import struct
import sys
import tempfile
import threading
import time

MAGIC = b"DOTNET_IPC_V1\x00"
DUMP_COMMAND_SET = 0x01
GENERATE_CORE_DUMP = 0x01
SERVER_COMMAND_SET = 0xFF
DUMP_TYPES = {"mini": 1, "heap": 2, "triage": 3, "full": 4}


def encode_string(value: str) -> bytes:
    chars = (value + "\x00").encode("utf-16-le")
    return struct.pack("<I", len(chars) // 2) + chars


def encode_request(dump_path: str, dump_type: int) -> bytes:
    payload = encode_string(dump_path) + struct.pack("<II", dump_type, 0)
    size = len(MAGIC) + 2 + 1 + 1 + 2 + len(payload)
    return MAGIC + struct.pack("<HBBH", size, DUMP_COMMAND_SET, GENERATE_CORE_DUMP, 0) + payload


def parse_reply(data: bytes) -> tuple[bool, int | None, str]:
    """(ok, hresult, why). A reply this script cannot read is never reported as success."""
    header = len(MAGIC) + 6
    if len(data) < header or not data.startswith(MAGIC):
        return False, None, f"unreadable reply ({len(data)} byte(s))"
    _, command_set, command_id, _ = struct.unpack_from("<HBBH", data, len(MAGIC))
    if command_set != SERVER_COMMAND_SET:
        return False, None, f"reply carries command set 0x{command_set:02X}, not the server's 0xFF"
    hresult = struct.unpack_from("<I", data, header)[0] if len(data) >= header + 4 else None
    if command_id == 0x00:
        return True, hresult, "ok"
    return False, hresult, f"runtime refused the dump (hresult 0x{(hresult or 0):08X})"


def socket_for(pid: int) -> str | None:
    roots = {os.environ.get("TMPDIR") or "", tempfile.gettempdir(), "/tmp"}
    for root in sorted(r for r in roots if r):
        hits = sorted(glob.glob(os.path.join(root, f"dotnet-diagnostic-{pid}-*-socket")))
        if hits:
            return hits[-1]
    return None


def request_dump(pid: int, dump_path: str, dump_type: int, timeout: float = 600.0) -> int:
    path = socket_for(pid)
    if path is None:
        print(f"diag-dump: pid {pid} has no diagnostics socket (not a .NET process, or "
              "DOTNET_EnableDiagnostics=0) — no dump", file=sys.stderr)
        return 2
    # A heap dump of a large host takes tens of seconds; the reply arrives when it is on disk.
    deadline = time.monotonic() + timeout
    data = b""
    try:
        with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as s:
            def remaining() -> float:
                left = deadline - time.monotonic()
                if left <= 0:
                    raise TimeoutError
                return left
            s.settimeout(remaining())
            s.connect(path)
            s.settimeout(remaining())
            s.sendall(encode_request(dump_path, dump_type))
            while len(data) < len(MAGIC) + 10:
                s.settimeout(remaining())
                chunk = s.recv(64)
                if not chunk:
                    break
                data += chunk
    except (TimeoutError, socket.timeout):
        print(f"diag-dump: pid {pid}: no reply within {timeout:.0f}s — the request was cut at its "
              "deadline; no dump is known to have been written", file=sys.stderr)
        return 4
    except OSError as e:
        print(f"diag-dump: pid {pid}: the diagnostics socket failed ({e}) — no dump", file=sys.stderr)
        return 4
    ok, hresult, why = parse_reply(data)
    if ok:
        print(f"diag-dump: pid {pid} wrote {dump_path}")
        return 0
    print(f"diag-dump: pid {pid}: {why}", file=sys.stderr)
    return 3 if hresult is not None else 4


def self_test() -> int:
    req = encode_request("/tmp/x.dmp", DUMP_TYPES["heap"])
    assert req.startswith(MAGIC)
    size, cs, cid, _ = struct.unpack_from("<HBBH", req, len(MAGIC))
    assert size == len(req), (size, len(req))
    assert (cs, cid) == (DUMP_COMMAND_SET, GENERATE_CORE_DUMP)
    n = struct.unpack_from("<I", req, 20)[0]
    assert n == len("/tmp/x.dmp") + 1, n
    assert req[24:24 + 2 * n].decode("utf-16-le") == "/tmp/x.dmp\x00"
    assert struct.unpack_from("<II", req, 24 + 2 * n) == (2, 0)
    ok_reply = MAGIC + struct.pack("<HBBH", 24, 0xFF, 0x00, 0) + struct.pack("<I", 0)
    err_reply = MAGIC + struct.pack("<HBBH", 24, 0xFF, 0xFF, 0) + struct.pack("<I", 0x80004005)
    assert parse_reply(ok_reply)[0] is True
    assert parse_reply(err_reply) == (False, 0x80004005, "runtime refused the dump (hresult 0x80004005)")
    assert parse_reply(b"")[0] is False                                   # closed socket
    assert parse_reply(b"garbage" * 4)[0] is False                         # not the protocol
    assert parse_reply(MAGIC + struct.pack("<HBBH", 24, 0x01, 0x00, 0))[0] is False  # not a reply
    # A socket that ACCEPTS and then never answers is cut at the ONE deadline, not after 600 s,
    # and one that trickles a byte at a time cannot renew it per read.
    for trickle in (False, True):
        with tempfile.TemporaryDirectory() as tmp:
            fake_pid = 999999
            sock_path = os.path.join(tmp, f"dotnet-diagnostic-{fake_pid}-1-socket")
            server = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
            server.bind(sock_path)
            server.listen(1)
            stop = threading.Event()

            def serve() -> None:
                conn, _ = server.accept()
                with conn:
                    while not stop.is_set():
                        if trickle:
                            try:
                                conn.sendall(b"D")
                            except OSError:
                                return
                        stop.wait(0.3)

            threading.Thread(target=serve, daemon=True).start()
            saved = os.environ.get("TMPDIR")
            os.environ["TMPDIR"] = tmp
            try:
                t0 = time.monotonic()
                rc = request_dump(fake_pid, os.path.join(tmp, "x.dmp"), DUMP_TYPES["mini"], timeout=1.5)
                took = time.monotonic() - t0
            finally:
                stop.set()
                server.close()
                if saved is None:
                    os.environ.pop("TMPDIR", None)
                else:
                    os.environ["TMPDIR"] = saved
            assert rc == 4, (trickle, rc)
            assert took < 5, f"a {'trickling' if trickle else 'stalled'} socket held the request {took:.1f}s past a 1.5s deadline"
    print("✓ diag-dump self-test: request framing (size, command, UTF-16 name, type) and every "
          "reply shape — OK, refused, closed, foreign, non-server — only OK reads as written; a stalled "
          "or trickling socket is cut at the one request deadline")
    return 0


def main(argv: list[str]) -> int:
    if argv[1:] == ["--self-test"]:
        return self_test()
    args = argv[1:]
    opts = {"--type": "heap", "--timeout": "600"}
    positional = []
    while args:
        a = args.pop(0)
        if a in opts and args:
            opts[a] = args.pop(0)
        elif a.startswith("--"):
            print(__doc__, file=sys.stderr)
            return 64
        else:
            positional.append(a)
    if len(positional) != 2:
        print(__doc__, file=sys.stderr)
        return 64
    kind = opts["--type"]
    if kind not in DUMP_TYPES:
        print(f"diag-dump: unknown dump type '{kind}' ({', '.join(DUMP_TYPES)})", file=sys.stderr)
        return 64
    try:
        timeout = float(opts["--timeout"])
    except ValueError:
        timeout = -1
    if timeout <= 0:
        print(f"diag-dump: --timeout must be a positive number of seconds, got '{opts['--timeout']}'", file=sys.stderr)
        return 64
    return request_dump(int(positional[0]), positional[1], DUMP_TYPES[kind], timeout)


if __name__ == "__main__":
    sys.exit(main(sys.argv))
