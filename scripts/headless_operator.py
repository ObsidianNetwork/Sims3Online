"""Turnkey operator lifecycle tool for the Rust headless server (`simlink_headless_server`).

Preflights fork readiness, Sidecar (`SimLink.asi`), host mod freshness, save
availability (`.sims3` and extensionless folders), sync-policy checks,
and `SECURITY.md` listener posture BEFORE stopping any running controller or
launching TS3. On `start`, cold-boots native TS3 via the internal C# bootstrap
controller on loopback (`--bootstrap-endpoint`), waits for `phase=ready`,
disables C# ring push (`SACS_RECORDER push-off`), stops only the C#
controller while keeping TS3W warm, attaches `simlink_headless_server --serve`
to that PID, verifies public v1 `HELLO`/`WELCOME` + loopback `FRONT_DOOR_STATE`,
and prints the exact tester-kit `-Wire line` join command.
"""
from __future__ import annotations

import argparse
import contextlib
import dataclasses
import hashlib
import io
import ipaddress
import json
import os
import socket
import subprocess
import sys
import time
from collections.abc import Callable, Mapping, Sequence
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

SCRIPTS_DIR = Path(__file__).resolve().parent
REPO = SCRIPTS_DIR.parent
if str(SCRIPTS_DIR) not in sys.path:
    sys.path.insert(0, str(SCRIPTS_DIR))

import fork_census  # noqa: E402
import sidecar  # noqa: E402
import stack_claim  # noqa: E402
import stackctl  # noqa: E402
import ts3_paths  # noqa: E402

DEFAULT_PUBLIC_ENDPOINT = "127.0.0.1:34019"
DEFAULT_ADMIN_ENDPOINT = "127.0.0.1:34029"
DEFAULT_BOOTSTRAP_ENDPOINT = "127.0.0.1:34119"
DEFAULT_CONNECTIONS = 32
DEFAULT_SAVE = stackctl.DEFAULT_SAVE
DEFAULT_HOST_TIMEOUT = 240
DEFAULT_READY_TIMEOUT = 60
MAX_LINE_BYTES = 4_194_304

RUN_DIR = Path(stackctl.RUN_DIR)
OPERATOR_STATE_FILE = RUN_DIR / "headless-operator.json"
SERVER_STATE_FILE = Path(stackctl.SERVER_STATE_FILE)
SERVER_LOG = Path(stackctl.SERVER_LOG)
BOOTSTRAP_LOG = RUN_DIR / "headless-bootstrap.log"
SERVER_TS3_PID_FILE = Path(stackctl.SERVER_TS3_PID_FILE)

EXPOSURE_WARNING_TEMPLATE = (
    "WARNING: the public client listener accepts connections from other machines ({addr}). "
    "The protocol has no authentication: anyone who can reach this port can join and command "
    "Sims. Use a private network (see SECURITY.md) and never forward this port on a router."
)


class OperatorError(RuntimeError):
    """Structured operator preflight or runtime failure."""

    def __init__(self, code: str, message: str) -> None:
        super().__init__(message)
        self.code = code
        self.message = message


@dataclasses.dataclass(frozen=True)
class Endpoint:
    """Validated `<host>:<port>` listener endpoint."""

    raw: str
    canonical: str
    host_display: str
    ip: ipaddress.IPv4Address | ipaddress.IPv6Address
    port: int

    @property
    def is_loopback(self) -> bool:
        return self.ip.is_loopback

    @property
    def is_unspecified(self) -> bool:
        return self.ip.is_unspecified

    @property
    def connect_host(self) -> str:
        """Loopback substitute when binding `0.0.0.0` or `::` and probing locally."""
        if not self.is_unspecified:
            return str(self.ip)
        return "::1" if self.ip.version == 6 else "127.0.0.1"


def parse_endpoint(raw: str) -> Endpoint:
    """Parse `<host>:<port>` using the same IP/localhost rules as `bind.rs`."""
    value = raw.strip()
    if not value:
        raise OperatorError("invalid_endpoint", "Listener endpoint is empty.")
    if ":" not in value:
        raise OperatorError("invalid_endpoint", f"Listener endpoint '{value}' is not in <host>:<port> form.")
    host, port_text = value.rsplit(":", 1)
    if not host or not port_text:
        raise OperatorError("invalid_endpoint", f"Listener endpoint '{value}' is not in <host>:<port> form.")
    try:
        port = int(port_text)
    except ValueError as exc:
        raise OperatorError("invalid_port", f"Listener endpoint port '{port_text}' is not a valid TCP port.") from exc
    if not 1 <= port <= 65535:
        raise OperatorError("invalid_port", f"Listener endpoint port '{port_text}' must be between 1 and 65535.")

    normalized_host = "127.0.0.1" if host == "localhost" else host
    if ":" in normalized_host:
        if not (normalized_host.startswith("[") and normalized_host.endswith("]")):
            raise OperatorError(
                "invalid_endpoint",
                f"IPv6 endpoint '{value}' must bracket the address as [<ipv6>]:<port>.",
            )
        normalized_host = normalized_host[1:-1]
    try:
        ip = ipaddress.ip_address(normalized_host)
    except ValueError as exc:
        raise OperatorError(
            "host_not_ip",
            f"Listener host '{host}' is not an IP address. Use 127.0.0.1, a Tailscale/LAN IP, or 0.0.0.0.",
        ) from exc

    canonical = f"[{ip}]:{port}" if ip.version == 6 else f"{ip}:{port}"
    return Endpoint(raw=value, canonical=canonical, host_display=str(ip), ip=ip, port=port)


def validate_public_endpoint(raw: str, *, allow_public_exposure: bool) -> tuple[Endpoint, list[str]]:
    """Enforce `SECURITY.md` public-exposure confirmation for non-loopback `--public` binds."""
    ep = parse_endpoint(raw)
    warnings: list[str] = []
    if not ep.is_loopback:
        warnings.append(EXPOSURE_WARNING_TEMPLATE.format(addr=ep.canonical))
        if ep.is_unspecified:
            warnings.append(
                "RECOMMENDATION: Prefer binding your specific Tailscale (100.x.y.z) or LAN IP "
                f"instead of wildcard {ep.host_display} so untrusted interfaces stay closed."
            )
        if not allow_public_exposure:
            raise OperatorError(
                "public_exposure_unconfirmed",
                f"Refusing non-loopback --public bind {ep.canonical} without "
                "--i-understand-public-exposure (see SECURITY.md).",
            )
    return ep, warnings


def validate_admin_endpoint(raw: str, *, role: str = "Admin") -> Endpoint:
    """Enforce `SECURITY.md` IPv4 loopback-only binding on admin/bootstrap endpoints."""
    ep = parse_endpoint(raw)
    if not ep.is_loopback or ep.ip.version != 4:
        raise OperatorError(
            "admin_not_loopback",
            f"{role} listener endpoint '{ep.canonical}' must use IPv4 loopback (e.g. 127.0.0.1:34029); "
            "BridgeAdminListener does not support IPv6.",
        )
    return ep


def sha256_file(path: Path) -> str:
    """Return lowercase hex SHA-256 of `path`."""
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(65536), b""):
            digest.update(chunk)
    return digest.hexdigest()


def resolve_front_door_binary(
    explicit: str | None = None,
    *,
    env: Mapping[str, str] | None = None,
    repo: Path = REPO,
    is_windows: bool = os.name == "nt",
) -> Path:
    """Locate `simlink_headless_server` using explicit flag, env, Cargo target, or release layout."""
    environ = env if env is not None else os.environ
    executable = "simlink_headless_server.exe" if is_windows else "simlink_headless_server"
    override = explicit or environ.get("SIMSONLINE_FRONT_DOOR_BIN")

    candidates: list[Path] = []
    if override:
        path = Path(override)
        candidates.append(path if path.is_absolute() else repo / path)
    else:
        raw_target = environ.get("CARGO_TARGET_DIR")
        target_dir = Path(raw_target) if raw_target else repo / "rust" / "simlink_headless_server" / "target"
        if not target_dir.is_absolute():
            target_dir = repo / target_dir
        for profile in ("release", "debug"):
            candidates.append(target_dir / profile / executable)
        if is_windows:
            candidates.append(repo / "dist" / "simlink_headless_server-win-x64" / executable)
        candidates.append(SCRIPTS_DIR / executable)

    for candidate in candidates:
        if candidate.is_file():
            return candidate.resolve()

    manifest = repo / "rust" / "simlink_headless_server" / "Cargo.toml"
    raise OperatorError(
        "front_door_binary_missing",
        "simlink_headless_server binary not found (checked: "
        + ", ".join(str(p) for p in candidates)
        + f'). Build with: cargo build --manifest-path "{manifest}" --release --bin simlink_headless_server',
    )


def resolve_ts3_binary(explicit: str | None = None, *, env: Mapping[str, str] | None = None) -> Path:
    """Resolve `TS3W.exe` from `--ts3-path`, `SIMSONLINE_TS3_PATH`, or installed locations."""
    environ = env if env is not None else os.environ
    override = explicit or environ.get("SIMSONLINE_TS3_PATH")
    if override:
        candidate = Path(override)
        if candidate.is_file():
            return candidate
        raise OperatorError("ts3_not_found", f"TS3W.exe not found at '{candidate}'.")

    found = ts3_paths.find_ts3w()
    if found is not None and found.is_file():
        return found
    default = Path(stackctl.DEFAULT_TS3_PATH)
    if default.is_file():
        return default
    raise OperatorError(
        "ts3_not_found",
        "TS3W.exe not found; pass --ts3-path PATH or set SIMSONLINE_TS3_PATH.",
    )


def find_save_folder(save_name: str, saves_dir: Path) -> Path | None:
    """Locate a save folder matching `save_name` across both `.sims3` and extensionless forms."""
    cleaned = save_name.strip()
    if not cleaned or not saves_dir.is_dir():
        return None
    candidates = [cleaned]
    if cleaned.lower().endswith(".sims3"):
        candidates.append(cleaned[:-6])
    else:
        candidates.append(f"{cleaned}.sims3")
    for name in candidates:
        path = saves_dir / name
        if path.is_dir():
            return path
    return None


def probe_bootstrap_bind(bootstrap_ep: Endpoint) -> None:
    """Verify the C# controller's IPv4 bootstrap TCP port is unbound before launching TS3."""
    sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    try:
        if os.name == "nt" and hasattr(socket, "SO_EXCLUSIVEADDRUSE"):
            sock.setsockopt(socket.SOL_SOCKET, socket.SO_EXCLUSIVEADDRUSE, 1)
        sock.bind((bootstrap_ep.host_display, bootstrap_ep.port))
    except OSError as exc:
        raise OperatorError(
            "bootstrap_port_busy",
            f"Bootstrap endpoint '{bootstrap_ep.canonical}' is unavailable: {exc}",
        ) from exc
    finally:
        sock.close()


def probe_binary_bind(binary: Path, public_ep: str, admin_ep: str, timeout: float = 10.0) -> None:
    """Run `simlink_headless_server --bind-only` to verify listener binding without touching game state."""
    cmd = [str(binary), "--bind-only", "--public", public_ep, "--admin", admin_ep, "--enable-admin"]
    try:
        proc = subprocess.run(
            cmd,
            capture_output=True,
            text=True,
            encoding="utf-8",
            errors="replace",
            stdin=subprocess.DEVNULL,
            timeout=timeout,
            check=False,
        )
    except (OSError, subprocess.TimeoutExpired) as exc:
        raise OperatorError("bind_probe_failed", f"Bind probe could not run '{binary}': {exc}") from exc
    if proc.returncode != 0:
        detail = (proc.stderr or proc.stdout).strip()
        raise OperatorError("bind_probe_failed", f"Bind probe failed (exit {proc.returncode}): {detail}")


def check_sync_policy(binary: Path, policy_path: Path, timeout: float = 10.0) -> str:
    """Validate `--sync-policy FILE` via `simlink_headless_server --check-sync-policy`."""
    if not policy_path.is_file():
        raise OperatorError("sync_policy_missing", f"Sync policy file not found: '{policy_path}'.")
    cmd = [str(binary), "--check-sync-policy", str(policy_path)]
    try:
        proc = subprocess.run(
            cmd,
            capture_output=True,
            text=True,
            encoding="utf-8",
            errors="replace",
            stdin=subprocess.DEVNULL,
            timeout=timeout,
            check=False,
        )
    except (OSError, subprocess.TimeoutExpired) as exc:
        raise OperatorError("sync_policy_invalid", f"Sync policy check failed: {exc}") from exc
    if proc.returncode != 0:
        detail = (proc.stderr or proc.stdout).strip()
        raise OperatorError("sync_policy_invalid", f"Sync policy rejected: {detail}")
    try:
        payload = json.loads(proc.stdout.strip().splitlines()[-1])
        return str(payload.get("sha256") or "")
    except (ValueError, IndexError):
        return ""


def probe_public_hello(endpoint: Endpoint, timeout: float = 2.0) -> tuple[bool, str | None]:
    """Send v1 `HELLO` to the public endpoint and verify a `WELCOME|1|<u32>|...` reply."""
    try:
        with socket.create_connection((endpoint.connect_host, endpoint.port), timeout=timeout) as sock:
            sock.settimeout(timeout)
            sock.sendall(b"HELLO|1|ring=1,wfs=1,role=client\n")
            with sock.makefile("rb") as reader:
                raw = reader.readline(MAX_LINE_BYTES + 2)
    except OSError as exc:
        return False, str(exc)

    if not raw.endswith(b"\n") or len(raw.rstrip(b"\r\n")) > MAX_LINE_BYTES:
        return False, "truncated or oversized WELCOME frame"
    line = raw.decode("utf-8", errors="replace").rstrip("\r\n")
    fields = line.split("|", 3)
    if len(fields) == 4 and fields[:2] == ["WELCOME", "1"] and fields[2].isdigit():
        if 0 <= int(fields[2]) <= 0xFFFFFFFF:
            return True, line
    return False, f"unexpected reply: {line}"


def admin_request(admin_ep: Endpoint, command: str, timeout: float = 5.0) -> dict[str, Any]:
    """Send one raw newline command to the loopback admin listener and parse its single-line JSON reply."""
    try:
        with socket.create_connection((admin_ep.connect_host, admin_ep.port), timeout=timeout) as sock:
            sock.settimeout(timeout)
            sock.sendall(f"{command.rstrip()}\n".encode("utf-8"))
            with sock.makefile("rb") as reader:
                raw = reader.readline(MAX_LINE_BYTES + 2)
    except OSError as exc:
        raise OperatorError("admin_unreachable", f"Admin request '{command}' failed on {admin_ep.canonical}: {exc}") from exc

    if not raw:
        raise OperatorError("admin_empty_reply", f"Admin listener closed without replying to '{command}'.")
    text = raw.decode("utf-8", errors="replace").strip()
    try:
        parsed = json.loads(text)
    except ValueError as exc:
        raise OperatorError("admin_bad_json", f"Invalid JSON from admin for '{command}': {text}") from exc
    if not isinstance(parsed, dict):
        raise OperatorError("admin_bad_json", f"Unexpected admin payload type for '{command}': {text}")
    return parsed


def format_tester_join_command(
    public_ep: Endpoint,
    *,
    player_id: int = 2,
    claim_sim: str | int | None = None,
    public_host_override: str | None = None,
) -> str:
    """Render the exact tester-kit `join.ps1` command including mandatory `-Wire line`."""
    host = public_host_override or ("<YOUR-TAILSCALE-OR-LAN-IP>" if public_ep.is_unspecified else public_ep.host_display)
    server_arg = f"[{host}]:{public_ep.port}" if ":" in host else f"{host}:{public_ep.port}"
    cmd = f'.\\join.ps1 -Server "{server_arg}" -Wire line -Player {player_id}'
    if claim_sim is not None and str(claim_sim).strip():
        cmd += f' -Sim "{str(claim_sim).strip()}"'
    return cmd


def _saves_dir_default() -> Path | None:
    profile = fork_census.documents_profile()
    return (profile / "Saves") if profile is not None else None


def _check_fork_state(allow_stock: bool) -> dict[str, Any]:
    result = fork_census.census(fork_census.receipt_checkout())
    try:
        fork_census.guard(result, allow_stock=allow_stock)
    except SystemExit as exc:
        raise OperatorError("fork_census_failed", f"Fork census refused launch: {fork_census.summary(result)} ({exc})") from exc
    return {"state": result.get("state"), "summary": fork_census.summary(result)}


def _check_sidecar_state(ts3_path: Path) -> dict[str, Any]:
    installed = ts3_path.parent / "SimLink.asi"
    if not installed.is_file():
        raise OperatorError(
            "sidecar_missing",
            f"SimLink.asi is not installed next to TS3W.exe ('{installed}'). Run `python scripts/sidecar.py build` and install.",
        )
    built = sidecar.BUILT
    installed_sha = sidecar.sha(installed)
    built_sha = sidecar.sha(built) if built.is_file() else None
    if built_sha and built_sha != installed_sha:
        raise OperatorError(
            "sidecar_mismatch",
            f"Installed SimLink.asi ({sidecar.describe(installed)}) differs from built '{built}' ({sidecar.describe(built)}).",
        )
    return {
        "installedPath": str(installed),
        "installed": sidecar.describe(installed),
        "builtMatched": bool(built_sha and built_sha == installed_sha),
    }


def _check_mod_state(*, skip_mod_sync: bool) -> dict[str, Any]:
    if skip_mod_sync:
        return {"skipped": True}
    if stackctl.installed_slot():
        return {"slot": stackctl.installed_slot(), "stale": False}
    if stackctl.test_mod_needs_sync():
        raise OperatorError(
            "mod_stale",
            "Host mod package differs from workspace sources or has no manifest. "
            "Run `pwsh -NoProfile -File scripts/make-mod.ps1` (with games down) or pass --skip-mod-sync.",
        )
    return {"stale": False}


def run_preflight(
    *,
    public_raw: str = DEFAULT_PUBLIC_ENDPOINT,
    admin_raw: str = DEFAULT_ADMIN_ENDPOINT,
    bootstrap_raw: str = DEFAULT_BOOTSTRAP_ENDPOINT,
    save_name: str = DEFAULT_SAVE,
    connections: int = DEFAULT_CONNECTIONS,
    allow_public_exposure: bool = False,
    allow_stock: bool = False,
    skip_mod_sync: bool = False,
    skip_bind_probe: bool = False,
    front_door_bin: str | None = None,
    ts3_path_arg: str | None = None,
    sync_policy_arg: str | None = None,
    saves_dir: Path | None = None,
    bind_probe_fn: Callable[[Path, str, str], None] = probe_binary_bind,
    fork_checker: Callable[[bool], dict[str, Any]] = _check_fork_state,
    sidecar_checker: Callable[[Path], dict[str, Any]] = _check_sidecar_state,
    mod_checker: Callable[[bool], dict[str, Any]] = lambda skip: _check_mod_state(skip_mod_sync=skip),
) -> dict[str, Any]:
    """Validate every endpoint, binary, fork, mod, save, and policy precondition before mutating anything."""
    errors: list[dict[str, str]] = []
    warnings: list[str] = []

    if connections < 1:
        errors.append({"code": "invalid_connections", "message": "--connections must be >= 1."})

    public_ep: Endpoint | None = None
    admin_ep: Endpoint | None = None
    bootstrap_ep: Endpoint | None = None

    try:
        public_ep, pub_warnings = validate_public_endpoint(public_raw, allow_public_exposure=allow_public_exposure)
        warnings.extend(pub_warnings)
    except OperatorError as exc:
        errors.append({"code": exc.code, "message": exc.message})

    try:
        admin_ep = validate_admin_endpoint(admin_raw)
    except OperatorError as exc:
        errors.append({"code": exc.code, "message": exc.message})

    try:
        bootstrap_ep = validate_admin_endpoint(bootstrap_raw, role="Bootstrap")
    except OperatorError as exc:
        raise_code = "bootstrap_not_loopback"
        errors.append({"code": raise_code, "message": exc.message})

    if public_ep and admin_ep and public_ep.port == admin_ep.port:
        errors.append({"code": "port_collision", "message": "--public and --admin cannot share the same TCP port."})
    if public_ep and bootstrap_ep and public_ep.port == bootstrap_ep.port:
        errors.append(
            {"code": "port_collision", "message": "--public and --bootstrap-endpoint cannot share the same TCP port."}
        )
    if admin_ep and bootstrap_ep and admin_ep.port == bootstrap_ep.port:
        errors.append(
            {"code": "port_collision", "message": "--admin and --bootstrap-endpoint cannot share the same TCP port."}
        )

    binary_path: Path | None = None
    binary_sha: str | None = None
    try:
        binary_path = resolve_front_door_binary(front_door_bin)
        binary_sha = sha256_file(binary_path)
    except OperatorError as exc:
        errors.append({"code": exc.code, "message": exc.message})

    ts3_path: Path | None = None
    sidecar_info: dict[str, Any] | None = None
    try:
        ts3_path = resolve_ts3_binary(ts3_path_arg)
        sidecar_info = sidecar_checker(ts3_path)
    except OperatorError as exc:
        errors.append({"code": exc.code, "message": exc.message})

    fork_info: dict[str, Any] | None = None
    try:
        fork_info = fork_checker(allow_stock)
    except OperatorError as exc:
        errors.append({"code": exc.code, "message": exc.message})

    mod_info: dict[str, Any] | None = None
    try:
        mod_info = mod_checker(skip_mod_sync)
    except OperatorError as exc:
        errors.append({"code": exc.code, "message": exc.message})

    resolved_saves_dir = saves_dir if saves_dir is not None else _saves_dir_default()
    save_folder = find_save_folder(save_name, resolved_saves_dir) if resolved_saves_dir is not None else None
    if save_folder is None:
        errors.append(
            {
                "code": "save_not_found",
                "message": f"Save '{save_name}' (with or without .sims3) not found in '{resolved_saves_dir}'.",
            }
        )

    policy_sha: str | None = None
    if sync_policy_arg:
        if binary_path is None:
            errors.append({"code": "sync_policy_unchecked", "message": "Cannot check --sync-policy without Rust binary."})
        else:
            try:
                policy_sha = check_sync_policy(binary_path, Path(sync_policy_arg))
            except OperatorError as exc:
                errors.append({"code": exc.code, "message": exc.message})

    if not skip_bind_probe and binary_path is not None and public_ep is not None and admin_ep is not None:
        try:
            bind_probe_fn(binary_path, public_ep.canonical, admin_ep.canonical)
        except OperatorError as exc:
            errors.append({"code": exc.code, "message": exc.message})
    if not skip_bind_probe and bootstrap_ep is not None:
        try:
            probe_bootstrap_bind(bootstrap_ep)
        except OperatorError as exc:
            errors.append({"code": exc.code, "message": exc.message})

    return {
        "ok": not errors,
        "errors": errors,
        "warnings": warnings,
        "public": public_ep.canonical if public_ep else public_raw,
        "admin": admin_ep.canonical if admin_ep else admin_raw,
        "bootstrap": bootstrap_ep.canonical if bootstrap_ep else bootstrap_raw,
        "connections": connections,
        "frontDoorBin": str(binary_path) if binary_path else None,
        "frontDoorSha256": binary_sha,
        "ts3Path": str(ts3_path) if ts3_path else None,
        "sidecar": sidecar_info,
        "fork": fork_info,
        "mod": mod_info,
        "save": str(save_folder) if save_folder else None,
        "syncPolicySha256": policy_sha,
    }


def _port_open(ep: Endpoint, timeout: float = 0.4) -> bool:
    try:
        with socket.create_connection((ep.connect_host, ep.port), timeout=timeout):
            return True
    except OSError:
        return False


def _wait_port_closed(ep: Endpoint, timeout: float = 10.0) -> None:
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        if not _port_open(ep, timeout=0.25):
            return
        time.sleep(0.2)
    raise OperatorError("admin_port_busy", f"Port {ep.canonical} remained occupied after stopping bootstrap controller.")


def _bootstrap_argv(
    *,
    save_name: str,
    bootstrap_ep: Endpoint,
    admin_ep: Endpoint,
    host_timeout: int,
    ts3_path: Path,
    build: bool,
) -> list[str]:
    ns = argparse.Namespace(build=build)
    exe = stackctl.slot_binary("server", ns)
    tail = [
        "--save",
        save_name,
        "--host-timeout-seconds",
        str(host_timeout),
        "--listen",
        bootstrap_ep.canonical,
        "--bridgectl-listen",
        admin_ep.canonical,
        "--sync-broadcast-seconds",
        "2",
        "--state-file",
        str(SERVER_STATE_FILE),
        "--ts3",
        str(ts3_path),
    ]
    if exe:
        return [exe, *tail]
    run_cmd = ["dotnet", "run", "--project", "DedicatedServer/SimsOnline.DedicatedServer.csproj"]
    if not build:
        run_cmd.append("--no-build")
    return [*run_cmd, "-c", "Debug", "--", *tail]


def _spawn_detached(
    argv: Sequence[str],
    log_path: Path,
    *,
    cwd: Path = REPO,
    extra_env: Mapping[str, str] | None = None,
) -> subprocess.Popen[Any]:
    log_path.parent.mkdir(parents=True, exist_ok=True)
    env = os.environ.copy()
    if extra_env:
        env.update(extra_env)

    extra_kwargs: dict[str, Any] = {}
    if os.name == "nt":
        startup = subprocess.STARTUPINFO()
        startup.dwFlags |= subprocess.STARTF_USESHOWWINDOW
        startup.wShowWindow = subprocess.SW_HIDE
        extra_kwargs = {"creationflags": subprocess.CREATE_NEW_CONSOLE, "startupinfo": startup}
    else:
        extra_kwargs = {"start_new_session": True}

    with log_path.open("w", encoding="utf-8") as stdout_f, (log_path.with_suffix(".err")).open(
        "w", encoding="utf-8"
    ) as stderr_f:
        return subprocess.Popen(
            list(argv),
            stdout=stdout_f,
            stderr=stderr_f,
            cwd=str(cwd),
            env=env,
            stdin=subprocess.DEVNULL,
            **extra_kwargs,
        )


def _wait_bootstrap_ready(
    proc: subprocess.Popen[Any],
    state_file: Path,
    timeout: float,
    *,
    clock: Callable[[], float] = time.monotonic,
    sleeper: Callable[[float], None] = time.sleep,
) -> tuple[int, int | None]:
    """Wait until `state_file` hits `phase == 'ready'`, returning `(ts3_pid, controller_pid)`."""
    deadline = clock() + timeout
    while clock() < deadline:
        code = proc.poll()
        if code is not None:
            raise OperatorError(
                "bootstrap_exited",
                f"C# bootstrap DedicatedServer exited unexpectedly with code {code} before world ready.",
            )
        if state_file.is_file():
            try:
                data = json.loads(state_file.read_text(encoding="utf-8-sig"))
            except (OSError, ValueError):
                data = {}
            if data.get("phase") == "ready" and data.get("gamePid"):
                controller_pid = int(data["pid"]) if data.get("pid") else None
                return int(data["gamePid"]), controller_pid
        sleeper(0.5)
    raise OperatorError("bootstrap_timeout", f"C# bootstrap timed out after {timeout:.0f}s waiting for phase=ready.")


def ensure_push_off(
    admin_ep: Endpoint,
    *,
    requester: Callable[[Endpoint, str], dict[str, Any]] = admin_request,
    timeout: float = 15.0,
    clock: Callable[[], float] = time.monotonic,
    sleeper: Callable[[float], None] = time.sleep,
) -> dict[str, Any]:
    """Send `SACS_RECORDER push-off` on `admin_ep`, waiting through `presentation_push_off_pending`."""
    deadline = clock() + timeout
    last_reply: dict[str, Any] = {}
    while clock() < deadline:
        reply = requester(admin_ep, "SACS_RECORDER push-off")
        last_reply = reply
        report = str(reply.get("report") or "")
        reason = str(reply.get("reason") or "")
        if reply.get("ok") is True and ("push=off" in report or "presentation_push_off=ready" in report):
            return reply
        if reply.get("accepted") is True and reason != "presentation_push_off_pending":
            return reply
        if reason and reason != "presentation_push_off_pending":
            raise OperatorError("push_off_rejected", f"SACS_RECORDER push-off failed: {reply}")
        sleeper(0.25)
    raise OperatorError("push_off_timeout", f"SACS_RECORDER push-off timed out: {last_reply}")


def _wait_rust_ready(
    proc: subprocess.Popen[Any],
    public_ep: Endpoint,
    admin_ep: Endpoint,
    timeout: float,
    *,
    hello_probe: Callable[[Endpoint], tuple[bool, str | None]] = probe_public_hello,
    admin_ask: Callable[[Endpoint, str], dict[str, Any]] = admin_request,
    clock: Callable[[], float] = time.monotonic,
    sleeper: Callable[[float], None] = time.sleep,
) -> tuple[str, dict[str, Any]]:
    deadline = clock() + timeout
    last_err: str | None = "not tried yet"
    while clock() < deadline:
        code = proc.poll()
        if code is not None:
            raise OperatorError("front_door_exited", f"simlink_headless_server exited early with code {code}.")
        ok, welcome_or_err = hello_probe(public_ep)
        if ok and welcome_or_err is not None:
            state = admin_ask(admin_ep, "FRONT_DOOR_STATE")
            return welcome_or_err, state
        last_err = welcome_or_err
        sleeper(0.4)
    raise OperatorError("front_door_timeout", f"simlink_headless_server did not respond to HELLO in {timeout:.0f}s: {last_err}")


def _write_operator_state(
    *,
    public_ep: Endpoint,
    admin_ep: Endpoint,
    ts3_pid: int,
    rust_pid: int,
    binary: Path,
    binary_sha: str,
    save_name: str,
) -> None:
    RUN_DIR.mkdir(parents=True, exist_ok=True)
    now_iso = datetime.now(timezone.utc).isoformat()
    op_state = {
        "mode": "rust-front-door",
        "updatedAtUtc": now_iso,
        "publicEndpoint": public_ep.canonical,
        "adminEndpoint": admin_ep.canonical,
        "gamePid": ts3_pid,
        "rustPid": rust_pid,
        "frontDoorBin": str(binary),
        "frontDoorSha256": binary_sha,
        "save": save_name,
    }
    OPERATOR_STATE_FILE.write_text(json.dumps(op_state, indent=2) + "\n", encoding="utf-8")
    SERVER_STATE_FILE.write_text(
        json.dumps(
            {
                "mode": "rust-front-door",
                "phase": "ready",
                "gamePid": ts3_pid,
                "rustPid": rust_pid,
                "publicEndpoint": public_ep.canonical,
                "adminEndpoint": admin_ep.canonical,
                "updatedAtUtc": now_iso,
            },
            indent=2,
        )
        + "\n",
        encoding="utf-8",
    )
    SERVER_TS3_PID_FILE.write_text(f"{ts3_pid}\n", encoding="utf-8")


def _check_stack_claim(ignore_claim: bool) -> None:
    if ignore_claim:
        return
    holder = stackctl.claim_holder()
    if holder:
        raise OperatorError("claim_held", f"Live stack claim is held by another session: {holder} (use --ignore-claim to override).")
    conflict = stack_claim.session_conflict(os.environ)
    if conflict:
        raise OperatorError("claim_conflict", conflict)


def _quiet_warm_server_ts3_pid() -> int | None:
    """Resolve the running server TS3W PID without emitting stackctl `cprint` lines to stdout."""
    tracked_pid: int | None = None
    tracked_alive = False
    if SERVER_TS3_PID_FILE.is_file():
        try:
            tracked_pid = int(SERVER_TS3_PID_FILE.read_text(encoding="utf-8").strip())
            tracked_alive = bool(stackctl._pid_alive(tracked_pid))
        except (OSError, ValueError):
            tracked_pid = None
    client_pid = stackctl.get_live_pid_from_file(str(Path(stackctl.CLIENT_TS3_PID_FILE)))
    live = stackctl.get_ts3_pids()
    selected = stackctl.select_server_ts3_pid_candidate(tracked_pid, tracked_alive, live, client_pid)
    return int(selected) if selected is not None else None


def _quiet_save_run_logs() -> None:
    """Archive prior `.omo/run` logs without letting stackctl print to stdout."""
    with contextlib.redirect_stdout(io.StringIO()):
        stackctl.save_run_logs()


def _dedupe_pids(pids: Sequence[int | None]) -> list[int]:
    seen: set[int] = set()
    ordered: list[int] = []
    for pid in pids:
        if pid is not None and pid > 0 and pid not in seen:
            seen.add(pid)
            ordered.append(pid)
    return ordered


def _front_door_controller_pids(args: argparse.Namespace) -> list[int]:
    ns = argparse.Namespace(**{**vars(args), "front_door": True})
    return list(stackctl._server_controller_pids(ns, stackctl.Platform()))


def _state_controller_pid(state_file: Path) -> int | None:
    if not state_file.is_file():
        return None
    try:
        data = json.loads(state_file.read_text(encoding="utf-8-sig"))
    except (OSError, ValueError):
        return None
    pid = data.get("pid") or data.get("rustPid")
    return int(pid) if isinstance(pid, int) and pid > 0 else None


def run_start(
    args: argparse.Namespace,
    *,
    preflight_fn: Callable[..., dict[str, Any]] = run_preflight,
    claim_checker: Callable[[bool], None] = _check_stack_claim,
    spawner: Callable[..., subprocess.Popen[Any]] = _spawn_detached,
    stop_controller_fn: Callable[..., Any] = stackctl.stop_controller,
    bootstrap_waiter: Callable[..., int | tuple[int, int | None]] = _wait_bootstrap_ready,
    push_off_fn: Callable[..., dict[str, Any]] = ensure_push_off,
    rust_waiter: Callable[..., tuple[str, dict[str, Any]]] = _wait_rust_ready,
    port_open_fn: Callable[[Endpoint], bool] = _port_open,
    port_close_waiter: Callable[[Endpoint, float], None] = _wait_port_closed,
    existing_ts3_resolver: Callable[[], int | None] = _quiet_warm_server_ts3_pid,
    controller_pids_fn: Callable[[argparse.Namespace], list[int]] = _front_door_controller_pids,
    admin_ask: Callable[[Endpoint, str], dict[str, Any]] = admin_request,
    state_writer: Callable[..., None] = _write_operator_state,
    save_logs_fn: Callable[[], None] = _quiet_save_run_logs,
    bootstrap_state_file: Path = SERVER_STATE_FILE,
) -> dict[str, Any]:
    """Preflight everything first, then bootstrap TS3 -> push-off -> swap to Rust front door."""
    claim_checker(args.ignore_claim)

    existing_ts3_pid = args.attach_pid
    if existing_ts3_pid is None and args.attach_existing:
        existing_ts3_pid = existing_ts3_resolver()
        if not existing_ts3_pid:
            raise OperatorError("no_warm_ts3", "--attach-existing found no live server TS3W.exe PID.")

    # When swapping onto an already-live C# controller, skip the bind probe so the active port
    # does not fail preflight before the swap stops C#.
    skip_probe = args.skip_bind_probe or args.dry_run or (existing_ts3_pid is not None and port_open_fn(parse_endpoint(args.admin)))

    preflight = preflight_fn(
        public_raw=args.public,
        admin_raw=args.admin,
        bootstrap_raw=args.bootstrap_endpoint,
        save_name=args.save,
        connections=args.connections,
        allow_public_exposure=args.i_understand_public_exposure,
        allow_stock=args.allow_stock,
        skip_mod_sync=args.skip_mod_sync,
        skip_bind_probe=skip_probe,
        front_door_bin=args.front_door_bin,
        ts3_path_arg=args.ts3_path,
        sync_policy_arg=args.sync_policy,
    )
    if not preflight["ok"]:
        summary = "; ".join(f"{err['code']}: {err['message']}" for err in preflight["errors"])
        raise OperatorError("preflight_failed", summary)

    public_ep = parse_endpoint(preflight["public"])
    admin_ep = parse_endpoint(preflight["admin"])
    bootstrap_ep = parse_endpoint(preflight["bootstrap"])
    binary = Path(preflight["frontDoorBin"])
    ts3_path = Path(preflight["ts3Path"])
    join_cmd = format_tester_join_command(
        public_ep,
        player_id=args.tester_player_id,
        claim_sim=args.tester_claim_sim,
    )

    rust_argv = [
        str(binary),
        "--pid",
        str(existing_ts3_pid or "<BOOT_TS3_PID>"),
        "--serve",
        "--public",
        public_ep.canonical,
        "--admin",
        admin_ep.canonical,
        "--connections",
        str(args.connections),
        "--save",
        args.save,
    ]
    if args.checkpoint_save:
        rust_argv += ["--checkpoint-save", args.checkpoint_save]
    if args.sync_policy:
        rust_argv += ["--sync-policy", args.sync_policy]

    if args.dry_run:
        return {
            "ok": True,
            "dryRun": True,
            "preflight": preflight,
            "mode": "attach" if existing_ts3_pid else "cold-bootstrap-then-swap",
            "plannedRustArgv": rust_argv,
            "testerJoinCommand": join_cmd,
        }

    if existing_ts3_pid is None:
        running_ts3 = existing_ts3_resolver()
        if running_ts3:
            raise OperatorError(
                "warm_ts3_already_running",
                f"Server TS3W.exe (PID {running_ts3}) is already running; pass --attach-existing or stop it first.",
            )
        if port_open_fn(public_ep):
            raise OperatorError("public_port_busy", f"Public endpoint {public_ep.canonical} is already listening.")

        save_logs_fn()
        if bootstrap_state_file.is_file():
            bootstrap_state_file.unlink(missing_ok=True)
        boot_argv = _bootstrap_argv(
            save_name=args.save,
            bootstrap_ep=bootstrap_ep,
            admin_ep=admin_ep,
            host_timeout=args.host_timeout,
            ts3_path=ts3_path,
            build=args.build,
        )
        boot_proc = spawner(boot_argv, BOOTSTRAP_LOG, extra_env=stackctl.new_role_env("server"))
        boot_ready = bootstrap_waiter(boot_proc, bootstrap_state_file, float(args.host_timeout))
        ts3_pid, controller_pid = boot_ready if isinstance(boot_ready, tuple) else (boot_ready, None)
        push_off_fn(admin_ep)
        for pid in _dedupe_pids([controller_pid, boot_proc.pid]):
            stop_controller_fn(pid, graceful=False)
        if boot_proc.poll() is None:
            try:
                boot_proc.wait(timeout=5)
            except subprocess.TimeoutExpired:
                pass
        port_close_waiter(admin_ep, 10.0)
    else:
        ts3_pid = int(existing_ts3_pid)
        if port_open_fn(admin_ep):
            try:
                front_door_live = bool(admin_ask(admin_ep, "FRONT_DOOR_STATE").get("ok"))
            except OperatorError:
                front_door_live = False
            if front_door_live:
                try:
                    admin_ask(admin_ep, "QUIT")
                except OperatorError as exc:
                    if exc.code != "admin_empty_reply":
                        raise
            else:
                push_off_fn(admin_ep)
                pids = _dedupe_pids([*controller_pids_fn(args), _state_controller_pid(bootstrap_state_file)])
                for pid in pids:
                    stop_controller_fn(pid, graceful=False)
            port_close_waiter(admin_ep, 10.0)

    rust_argv[2] = str(ts3_pid)
    extra_env = {"SIMSONLINE_ROUTE_STREAM": "1"} if args.route_stream else None
    rust_proc = spawner(rust_argv, SERVER_LOG, extra_env=extra_env)
    welcome_line, front_door_state = rust_waiter(rust_proc, public_ep, admin_ep, float(args.timeout))
    state_writer(
        public_ep=public_ep,
        admin_ep=admin_ep,
        ts3_pid=ts3_pid,
        rust_pid=rust_proc.pid,
        binary=binary,
        binary_sha=preflight["frontDoorSha256"],
        save_name=args.save,
    )

    return {
        "ok": True,
        "dryRun": False,
        "warnings": preflight["warnings"],
        "ts3Pid": ts3_pid,
        "rustPid": rust_proc.pid,
        "public": public_ep.canonical,
        "admin": admin_ep.canonical,
        "frontDoorSha256": preflight["frontDoorSha256"],
        "welcome": welcome_line,
        "frontDoorState": front_door_state,
        "testerJoinCommand": join_cmd,
    }


def run_status(
    public_raw: str,
    admin_raw: str,
    *,
    hello_probe: Callable[[Endpoint], tuple[bool, str | None]] = probe_public_hello,
    admin_ask: Callable[[Endpoint, str], dict[str, Any]] = admin_request,
    operator_state_file: Path = OPERATOR_STATE_FILE,
) -> dict[str, Any]:
    """Query public `HELLO`/`WELCOME` and loopback `FRONT_DOOR_STATE` + `DIAG_STATE`."""
    public_ep = parse_endpoint(public_raw)
    admin_ep = validate_admin_endpoint(admin_raw)
    hello_ok, welcome_or_err = hello_probe(public_ep)
    front_door_state: dict[str, Any] | None = None
    diag_state: dict[str, Any] | None = None
    admin_error: str | None = None
    try:
        front_door_state = admin_ask(admin_ep, "FRONT_DOOR_STATE")
        diag_state = admin_ask(admin_ep, "DIAG_STATE")
    except OperatorError as exc:
        admin_error = exc.message

    saved_state: dict[str, Any] | None = None
    if operator_state_file.is_file():
        try:
            saved_state = json.loads(operator_state_file.read_text(encoding="utf-8"))
        except (OSError, ValueError):
            saved_state = None

    return {
        "ok": bool(hello_ok and admin_error is None),
        "public": public_ep.canonical,
        "admin": admin_ep.canonical,
        "publicWelcomeOk": hello_ok,
        "publicReply": welcome_or_err,
        "frontDoorState": front_door_state,
        "diagState": diag_state,
        "adminError": admin_error,
        "savedOperatorState": saved_state,
    }


def run_stop(
    admin_raw: str,
    *,
    down_ts3: bool = False,
    ignore_claim: bool = False,
    admin_ask: Callable[[Endpoint, str], dict[str, Any]] = admin_request,
    port_open_fn: Callable[[Endpoint], bool] = _port_open,
    port_close_waiter: Callable[[Endpoint, float], None] = _wait_port_closed,
    claim_checker: Callable[[bool], None] = _check_stack_claim,
) -> dict[str, Any]:
    """Ask the Rust headless server to exit cleanly over loopback `QUIT`; optionally run `stackctl down`."""
    claim_checker(ignore_claim)
    admin_ep = validate_admin_endpoint(admin_raw)
    reply: dict[str, Any] | None = None
    if port_open_fn(admin_ep):
        try:
            reply = admin_ask(admin_ep, "QUIT")
        except OperatorError as exc:
            if exc.code != "admin_empty_reply":
                raise
        port_close_waiter(admin_ep, 10.0)
    if down_ts3:
        cmd = [sys.executable, str(SCRIPTS_DIR / "stackctl.py"), "down"]
        if ignore_claim:
            cmd.append("--ignore-claim")
        proc = subprocess.run(cmd, cwd=str(REPO), stdin=subprocess.DEVNULL, check=False)
        if proc.returncode != 0:
            raise OperatorError("down_failed", f"`stackctl.py down` exited with code {proc.returncode}.")
    return {"ok": True, "admin": admin_ep.canonical, "quitReply": reply, "downTs3": down_ts3}


def _add_common_flags(parser: argparse.ArgumentParser) -> None:
    parser.add_argument("--public", default=DEFAULT_PUBLIC_ENDPOINT, help="Public v1 bind <ip:port> (default: 127.0.0.1:34019)")
    parser.add_argument("--admin", default=DEFAULT_ADMIN_ENDPOINT, help="Loopback admin <ip:port> (default: 127.0.0.1:34029)")
    parser.add_argument("--json", action="store_true", help="Emit machine-readable JSON")


def _add_preflight_flags(parser: argparse.ArgumentParser) -> None:
    parser.add_argument("--bootstrap-endpoint", default=DEFAULT_BOOTSTRAP_ENDPOINT, help="Loopback endpoint for C# bootstrap (default: 127.0.0.1:34119)")
    parser.add_argument("--save", default=DEFAULT_SAVE, help=f"Save folder name (.sims3 or extensionless, default: {DEFAULT_SAVE})")
    parser.add_argument("--connections", type=int, default=DEFAULT_CONNECTIONS, help=f"Max concurrent connections (default: {DEFAULT_CONNECTIONS})")
    parser.add_argument("--i-understand-public-exposure", action="store_true", help="Acknowledge unauthenticated SECURITY.md warning for non-loopback --public binds")
    parser.add_argument("--allow-stock", action="store_true", help="Permit launch without fork only when physical census is empty")
    parser.add_argument("--skip-mod-sync", action="store_true", help="Skip workspace-vs-installed mod staleness check")
    parser.add_argument("--skip-bind-probe", action="store_true", help="Skip `simlink_headless_server --bind-only` probe")
    parser.add_argument("--front-door-bin", help="Explicit path to simlink_headless_server(.exe)")
    parser.add_argument("--ts3-path", help="Explicit path to TS3W.exe")
    parser.add_argument("--sync-policy", help="Optional resident sync policy script path validated via --check-sync-policy")


def build_parser() -> argparse.ArgumentParser:
    """Construct the CLI parser without performing I/O."""
    parser = argparse.ArgumentParser(description="Turnkey operator lifecycle helper for simlink_headless_server.")
    sub = parser.add_subparsers(dest="command", required=True)

    pre = sub.add_parser("preflight", help="Check endpoints, security posture, binary, fork, mod, sidecar, save, and sync policy")
    _add_common_flags(pre)
    _add_preflight_flags(pre)

    start = sub.add_parser("start", help="Preflight, cold-boot TS3 via C# bootstrap, push-off, swap to Rust front door")
    _add_common_flags(start)
    _add_preflight_flags(start)
    start.add_argument("--checkpoint-save", help="Optional checkpoint target save name passed to Rust")
    start.add_argument("--build", action="store_true", help="Build DedicatedServer before bootstrap launch")
    start.add_argument("--route-stream", action="store_true", help="Set SIMSONLINE_ROUTE_STREAM=1 for Rust server")
    start.add_argument("--host-timeout", type=int, default=DEFAULT_HOST_TIMEOUT, help="TS3 world bootstrap timeout in seconds")
    start.add_argument("--timeout", type=int, default=DEFAULT_READY_TIMEOUT, help="Rust HELLO readiness timeout in seconds")
    start.add_argument("--attach-existing", action="store_true", help="Attach to an already-running warm server TS3W.exe")
    start.add_argument("--attach-pid", type=int, help="Explicit already-running server TS3W.exe PID")
    start.add_argument("--ignore-claim", action="store_true", help="Bypass cross-session stack_claim check (operator use)")
    start.add_argument("--dry-run", action="store_true", help="Run full preflight and print planned argv/join command without launching")
    start.add_argument("--tester-player", "--tester-player-id", dest="tester_player_id", type=int, default=2, help="Suggested -Player integer in printed join.ps1 line (default: 2)")
    start.add_argument("--tester-sim", "--tester-claim-sim", dest="tester_claim_sim", help="Optional Sim id or first name for printed join.ps1 -Sim flag")

    status = sub.add_parser("status", help="Probe public v1 HELLO/WELCOME and loopback FRONT_DOOR_STATE")
    _add_common_flags(status)

    stop = sub.add_parser("stop", help="Stop resident Rust front door over loopback QUIT (optionally stop TS3W too)")
    _add_common_flags(stop)
    stop.add_argument("--down-ts3", action="store_true", help="Also run `stackctl.py down` to terminate TS3W")
    stop.add_argument("--ignore-claim", action="store_true", help="Pass --ignore-claim through to stackctl")
    return parser


def _print_result(payload: dict[str, Any], *, as_json: bool) -> None:
    if as_json:
        print(json.dumps(payload, indent=2))
        return
    for warn in payload.get("warnings") or []:
        print(warn, file=sys.stderr)
    if payload.get("errors"):
        for err in payload["errors"]:
            print(f"ERROR [{err['code']}]: {err['message']}", file=sys.stderr)
        return
    if "testerJoinCommand" in payload:
        prefix = "DRY-RUN READY" if payload.get("dryRun") else "HEADLESS FRONT DOOR READY"
        print(f"{prefix}: public={payload.get('public') or payload['preflight']['public']}")
        if not payload.get("dryRun"):
            print(f"  ts3_pid={payload['ts3Pid']} rust_pid={payload['rustPid']} sha256={payload['frontDoorSha256'][:16]}")
            print(f"  welcome={payload['welcome']}")
        print(f"  Tester join (note -Wire line): {payload['testerJoinCommand']}")
        return
    print(json.dumps(payload, indent=2))


def main(argv: Sequence[str] | None = None) -> int:
    parser = build_parser()
    args = parser.parse_args(list(argv) if argv is not None else None)
    try:
        if args.command == "preflight":
            result = run_preflight(
                public_raw=args.public,
                admin_raw=args.admin,
                bootstrap_raw=args.bootstrap_endpoint,
                save_name=args.save,
                connections=args.connections,
                allow_public_exposure=args.i_understand_public_exposure,
                allow_stock=args.allow_stock,
                skip_mod_sync=args.skip_mod_sync,
                skip_bind_probe=args.skip_bind_probe,
                front_door_bin=args.front_door_bin,
                ts3_path_arg=args.ts3_path,
                sync_policy_arg=args.sync_policy,
            )
            _print_result(result, as_json=args.json)
            return 0 if result["ok"] else 2
        if args.command == "start":
            result = run_start(args)
            _print_result(result, as_json=args.json)
            return 0
        if args.command == "status":
            result = run_status(args.public, args.admin)
            _print_result(result, as_json=args.json)
            return 0 if result["ok"] else 1
        if args.command == "stop":
            result = run_stop(args.admin, down_ts3=args.down_ts3, ignore_claim=args.ignore_claim)
            _print_result(result, as_json=args.json)
            return 0
    except OperatorError as exc:
        if getattr(args, "json", False):
            print(json.dumps({"ok": False, "code": exc.code, "error": exc.message}, indent=2))
        else:
            print(f"ERROR [{exc.code}]: {exc.message}", file=sys.stderr)
        return 2
    return 1


if __name__ == "__main__":
    raise SystemExit(main())
