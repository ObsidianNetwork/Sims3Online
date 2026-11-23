#!/usr/bin/env python3
"""bridgectl — raw bridge-command REPL for the SimsOnline dedicated server (DEV-105).

Talks the loopback admin protocol (--bridgectl-listen, default 127.0.0.1:34029;
stackctl.py enables it automatically). Unlike simctl (public protocol, session
gates), this sends ARBITRARY wire commands straight to the memory bridge — the live
game-state inspector's front door, and a dev-only escape hatch.

Commands:
  bridgectl.py commands                      list every known wire command + usage
  bridgectl.py <NAME> [args...]              one-shot (e.g. DIAG_QUEUE 18031287978496800304)
  bridgectl.py --timeout 60 REQUEST_SNAPSHOT heavy command with a longer deadline
  bridgectl.py repl                          interactive loop (one command per line, @N timeout prefix)
  bridgectl.py hexenc "Terrain.GoHere"       hex-encode text for hex-typed args (keys, names)
  bridgectl.py hexdec 5465...                decode a hex field
  bridgectl.py capture <simId>              DEV-137: live interaction replay bundle capture + parse
  bridgectl.py capture <simId> --json       same, machine-readable fields + policy
  bridgectl.py state <simId> [bookId]       DEV-137: parse DIAG_INTERACTION_STATE
  bridgectl.py state-compare <simId> --book-id ID
                                             compare server/client posture, book, progress
  bridgectl.py reload                         DEV-122: hot-swap the handler assembly (Handlers/*.cs) — no TS3 restart
  bridgectl.py send-corpus <corpus> --live    card 56 gate 6 step (a): send the lines simlinkctl roundtrip sends
                                             (same corpus, same encoder) through this listener, in order
  bridgectl.py probe scratch/Foo.cs --json   parse the probe report into JSON (DIAG_WALK-style fields)
  bridgectl.py probe scratch/Foo.cs --watch  recompile + reupload + run on every file save (Ctrl+C to stop)
  bridgectl.py probe --rerun [--type T]      re-invoke the last-loaded probe with NO compile/upload (DIAG_RERUN)
  bridgectl.py probe scratch/Foo.cs --script loop.bridge   observe -> mutate -> re-observe loop

Structured probe output (DEV-108): a probe's Run() should return a DIAG_WALK-style report —
a ';'-delimited key=value header line, then optional '\n'-separated detail lines. --json parses
it; the default human view pretty-prints the fields. Errors / freeform strings pass through raw.

.bridge script verbs (one per line; '#' comments): RUN (upload+run on first, re-run after),
RERUN (DIAG_RERUN, needs the mod build), WAIT <ms>, @hex(text) token expansion, or any raw
wire command (e.g. SET_ROLE client / APPLY_MOTIVE <id> Hunger -90) sent verbatim.

Inspector quick reference:
  DIAG_QUEUE <simId>                full interaction queue + exit reasons (DEV-102 forensics)
  DIAG_RESOLVE <objectId>           which Sim-resolution paths succeed
  DIAG_WALK <objectId> - 2          reflection field dump (path '-' = root, depth 2)
  DIAG_WALK <objectId> $(hexenc mExitReason) 1     walk a specific field path
  DIAG_STATE                        mod session state (role, inWorld, autonomy watch)
  EVENTS_DROPPED                    answered by the controller: mod events it could not decode, by kind
  FRONT_DOOR_STATE                  front-door per-client stream and join counters (admin listener)

Argument tokens, on a one-shot verb, in the repl and in send-corpus: "-" is an empty wire part and "\\-" a part that
is a literal "-" (BridgeAdminListener.HandleRequestAsync). No other token is escaped, so a card 56 gate 6 line whose
argument is a null or an empty id list (`SET_SIM_SCOPE|<id>|-|-`) crosses as `SET_SIM_SCOPE \\- \\-`.
"""
import argparse
import json
import os
import re
import shutil
import socket
import subprocess
import sys
import time
import uuid
from pathlib import Path

from diag_state import DiagInteractionState, compare_interaction_states, parse_interaction_state_report
from local_endpoints import CLIENT_BRIDGECTL, SERVER_BRIDGECTL

DEFAULT_ENDPOINT = SERVER_BRIDGECTL
CONNECT_TIMEOUT_SECONDS = 2.0
# Hex chars per DIAG_BLOB chunk. 3600 hex = 1800 binary bytes; the wire line
# (DIAG_BLOB|corrId|blobId|i|n|hex) then sits well under the 4080-byte frame payload.
BLOB_CHUNK_HEX = 3600
BULK_UPLOAD_MAX = 524288
REPO_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
MAKE_PROBE = os.path.join(REPO_ROOT, "scripts", "make-probe.ps1")


def _powershell_argv(script: str, *script_args: str) -> list[str]:
    """Resolve pwsh (Linux/macOS) or powershell (Windows) for make-*.ps1 helpers."""
    exe = shutil.which("pwsh") or shutil.which("powershell")
    if not exe:
        sys.exit("probe compile requires pwsh or powershell on PATH")
    return [exe, "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
            "-File", script, *script_args]


def connect(endpoint: str, timeout: float) -> socket.socket:
    host, port = endpoint.rsplit(":", 1)
    try:
        sock = socket.create_connection((host, int(port)), timeout=CONNECT_TIMEOUT_SECONDS)
    except OSError as ex:
        sys.exit(f"connect to bridgectl listener {endpoint} failed: {ex}\n"
                 "(is the server up, and was it launched with --bridgectl-listen? stackctl.py enables it)")
    sock.settimeout(timeout)
    return sock


# Every command a script sends, one JSON line before it goes out (time, endpoint, calling script, the command's first
# 160 characters): crash_catch.py context lists the last ones before a crash. Upload chunks are left out.
# SIMSONLINE_BRIDGE_JOURNAL=0 turns it off; at 20 MB the file moves to .1 and starts again.
JOURNAL = Path(REPO_ROOT) / ".omo" / "run" / "bridge-journal.jsonl"
JOURNAL_MAX = 20 * 2 ** 20
JOURNAL_SKIP = ("DIAG_BLOB",)


def journal(sock: socket.socket, line: str, path: "Path | None" = None) -> None:
    """Append one command to the journal; never raises (a journal problem must not stop a command)."""
    if os.environ.get("SIMSONLINE_BRIDGE_JOURNAL") == "0" or line.lstrip("@0123456789 ").startswith(JOURNAL_SKIP):
        return
    path = path or JOURNAL
    try:
        host, port = sock.getpeername()[:2]
        entry = {"t": round(time.time(), 3), "endpoint": f"{host}:{port}", "caller": Path(sys.argv[0]).name,
                 "pid": os.getpid(), "cmd": line[:160]}
        if path.exists() and path.stat().st_size > JOURNAL_MAX:
            os.replace(path, path.with_name(path.name + ".1"))
        with path.open("a", encoding="utf-8") as f:
            f.write(json.dumps(entry) + "\n")
    except (OSError, ValueError):
        pass


def roundtrip(sock: socket.socket, line: str) -> dict:
    journal(sock, line)
    sock.sendall((line + "\n").encode("utf-8"))
    buf = b""
    while not buf.endswith(b"\n"):
        try:
            chunk = sock.recv(65536)
        except TimeoutError:
            sys.exit(f"timed out waiting for bridgectl response after {sock.gettimeout()}s")
        if not chunk:
            sys.exit("connection closed by server")
        buf += chunk
    return json.loads(buf.decode("utf-8-sig"))


def _split_kv(segment: str) -> "list[tuple[str, str]]":
    """Split a ';'-delimited key=value run into ordered (key, value) pairs.
    Tolerant: a bare token with no '=' becomes (token, ''); empty segments are skipped."""
    pairs = []
    for piece in segment.split(";"):
        piece = piece.strip()
        if not piece:
            continue
        key, sep, value = piece.partition("=")
        pairs.append((key.strip(), value.strip()) if sep else (piece, ""))
    return pairs


def parse_probe_report(report: str) -> dict:
    """Parse a DIAG_RUN/DIAG_RERUN report into structure.

    Wire shape (from the mod's LoadAndRun): 'loaded=<label>;asm=<name>;type=<type>;run=<probe-output>'
    where <probe-output> is the probe's own Run() string — by convention a DIAG_WALK-style
    ';'-delimited key=value header, optionally followed by '\\n' detail lines.

    Returns {envelope: {...}, fields: {...}, detail: [...], error: bool} on a clean parse,
    or {raw: report} when the shape is unrecognised (freeform string, '!'-error, etc.)."""
    if not isinstance(report, str) or "run=" not in report:
        return {"raw": report}
    head, _, run = report.partition("run=")
    envelope = dict(_split_kv(head))
    is_error = run.lstrip().startswith("!")
    lines = run.split("\n")
    fields = {} if is_error else dict(_split_kv(lines[0]))
    detail = [ln for ln in (lines[1:] if not is_error else lines) if ln.strip()]
    return {"envelope": envelope, "fields": fields, "detail": detail, "error": is_error,
            "run": run if is_error else None}


def _print_probe_report(report: str) -> None:
    """Human pretty-print: envelope on one line, parsed fields aligned, detail indented.
    Falls back to the raw report when it doesn't parse as a structured probe report."""
    parsed = parse_probe_report(report)
    if "raw" in parsed:
        print(report)
        return
    env = parsed["envelope"]
    print(f"loaded={env.get('label', env.get('loaded', '?'))}  type={env.get('type', '?')}"
          + (f"  asm={env['asm']}" if env.get("asm") else ""))
    if parsed["error"]:
        print(f"  ERROR {parsed['run']}")
        return
    width = max((len(k) for k in parsed["fields"]), default=0)
    for key, value in parsed["fields"].items():
        print(f"  {key.ljust(width)} = {value}")
    for line in parsed["detail"]:
        print(f"    {line}")


def render(response: dict, as_json: bool = False) -> int:
    if not response.get("ok"):
        if as_json:
            print(json.dumps({"ok": False, "error": response.get("error")}))
        else:
            print(f"ERROR: {response.get('error')}")
        return 1
    if "commands" in response:
        if as_json:
            print(json.dumps(response["commands"]))
        else:
            for entry in response["commands"]:
                print(f"  {entry}")
        return 0
    accepted = response.get("accepted")
    report = response.get("report")
    if as_json:
        out = {"accepted": accepted}
        if response.get("reason"):
            out["reason"] = response["reason"]
        if response.get("snapshot"):
            out["snapshot"] = response["snapshot"]
        if report:
            out["probe"] = parse_probe_report(report)
        print(json.dumps(out))
        return 0 if accepted else 1
    print(f"accepted={accepted}" + (f"  reason={response['reason']}" if response.get("reason") else ""))
    if response.get("snapshot"):
        print(f"snapshot: {response['snapshot']}")
    if report:
        _print_probe_report(report)
    return 0 if accepted else 1


def hexenc(text: str) -> str:
    return "".join(f"{b:02X}" for b in text.encode("utf-8"))


def derive_type_name(source_path: str) -> str | None:
    """Best-effort Namespace.Type from a probe .cs (first namespace + first class)."""
    try:
        text = open(source_path, encoding="utf-8", errors="replace").read()
    except OSError:
        return None
    text = re.sub(r"//.*", "", text)
    ns = re.search(r"\bnamespace\s+([\w.]+)", text)
    cls = re.search(r"\b(?:public\s+|internal\s+|sealed\s+|static\s+)*class\s+(\w+)", text)
    if not cls:
        return None
    return (ns.group(1) + "." + cls.group(1)) if ns else cls.group(1)


PROBE_CACHE = Path(__file__).resolve().parent.parent / ".omo" / "cache" / "probe-dlls.json"


def cached_probe(source: str) -> str:
    """The probe built from this exact source (and make-probe.ps1): stamped with their hash, compiled once, reused.
    The same source then gives the same bytes and assembly name, so the mod loads it into the game once and reruns it
    (LoadAndRun reuses a loaded probe; .NET 2 never unloads one). A random stamp per call loaded about 100 new
    assemblies a scenario suite into the server's game, which twice crashed in its interpreter on long runs
    (2026-10-14/15)."""
    import hashlib
    digest = hashlib.sha1(Path(source).read_bytes() + Path(MAKE_PROBE).read_bytes()).hexdigest()[:8]
    try:
        cache = json.loads(PROBE_CACHE.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        cache = {}
    dll = cache.get(digest)
    if dll and os.path.exists(dll):
        return dll
    dll = compile_probe(source, digest)
    cache[digest] = dll
    try:
        PROBE_CACHE.parent.mkdir(parents=True, exist_ok=True)
        PROBE_CACHE.write_text(json.dumps(cache, indent=0), encoding="utf-8")
    except OSError:
        pass
    return dll


def compile_probe(source: str, stamp: str) -> str:
    """Run make-probe.ps1; return the built DLL path (its final .dll stdout line)."""
    if not os.path.exists(source):
        sys.exit(f"probe source not found: {source}")
    import stack_claim
    refusal = stack_claim.quiet_refusal()  # another session's quiet window: SIMSONLINE_QUIET_OK=1 compiles anyway
    if refusal:
        sys.exit(refusal)
    # -NonInteractive: a parameter-binding failure must ERROR, never prompt on stdin (a dead
    # pipe under capture_output) and hang. timeout: a compile should be seconds — fail fast.
    try:
        proc = subprocess.run(
            _powershell_argv(MAKE_PROBE, source, "-Stamp", stamp),
            capture_output=True, text=True, timeout=120)
    except subprocess.TimeoutExpired:
        sys.exit("probe compile timed out after 120s (make-probe.ps1 hung — check it manually)")
    out = (proc.stdout or "").strip().splitlines()
    dll = next((ln.strip() for ln in reversed(out) if ln.strip().lower().endswith(".dll")), None)
    if proc.returncode != 0 or not dll or not os.path.exists(dll):
        sys.stderr.write(proc.stdout or "")
        sys.stderr.write(proc.stderr or "")
        sys.exit(f"probe compile failed (exit {proc.returncode})")
    return dll


def _with_timeout(line: str, timeout: "int | None") -> str:
    return f"@{timeout} {line}" if timeout is not None else line


def bulk_upload(sock: socket.socket, dll_path: str, label: str) -> "str | None":
    """DEV-151 v11 inbound bulk upload (single slot <=512 KiB). Returns blobId on success."""
    blob_id = uuid.uuid4().hex[:8]
    blob = open(dll_path, "rb").read()
    if len(blob) > BULK_UPLOAD_MAX:
        return None
    print(f"{label}: {os.path.basename(dll_path)}  {len(blob)} bytes  bulk upload  blobId={blob_id}")
    header = f"BULK_UPLOAD {blob_id} {len(blob)}\n".encode("utf-8")
    sock.sendall(header + blob)
    buf = b""
    while not buf.endswith(b"\n"):
        chunk = sock.recv(65536)
        if not chunk:
            print("  bulk upload failed: connection closed")
            return None
        buf += chunk
    resp = json.loads(buf.decode("utf-8-sig"))
    if not resp.get("ok") or resp.get("accepted") is False:
        print(f"  bulk upload failed: {resp.get('error') or resp.get('reason')}")
        return None
    if resp.get("report"):
        print(f"  {resp['report']}")
    return blob_id


def chunk_upload(sock: socket.socket, dll_path: str, label: str) -> "str | None":
    """Upload a DLL via v11 bulk when it fits, else DIAG_BLOB chunks."""
    blob = open(dll_path, "rb").read()
    if len(blob) <= BULK_UPLOAD_MAX:
        blob_id = bulk_upload(sock, dll_path, label)
        if blob_id is not None:
            return blob_id
        print(f"{label}: bulk upload failed — falling back to DIAG_BLOB chunks")
    blob_id = uuid.uuid4().hex[:8]
    hexed = blob.hex()
    chunks = [hexed[i:i + BLOB_CHUNK_HEX] for i in range(0, len(hexed), BLOB_CHUNK_HEX)] or [""]
    print(f"{label}: {os.path.basename(dll_path)}  {len(blob)} bytes  {len(chunks)} chunk(s)  blobId={blob_id}")
    for i, chunk in enumerate(chunks):
        resp = roundtrip(sock, f"DIAG_BLOB {blob_id} {i} {len(chunks)} {chunk}")
        if not resp.get("ok") or resp.get("accepted") is False:
            print(f"  chunk {i} upload failed: {resp.get('error') or resp.get('reason')}")
            return None
        if resp.get("report"):
            print(f"  [{i+1}/{len(chunks)}] {resp['report']}")
    return blob_id


class blob_lock:
    """One upload-and-use of the mod's blob slot at a time per game, across processes. The mod keeps one last-uploaded
    blob and DIAG_RUN runs that one, so two probes uploading at once ran each other's assembly ("type not found:
    SimActProbe [asm=CameraFocusSimProbe_...]", a scenario's step racing the motion watch's camera focus, 2026-10-15)."""

    def __init__(self, sock: socket.socket, wait_s: float = 180.0):
        try:
            port = sock.getpeername()[1]
        except OSError:
            port = 0
        self.path = Path(__file__).resolve().parent.parent / ".omo" / "run" / f"blob-{port}.lock"
        self.wait_s = wait_s
        self.fd = None

    def __enter__(self):
        import time
        self.path.parent.mkdir(parents=True, exist_ok=True)
        self.fd = os.open(str(self.path), os.O_RDWR | os.O_CREAT)
        deadline = time.monotonic() + self.wait_s
        while True:
            try:
                if os.name == "nt":
                    import msvcrt
                    msvcrt.locking(self.fd, msvcrt.LK_NBLCK, 1)
                else:
                    import fcntl
                    fcntl.flock(self.fd, fcntl.LOCK_EX | fcntl.LOCK_NB)
                return self
            except OSError:
                if time.monotonic() > deadline:
                    raise SystemExit(f"blob slot busy for {self.wait_s:.0f}s ({self.path})") from None
                time.sleep(0.1)

    def __exit__(self, *exc):
        try:
            if os.name == "nt":
                import msvcrt
                os.lseek(self.fd, 0, 0)
                msvcrt.locking(self.fd, msvcrt.LK_UNLCK, 1)
            else:
                import fcntl
                fcntl.flock(self.fd, fcntl.LOCK_UN)
        finally:
            os.close(self.fd)
        return False


def upload_and_run(sock: socket.socket, source: str, type_name: str,
                   timeout: "int | None", as_json: bool) -> int:
    """Compile -> chunk-upload -> DIAG_RUN. The original probe flow, factored for reuse
    by --watch and the .bridge interpreter."""
    dll = cached_probe(source)
    print(f"probe type={type_name}")
    with blob_lock(sock):
        if chunk_upload(sock, dll, "probe") is None:
            return 1
        print("-- DIAG_RUN --")
        return render(roundtrip(sock, _with_timeout(f"DIAG_RUN {hexenc(type_name)}", timeout)), as_json)


def compile_handlers() -> str:
    """DEV-122: build ONLY the swappable handler DLL via make-handlers.ps1 -DllOnly (no embed
    regeneration). Returns the built DLL path (its final .dll stdout line)."""
    make_handlers = os.path.join(REPO_ROOT, "scripts", "make-handlers.ps1")
    try:
        proc = subprocess.run(
            _powershell_argv(make_handlers, "-DllOnly"),
            capture_output=True, text=True, timeout=120)
    except subprocess.TimeoutExpired:
        sys.exit("handler compile timed out after 120s (make-handlers.ps1 hung — check it manually)")
    out = (proc.stdout or "").strip().splitlines()
    dll = next((ln.strip() for ln in reversed(out) if ln.strip().lower().endswith(".dll")), None)
    if proc.returncode != 0 or not dll or not os.path.exists(dll):
        sys.stderr.write(proc.stdout or "")
        sys.stderr.write(proc.stderr or "")
        sys.exit(f"handler compile failed (exit {proc.returncode})")
    return dll


def cmd_reload(tokens: list, endpoint: str, timeout: int | None, as_json: bool) -> int:
    """DEV-122 / T5d: hot-swap the swappable handler assembly in the LIVE game — edit Handlers/*.cs,
    run this, behavior changes with NO TS3 restart. Compiles the handler DLL (engine-only), uploads
    it via DIAG_BLOB, then sends RELOAD_HANDLERS. No --no-build cold-boot embed is touched."""
    p = argparse.ArgumentParser(prog="bridgectl reload", add_help=True)
    p.add_argument("--type", help="fully-qualified entry type (default: SimsOnline.Handlers.HandlerSet)")
    pargs = p.parse_args(tokens)

    dll = compile_handlers()
    sock = connect(endpoint, (timeout or 15) + 10)
    if chunk_upload(sock, dll, "handlers") is None:
        return 1
    req = "RELOAD_HANDLERS"
    if pargs.type:
        req += " " + hexenc(pargs.type)
    print("-- RELOAD_HANDLERS --")
    return render(roundtrip(sock, _with_timeout(req, timeout)), as_json)


def rerun(sock: socket.socket, type_name: "str | None", timeout: "int | None", as_json: bool) -> int:
    """Re-invoke the last-loaded probe with no compile/upload (mod DIAG_RERUN).
    With --type, re-resolve a specific already-uploaded type via DIAG_RUN instead."""
    if type_name:
        print("-- DIAG_RUN (re-resolve) --")
        return render(roundtrip(sock, _with_timeout(f"DIAG_RUN {hexenc(type_name)}", timeout)), as_json)
    print("-- DIAG_RERUN --")
    return render(roundtrip(sock, _with_timeout("DIAG_RERUN", timeout)), as_json)


_HEX_TOKEN = re.compile(r"@hex\(([^)]*)\)")


def _expand_hex_tokens(line: str) -> str:
    """Expand @hex(text) -> hex so .bridge lines can pass hex-typed args inline
    (e.g. APPLY_MOTIVE <id> @hex(Hunger) -90)."""
    return _HEX_TOKEN.sub(lambda m: hexenc(m.group(1)), line)


def run_bridge_script(sock: socket.socket, script_path: str, source: "str | None",
                      type_name: "str | None", timeout: "int | None", as_json: bool) -> int:
    """Interpret a .bridge mutation script: observe -> mutate -> re-observe.
    Verbs: '#' comment, RUN (upload+run first time, DIAG_RERUN after), RERUN, WAIT <ms>,
    and any raw wire command (verbatim, with @hex(...) expansion)."""
    import time
    try:
        lines = open(script_path, encoding="utf-8", errors="replace").read().splitlines()
    except OSError as ex:
        sys.exit(f"bridge script not found: {script_path} ({ex})")
    loaded = False
    for raw in lines:
        line = raw.strip()
        if not line or line.startswith("#"):
            continue
        verb = line.split()[0].upper()
        if verb == "RUN":
            if not loaded:
                if not source:
                    sys.exit("RUN needs a probe source — pass `probe <source.cs> --script ...`")
                rc = upload_and_run(sock, source, type_name, timeout, as_json)
                loaded = True
            else:
                rc = rerun(sock, None, timeout, as_json)
            if rc != 0:
                print(f"  (RUN returned {rc}; continuing)")
        elif verb == "RERUN":
            rerun(sock, None, timeout, as_json)
        elif verb == "WAIT":
            ms = int(line.split()[1]) if len(line.split()) > 1 else 500
            print(f"-- WAIT {ms}ms --")
            time.sleep(ms / 1000.0)
        else:
            print(f"-- {line} --")
            render(roundtrip(sock, _expand_hex_tokens(_with_timeout(line, timeout))), as_json)
    return 0


BUNDLE_HEX_TEXT_FIELDS = {
    "interactionKey", "interactionInstanceType", "interactionDefinitionType",
    "targetObjectType", "priorityLevel", "postureType", "policyHint", "parameters",
}


def decode_bundle_field(key: str, value: str) -> str:
    if key not in BUNDLE_HEX_TEXT_FIELDS or not value or len(value) % 2 != 0:
        return value
    try:
        return bytes.fromhex(value).decode("utf-8")
    except ValueError:
        return value


def parse_bundle_report(report: str) -> dict:
    fields = {k: decode_bundle_field(k, v) for k, v in _split_kv(report)}
    policy = fields.get("policyHint") or None
    if not policy and fields.get("interactionKey", "").endswith("+Definition"):
        key = fields["interactionKey"]
        if any(token in key for token in ("GoHome", "GoHere", "VisitCommunityLot", "BePushed")):
            policy = "ReplayAsRoute"
    return {"fields": fields, "policyHint": policy}


def cmd_capture(tokens: list, endpoint: str, timeout: int | None, as_json: bool) -> int:
    if not tokens:
        sys.exit("usage: bridgectl capture <simId>")
    sim_id = tokens[0]
    socket_timeout = (timeout or 15) + 10
    sock = connect(endpoint, socket_timeout)
    request = _with_timeout(f"DIAG_CAPTURE_INTERACTION {sim_id}", timeout)
    resp = roundtrip(sock, request)
    if not resp.get("ok") or resp.get("accepted") is False:
        return render(resp, as_json)
    report = resp.get("report") or ""
    parsed = parse_bundle_report(report)
    if as_json:
        print(json.dumps({"ok": True, "simId": sim_id, "report": report, **parsed}))
        return 0
    print(f"capture sim={sim_id}  policyHint={parsed.get('policyHint') or '(resolver)'}")
    width = max((len(k) for k in parsed["fields"]), default=0)
    for key, value in parsed["fields"].items():
        print(f"  {key.ljust(width)} = {value}")
    return 0


def _state_request(sim_id: str, book_id: str | None, timeout: int | None) -> str:
    request = f"DIAG_INTERACTION_STATE {sim_id}"
    if book_id:
        request += " " + hexenc(book_id)
    return _with_timeout(request, timeout)


def _fetch_interaction_state(endpoint: str, sim_id: str, book_id: str | None, timeout: int | None) -> tuple[dict, DiagInteractionState | None]:
    sock = connect(endpoint, (timeout or 15) + 10)
    resp = roundtrip(sock, _state_request(sim_id, book_id, timeout))
    if not resp.get("ok") or resp.get("accepted") is False:
        return resp, None
    return resp, parse_interaction_state_report(resp.get("report") or "")


def _print_interaction_state(prefix: str, state: DiagInteractionState) -> None:
    fields = state.to_dict()
    print(f"{prefix} role={fields.get('role', '?')} sim={fields.get('sim', '?')}")
    width = max((len(k) for k in fields), default=0)
    for key, value in fields.items():
        print(f"  {key.ljust(width)} = {value}")


def cmd_state(tokens: list, endpoint: str, timeout: int | None, as_json: bool) -> int:
    p = argparse.ArgumentParser(prog="bridgectl state", add_help=True)
    p.add_argument("sim_id")
    p.add_argument("book_id", nargs="?", help="plain book data id; encoded before DIAG_INTERACTION_STATE")
    pargs = p.parse_args(tokens)

    resp, state = _fetch_interaction_state(endpoint, pargs.sim_id, pargs.book_id, timeout)
    if state is None:
        return render(resp, as_json)
    if as_json:
        print(json.dumps({
            "ok": True,
            "accepted": resp.get("accepted"),
            "endpoint": endpoint,
            "simId": pargs.sim_id,
            "bookId": pargs.book_id,
            "state": state.to_dict(),
        }))
        return 0
    _print_interaction_state("state", state)
    return 0


def cmd_state_compare(tokens: list, endpoint: str, timeout: int | None, as_json: bool) -> int:
    p = argparse.ArgumentParser(prog="bridgectl state-compare", add_help=True)
    p.add_argument("sim_id")
    p.add_argument("--book-id", help="plain watched book data id; encoded for both endpoints")
    p.add_argument("--server-endpoint", default=endpoint)
    p.add_argument("--client-endpoint", default=CLIENT_BRIDGECTL)
    p.add_argument("--progress-tolerance", type=float, default=0.05)
    pargs = p.parse_args(tokens)

    server_resp, server_state = _fetch_interaction_state(pargs.server_endpoint, pargs.sim_id, pargs.book_id, timeout)
    if server_state is None:
        return render(server_resp, as_json)
    client_resp, client_state = _fetch_interaction_state(pargs.client_endpoint, pargs.sim_id, pargs.book_id, timeout)
    if client_state is None:
        return render(client_resp, as_json)

    comparison = compare_interaction_states(server_state, client_state, pargs.progress_tolerance)
    if as_json:
        print(json.dumps({
            "ok": comparison.ok,
            "simId": pargs.sim_id,
            "bookId": pargs.book_id,
            "serverEndpoint": pargs.server_endpoint,
            "clientEndpoint": pargs.client_endpoint,
            "server": server_state.to_dict(),
            "client": client_state.to_dict(),
            "comparison": comparison.to_dict(),
        }))
        return 0 if comparison.ok else 2

    print(f"state-compare sim={pargs.sim_id} ok={comparison.ok}")
    for check in comparison.checks:
        status = "PASS" if check.ok else "FAIL"
        print(f"  {status} {check.field}: server={check.server} client={check.client}")
    if comparison.progress_field:
        print(f"  progressDelta={comparison.progress_delta} tolerance={pargs.progress_tolerance}")
    if comparison.failures:
        print("  failures=" + ",".join(comparison.failures))
    return 0 if comparison.ok else 2


def cmd_probe(tokens: list, endpoint: str, timeout: int | None, as_json: bool) -> int:
    p = argparse.ArgumentParser(prog="bridgectl probe", add_help=True)
    p.add_argument("source", nargs="?", help="probe .cs file (public static string Run())")
    p.add_argument("--type", help="fully-qualified type with Run() (default: inferred from the source)")
    p.add_argument("--watch", action="store_true", help="recompile + reupload + run on every file save")
    p.add_argument("--rerun", action="store_true", help="re-invoke the last-loaded probe (no compile/upload)")
    p.add_argument("--script", help="run a .bridge mutation script (observe -> mutate -> re-observe)")
    pargs = p.parse_args(tokens)

    # --rerun: no source needed; re-invoke resident code.
    if pargs.rerun:
        sock = connect(endpoint, (timeout or 15) + 10)
        return rerun(sock, pargs.type, timeout, as_json)

    if not pargs.source:
        sys.exit("probe needs a source .cs (or use --rerun to re-invoke the last-loaded probe)")
    type_name = pargs.type or derive_type_name(pargs.source)
    if not type_name:
        sys.exit("could not infer the probe type name — pass --type Namespace.Type")

    sock = connect(endpoint, (timeout or 15) + 10)

    if pargs.script:
        return run_bridge_script(sock, pargs.script, pargs.source, type_name, timeout, as_json)

    if pargs.watch:
        import time
        print(f"watching {pargs.source} — save to recompile + run, Ctrl+C to stop")
        last_mtime = 0.0
        try:
            while True:
                try:
                    mtime = os.path.getmtime(pargs.source)
                except OSError:
                    time.sleep(0.3)
                    continue
                if mtime != last_mtime:
                    last_mtime = mtime
                    print(f"\n=== {time.strftime('%H:%M:%S')}  {os.path.basename(pargs.source)} ===")
                    try:
                        upload_and_run(sock, pargs.source, type_name, timeout, as_json)
                    except SystemExit as ex:  # compile error etc. — report, keep watching
                        print(f"  (skipped: {ex})")
                    except (ConnectionError, OSError):
                        print("  (connection lost — reconnecting)")
                        sock = connect(endpoint, (timeout or 15) + 10)
                time.sleep(0.3)
        except KeyboardInterrupt:
            print()
            return 0

    return upload_and_run(sock, pargs.source, type_name, timeout, as_json)


# Card 56 gate 6 step (a). The listener answers these names itself, so they never reach the mod.
CONTROLLER_LOCAL_NAMES = {"EVENTS_DROPPED", "PRESENTATION_SENT"}
WIRE_CORR = re.compile(r"^[0-9a-fA-F]{32}$")
# The listener's two argument tokens (BridgeAdminListener.HandleRequestAsync): "-" is an empty wire part, and the
# escape token below is a wire part that is a literal "-" — what card 56 gate 6's three literal arguments encode to
# (`DIALOG_ANSWER|<id>|0|-`, `SET_SIM_SCOPE|<id>|-|-`, `SET_AUTHORITY|<id>|observe|-`). A part that is itself the
# escape token cannot be sent, so admin_request refuses it.
LISTENER_EMPTY = "-"
LISTENER_DASH = r"\-"
SIMLINKCTL = os.path.join(REPO_ROOT, "rust", "simlink_bridge", "target", "debug",
                          "simlinkctl.exe" if os.name == "nt" else "simlinkctl")


def admin_request(line: str) -> str:
    """The listener request whose Raw command encodes back to `line` with a new correlation id: the listener splits
    on spaces, maps `-` to an empty part and the token `\\-` to a literal `-` part, upper-cases the name and joins
    NAME|<id>|args with `|` (BridgeAdminListener.HandleRequestAsync, BridgeLineCodec.EncodeRawCommand). ValueError
    for a line it cannot carry that way."""
    parts = line.split("|")
    if len(parts) < 2 or not WIRE_CORR.match(parts[1]):
        raise ValueError(f"no correlation id in the second field: {line[:80]}")
    name, args = parts[0], parts[2:]
    if (not name or not name.isascii() or name != name.upper() or name.startswith("@")
            or name in CONTROLLER_LOCAL_NAMES):
        raise ValueError(f"the listener cannot forward {name!r} to the mod unchanged")
    for arg in args:
        if arg == LISTENER_DASH or not arg.isascii() or any(c.isspace() for c in arg):
            raise ValueError(f"{name}: argument {arg[:40]!r} cannot cross the space-split listener protocol")
    return " ".join([name, *(LISTENER_EMPTY if a == "" else LISTENER_DASH if a == "-" else a for a in args)])


def corpus_requests(encoded: list[dict]) -> tuple[list[tuple[str, str, str]], list[str]]:
    """`(verb, wire line, listener request)` per sent row, and the verbs the Rust driver refuses (card 56 §E.3),
    which neither half sends. ValueError (from admin_request) before anything is sent."""
    sends, refused = [], []
    for row in encoded:
        if "refused" in row:
            refused.append(row["verb"])
        else:
            sends.append((row["verb"], row["line"], admin_request(row["line"])))
    return sends, refused


# The one verb the mod never answers (HandleDisconnect, ServerHostMain.cs:708): its listener timeout is expected.
NO_REPLY_VERBS = {"Disconnect"}


def unanswered(verb: str, response: dict) -> bool:
    """Whether a send-corpus row failed for want of an answer; DISCONNECT's missing reply (the listener's timeout,
    BridgeAdminListener) is by design, any other error of it is not."""
    expected = verb in NO_REPLY_VERBS and str(response.get("error", "")).startswith("timeout after ")
    return not response.get("ok") and not expected


def cmd_send_corpus(tokens: list, endpoint: str, timeout: int | None) -> int:
    ap = argparse.ArgumentParser(prog="bridgectl.py send-corpus",
                                 description="Card 56 gate 6 step (a): the C# half sends the Rust half's corpus lines.")
    ap.add_argument("corpus", help="the corpus file simlinkctl roundtrip --corpus takes")
    ap.add_argument("--live", action="store_true", help="required: every line goes to the live server mod")
    ap.add_argument("--simlinkctl", default=SIMLINKCTL, help="the built simlinkctl (default: the crate's debug build)")
    ap.add_argument("--out", help="JSONL log: each command, the listener request and its answer")
    a = ap.parse_args(tokens)
    if not a.live:
        print("send-corpus: refusing to run without --live: it sends every corpus line to the live server mod "
              "(card 56 gate 6 step a; only under the stack claim)", file=sys.stderr)
        return 2
    enc = subprocess.run([a.simlinkctl, "encode", "--corpus", a.corpus], capture_output=True, text=True,
                         encoding="utf-8")
    if enc.returncode != 0:
        print(f"send-corpus: simlinkctl encode failed: {enc.stderr.strip()}", file=sys.stderr)
        return 2
    try:
        sends, refused = corpus_requests([json.loads(l) for l in enc.stdout.splitlines() if l.strip()])
    except ValueError as ex:
        print(f"send-corpus: {ex}; nothing was sent", file=sys.stderr)
        return 2
    seconds = timeout or 15
    sock = connect(endpoint, seconds + 10)
    log = open(a.out, "a", encoding="utf-8") if a.out else None
    failed = 0
    try:
        for verb, line, request in sends:
            response = roundtrip(sock, f"@{seconds} {request}")
            failed += unanswered(verb, response)
            entry = {"verb": verb, "line": line, "request": request, "response": response}
            print(json.dumps(entry))
            if log:
                log.write(json.dumps(entry) + "\n")
    finally:
        if log:
            log.close()
    print(f"send-corpus: {len(sends)} sent, {failed} without an answer, {len(refused)} refused by the Rust driver "
          f"and not sent: {', '.join(refused) or '-'}", file=sys.stderr)
    return 1 if failed else 0


def main() -> int:
    parser = argparse.ArgumentParser(
        description=__doc__,
        formatter_class=argparse.RawDescriptionHelpFormatter,
    )
    parser.add_argument("--endpoint", default=DEFAULT_ENDPOINT)
    parser.add_argument("--timeout", type=int, default=None, help="command deadline seconds (server default 15)")
    parser.add_argument("--json", action="store_true", dest="as_json",
                        help="emit machine-readable JSON (probe reports parsed into fields)")
    parser.add_argument("tokens", nargs=argparse.REMAINDER)
    args = parser.parse_args()
    # REMAINDER captures everything after the verb, so --json placed AFTER the verb
    # (bridgectl probe Foo.cs --json) lands in tokens. Honor it in any position by
    # scanning + stripping, so every verb (probe, raw command, repl) sees a clean token list.
    as_json = args.as_json or ("--json" in args.tokens)
    tokens = [t for t in args.tokens if t != "--json"]
    args.tokens = tokens

    if not args.tokens or args.tokens[0] in ("-h", "--help", "help"):
        print(__doc__)
        return 0

    verb = args.tokens[0]
    if verb == "hexenc":
        print(hexenc(" ".join(args.tokens[1:])))
        return 0
    if verb == "hexdec":
        print(bytes.fromhex(args.tokens[1]).decode("utf-8", errors="replace"))
        return 0
    if verb == "capture":
        return cmd_capture(args.tokens[1:], args.endpoint, args.timeout, as_json)
    if verb == "state":
        return cmd_state(args.tokens[1:], args.endpoint, args.timeout, as_json)
    if verb == "state-compare":
        return cmd_state_compare(args.tokens[1:], args.endpoint, args.timeout, as_json)
    if verb == "probe":
        return cmd_probe(args.tokens[1:], args.endpoint, args.timeout, as_json)
    if verb == "reload":
        return cmd_reload(args.tokens[1:], args.endpoint, args.timeout, as_json)
    if verb == "send-corpus":
        return cmd_send_corpus(args.tokens[1:], args.endpoint, args.timeout)

    # Socket timeout must outlive the server-side command deadline.
    socket_timeout = (args.timeout or 15) + 10
    sock = connect(args.endpoint, socket_timeout)

    if verb == "repl":
        print("bridgectl repl — 'commands' for inventory, '@N NAME args' for timeout override, Ctrl+C/quit to exit")
        while True:
            try:
                line = input("bridge> ").strip()
            except (EOFError, KeyboardInterrupt):
                print()
                return 0
            if not line:
                continue
            if line in ("quit", "exit"):
                return 0
            render(roundtrip(sock, line), as_json)
        return 0

    request = " ".join(args.tokens)
    if args.timeout is not None:
        request = f"@{args.timeout} {request}"
    return render(roundtrip(sock, request), as_json)


if __name__ == "__main__":
    sys.exit(main())
