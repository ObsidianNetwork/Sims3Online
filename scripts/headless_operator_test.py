"""Offline unit tests for scripts/headless_operator.py."""
from __future__ import annotations

import argparse
import json
import socket
import subprocess
import sys
import tempfile
import threading
import unittest
from pathlib import Path
from typing import Any
from unittest import mock

REPO = Path(__file__).resolve().parent.parent
SCRIPTS = REPO / "scripts"
if str(SCRIPTS) not in sys.path:
    sys.path.insert(0, str(SCRIPTS))

import headless_operator as ho  # noqa: E402


class _FakeProc:
    def __init__(self, pid: int, exit_code: int | None = None) -> None:
        self.pid = pid
        self._exit_code = exit_code

    def poll(self) -> int | None:
        return self._exit_code

    def wait(self, timeout: float | None = None) -> int:
        _ = timeout
        return 0 if self._exit_code is None else self._exit_code


def _start_line_server(handler: Any) -> tuple[socket.socket, int, threading.Thread]:
    srv = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    srv.bind(("127.0.0.1", 0))
    srv.listen(4)
    port = srv.getsockname()[1]

    def serve() -> None:
        while True:
            try:
                conn, _ = srv.accept()
            except OSError:
                break
            with conn:
                with conn.makefile("r", encoding="utf-8", errors="replace") as reader:
                    data = reader.readline(4096).strip()
                reply = handler(data)
                if reply is not None:
                    conn.sendall(f"{reply}\n".encode("utf-8"))

    thread = threading.Thread(target=serve, daemon=True)
    thread.start()
    return srv, port, thread


class CliHelpTests(unittest.TestCase):
    def test_top_and_subcommand_help_exit_cleanly(self) -> None:
        for argv in (["--help"], ["preflight", "--help"], ["start", "--help"], ["status", "--help"], ["stop", "--help"]):
            proc = subprocess.run(
                [sys.executable, str(SCRIPTS / "headless_operator.py"), *argv],
                cwd=REPO,
                capture_output=True,
                text=True,
                stdin=subprocess.DEVNULL,
                timeout=30,
                check=False,
            )
            self.assertEqual(proc.returncode, 0, msg=f"{argv} failed: {proc.stderr}")
            self.assertIn("usage:", proc.stdout.lower())


class EndpointAndSecurityTests(unittest.TestCase):
    def test_parse_endpoint_accepts_ipv4_localhost_and_bracketed_ipv6(self) -> None:
        ep4 = ho.parse_endpoint("localhost:34019")
        self.assertEqual(ep4.canonical, "127.0.0.1:34019")
        self.assertTrue(ep4.is_loopback)

        ep_any = ho.parse_endpoint("0.0.0.0:34019")
        self.assertTrue(ep_any.is_unspecified)
        self.assertEqual(ep_any.connect_host, "127.0.0.1")

        ep6 = ho.parse_endpoint("[::1]:34029")
        self.assertEqual(ep6.canonical, "[::1]:34029")
        self.assertTrue(ep6.is_loopback)

    def test_parse_endpoint_rejects_bad_forms_ports_and_hostnames(self) -> None:
        for bad in ("", "127.0.0.1", ":34019", "127.0.0.1:0", "127.0.0.1:70000", "host.example:34019", "::1:34019"):
            with self.assertRaises(ho.OperatorError, msg=f"expected failure for {bad!r}"):
                ho.parse_endpoint(bad)

    def test_public_endpoint_requires_confirmation_when_non_loopback(self) -> None:
        ep, warnings = ho.validate_public_endpoint("127.0.0.1:34019", allow_public_exposure=False)
        self.assertEqual(ep.canonical, "127.0.0.1:34019")
        self.assertEqual(warnings, [])

        with self.assertRaises(ho.OperatorError) as ctx:
            ho.validate_public_endpoint("100.88.12.4:34019", allow_public_exposure=False)
        self.assertEqual(ctx.exception.code, "public_exposure_unconfirmed")

        ep_ts, warnings_ts = ho.validate_public_endpoint("100.88.12.4:34019", allow_public_exposure=True)
        self.assertEqual(ep_ts.canonical, "100.88.12.4:34019")
        self.assertEqual(len(warnings_ts), 1)
        self.assertIn("no authentication", warnings_ts[0])

        _, warnings_any = ho.validate_public_endpoint("0.0.0.0:34019", allow_public_exposure=True)
        self.assertEqual(len(warnings_any), 2)
        self.assertIn("Tailscale", warnings_any[1])

    def test_admin_endpoint_must_be_loopback(self) -> None:
        self.assertTrue(ho.validate_admin_endpoint("127.0.0.1:34029").is_loopback)
        for bad in ("0.0.0.0:34029", "[::1]:34029", "100.88.12.4:34029", "192.168.1.5:34029"):
            with self.assertRaises(ho.OperatorError) as ctx:
                ho.validate_admin_endpoint(bad)
            self.assertEqual(ctx.exception.code, "admin_not_loopback")

    def test_format_tester_join_command_always_passes_wire_line(self) -> None:
        cmd_ts = ho.format_tester_join_command(ho.parse_endpoint("100.64.1.9:34019"), player_id=2, claim_sim=42)
        self.assertEqual(cmd_ts, '.\\join.ps1 -Server "100.64.1.9:34019" -Wire line -Player 2 -Sim "42"')

        cmd_any = ho.format_tester_join_command(ho.parse_endpoint("0.0.0.0:34019"))
        self.assertIn("-Wire line", cmd_any)
        self.assertIn("<YOUR-TAILSCALE-OR-LAN-IP>:34019", cmd_any)


class SaveAndBinaryResolutionTests(unittest.TestCase):
    def test_find_save_folder_handles_sim203_extensions_both_ways(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            saves = Path(tmp)
            (saves / "Sunset Valley.sims3").mkdir()
            (saves / "Riverview").mkdir()

            self.assertEqual(ho.find_save_folder("Sunset Valley.sims3", saves), saves / "Sunset Valley.sims3")
            self.assertEqual(ho.find_save_folder("Sunset Valley", saves), saves / "Sunset Valley.sims3")
            self.assertEqual(ho.find_save_folder("Riverview.sims3", saves), saves / "Riverview")
            self.assertEqual(ho.find_save_folder("Riverview", saves), saves / "Riverview")
            self.assertIsNone(ho.find_save_folder("Missing.sims3", saves))

    def test_resolve_front_door_binary_checks_release_layout_and_cargo_target(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            repo = Path(tmp)
            target_bin = repo / "custom_target" / "release" / "simlink_headless_server.exe"
            target_bin.parent.mkdir(parents=True)
            target_bin.write_bytes(b"mz")

            found = ho.resolve_front_door_binary(
                env={"CARGO_TARGET_DIR": "custom_target"},
                repo=repo,
                is_windows=True,
            )
            self.assertEqual(found, target_bin.resolve())

            dist_repo = Path(tmp) / "dist_layout"
            dist_bin = dist_repo / "dist" / "simlink_headless_server-win-x64" / "simlink_headless_server.exe"
            dist_bin.parent.mkdir(parents=True)
            dist_bin.write_bytes(b"mz")
            self.assertEqual(
                ho.resolve_front_door_binary(env={}, repo=dist_repo, is_windows=True),
                dist_bin.resolve(),
            )

            with self.assertRaises(ho.OperatorError) as ctx:
                ho.resolve_front_door_binary(env={}, repo=Path(tmp) / "empty", is_windows=True)
            self.assertEqual(ctx.exception.code, "front_door_binary_missing")
            self.assertIn("cargo build", ctx.exception.message)


class PreflightAndLifecycleTests(unittest.TestCase):
    def _setup_fixture_tree(self, root: Path) -> tuple[Path, Path, Path]:
        exe = root / "simlink_headless_server.exe"
        exe.write_bytes(b"rust-bin")
        ts3 = root / "Game" / "Bin" / "TS3W.exe"
        ts3.parent.mkdir(parents=True)
        ts3.write_bytes(b"ts3w")
        saves = root / "Saves"
        (saves / "Sunset Valley").mkdir(parents=True)
        return exe, ts3, saves

    def test_preflight_collects_errors_without_side_effects(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            exe, ts3, saves = self._setup_fixture_tree(Path(tmp))
            result = ho.run_preflight(
                public_raw="100.70.1.2:34019",
                admin_raw="100.70.1.2:34029",
                save_name="NoSuchSave.sims3",
                connections=0,
                allow_public_exposure=False,
                front_door_bin=str(exe),
                ts3_path_arg=str(ts3),
                saves_dir=saves,
                skip_bind_probe=True,
                fork_checker=lambda _: (_ for _ in ()).throw(ho.OperatorError("fork_census_failed", "bad fork")),
                sidecar_checker=lambda _: (_ for _ in ()).throw(ho.OperatorError("sidecar_missing", "no asi")),
                mod_checker=lambda _: (_ for _ in ()).throw(ho.OperatorError("mod_stale", "stale mod")),
            )
            self.assertFalse(result["ok"])
            codes = {err["code"] for err in result["errors"]}
            self.assertEqual(
                codes,
                {
                    "invalid_connections",
                    "public_exposure_unconfirmed",
                    "admin_not_loopback",
                    "fork_census_failed",
                    "sidecar_missing",
                    "mod_stale",
                    "save_not_found",
                },
            )

    def test_preflight_rejects_busy_bootstrap_port(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            exe, ts3, saves = self._setup_fixture_tree(Path(tmp))
            with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as occupied:
                occupied.bind(("127.0.0.1", 0))
                occupied.listen(1)
                busy_port = occupied.getsockname()[1]
                result = ho.run_preflight(
                    bootstrap_raw=f"127.0.0.1:{busy_port}",
                    save_name="Sunset Valley",
                    front_door_bin=str(exe),
                    ts3_path_arg=str(ts3),
                    saves_dir=saves,
                    bind_probe_fn=lambda *_: None,
                    fork_checker=lambda _: {"ok": True},
                    sidecar_checker=lambda _: {"ok": True},
                    mod_checker=lambda _: {"ok": True},
                )
        self.assertFalse(result["ok"])
        self.assertIn("bootstrap_port_busy", {err["code"] for err in result["errors"]})

    def test_ensure_push_off_retries_pending_then_succeeds(self) -> None:
        replies = iter(
            [
                {"ok": True, "accepted": False, "reason": "presentation_push_off_pending", "report": None},
                {"ok": True, "accepted": True, "reason": None, "report": "presentation_push_off=ready"},
            ]
        )
        t = [0.0]

        def clock() -> float:
            return t[0]

        def sleeper(dt: float) -> None:
            t[0] += dt

        ep = ho.parse_endpoint("127.0.0.1:34029")
        out = ho.ensure_push_off(ep, requester=lambda *_: next(replies), timeout=5.0, clock=clock, sleeper=sleeper)
        self.assertEqual(out["report"], "presentation_push_off=ready")

    def test_run_start_aborts_before_stopping_or_spawning_when_preflight_fails(self) -> None:
        spawner = mock.Mock()
        stopper = mock.Mock()
        args = self._default_start_args(public="100.80.1.2:34019", i_understand_public_exposure=False)
        with self.assertRaises(ho.OperatorError) as ctx:
            ho.run_start(
                args,
                preflight_fn=lambda **_: {"ok": False, "errors": [{"code": "public_exposure_unconfirmed", "message": "unconfirmed"}]},
                claim_checker=lambda _: None,
                spawner=spawner,
                stop_controller_fn=stopper,
            )
        self.assertEqual(ctx.exception.code, "preflight_failed")
        spawner.assert_not_called()
        stopper.assert_not_called()

    def test_run_start_refuses_cold_launch_if_warm_ts3_exists_without_attach(self) -> None:
        spawner = mock.Mock()
        with tempfile.TemporaryDirectory() as tmp:
            exe, ts3, saves = self._setup_fixture_tree(Path(tmp))
            args = self._default_start_args(front_door_bin=str(exe), ts3_path=str(ts3))
            with self.assertRaises(ho.OperatorError) as ctx:
                ho.run_start(
                    args,
                    preflight_fn=lambda **_: self._ok_preflight(exe, ts3, saves),
                    claim_checker=lambda _: None,
                    spawner=spawner,
                    existing_ts3_resolver=lambda: 9912,
                )
            self.assertEqual(ctx.exception.code, "warm_ts3_already_running")
            spawner.assert_not_called()

    def test_run_start_executes_cold_bootstrap_push_off_and_rust_swap_in_order(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            exe, ts3, saves = self._setup_fixture_tree(root)
            state_file = root / "server.state.json"
            events: list[tuple[str, Any]] = []
            spawned_cmds: list[list[str]] = []

            def spawner(argv: list[str], log_path: Path, **kwargs: Any) -> _FakeProc:
                _ = log_path, kwargs
                spawned_cmds.append(list(argv))
                if len(spawned_cmds) == 1:
                    events.append(("spawn_bootstrap", argv))
                    return _FakeProc(pid=2001)
                events.append(("spawn_rust", argv))
                return _FakeProc(pid=2002)

            def stopper(pid: int, graceful: bool = True, grace_seconds: int = 5) -> None:
                _ = graceful, grace_seconds
                events.append(("stop_bootstrap", pid))

            args = self._default_start_args(
                public="100.64.5.6:34019",
                i_understand_public_exposure=True,
                front_door_bin=str(exe),
                ts3_path=str(ts3),
                tester_claim_sim=777,
            )
            result = ho.run_start(
                args,
                preflight_fn=lambda **_: self._ok_preflight(exe, ts3, saves, public="100.64.5.6:34019"),
                claim_checker=lambda _: events.append(("claim", None)),
                spawner=spawner,
                stop_controller_fn=stopper,
                bootstrap_waiter=lambda proc, sf, t: (events.append(("wait_bootstrap", proc.pid)), 4444)[1],
                push_off_fn=lambda ep: (events.append(("push_off", ep.canonical)), {"ok": True, "report": "presentation_push_off=ready"})[1],
                port_open_fn=lambda _: False,
                port_close_waiter=lambda ep, _: events.append(("wait_admin_closed", ep.canonical)),
                rust_waiter=lambda proc, pub, adm, t: (events.append(("wait_rust", proc.pid)), ("WELCOME|1|1|mods", {"ok": True}))[1],
                existing_ts3_resolver=lambda: None,
                state_writer=lambda **kw: events.append(("write_state", kw["rust_pid"])),
                save_logs_fn=lambda: events.append(("save_logs", None)),
                bootstrap_state_file=state_file,
            )

            self.assertTrue(result["ok"])
            self.assertEqual(result["ts3Pid"], 4444)
            self.assertEqual(result["rustPid"], 2002)
            self.assertEqual(
                [name for name, _ in events],
                [
                    "claim",
                    "save_logs",
                    "spawn_bootstrap",
                    "wait_bootstrap",
                    "push_off",
                    "stop_bootstrap",
                    "wait_admin_closed",
                    "spawn_rust",
                    "wait_rust",
                    "write_state",
                ],
            )
            self.assertIn("--listen", spawned_cmds[0])
            self.assertIn("127.0.0.1:34119", spawned_cmds[0])
            self.assertEqual(spawned_cmds[1][:7], [str(exe), "--pid", "4444", "--serve", "--public", "100.64.5.6:34019", "--admin"])
            self.assertEqual(
                result["testerJoinCommand"],
                '.\\join.ps1 -Server "100.64.5.6:34019" -Wire line -Player 2 -Sim "777"',
            )

    def test_run_start_dry_run_skips_spawning(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            exe, ts3, saves = self._setup_fixture_tree(Path(tmp))
            spawner = mock.Mock()
            args = self._default_start_args(dry_run=True, front_door_bin=str(exe), ts3_path=str(ts3))
            result = ho.run_start(
                args,
                preflight_fn=lambda **_: self._ok_preflight(exe, ts3, saves),
                claim_checker=lambda _: None,
                spawner=spawner,
            )
            self.assertTrue(result["ok"])
            self.assertTrue(result["dryRun"])
            self.assertEqual(result["mode"], "cold-bootstrap-then-swap")
            self.assertIn("-Wire line", result["testerJoinCommand"])
            spawner.assert_not_called()

    def test_run_status_and_stop_talk_to_loopback_sockets(self) -> None:
        pub_srv, pub_port, _ = _start_line_server(lambda req: "WELCOME|1|0|mods" if req.startswith("HELLO|1|") else "ERR|0|bad|-|")
        adm_seen: list[str] = []

        def admin_handler(req: str) -> str:
            adm_seen.append(req)
            if req == "FRONT_DOOR_STATE":
                return json.dumps({"ok": True, "report": "sessions=0"})
            if req == "DIAG_STATE":
                return json.dumps({"ok": True, "report": "tick=10"})
            if req == "QUIT":
                return json.dumps({"ok": True, "report": "quitting"})
            return json.dumps({"ok": False})

        adm_srv, adm_port, _ = _start_line_server(admin_handler)
        try:
            with tempfile.TemporaryDirectory() as tmp:
                op_file = Path(tmp) / "headless-operator.json"
                op_file.write_text(json.dumps({"mode": "rust-front-door"}), encoding="utf-8")
                status = ho.run_status(
                    f"127.0.0.1:{pub_port}",
                    f"127.0.0.1:{adm_port}",
                    operator_state_file=op_file,
                )
                self.assertTrue(status["ok"])
                self.assertTrue(status["publicWelcomeOk"])
                self.assertEqual(status["frontDoorState"]["report"], "sessions=0")
                self.assertEqual(status["diagState"]["report"], "tick=10")

                stopped = ho.run_stop(
                    f"127.0.0.1:{adm_port}",
                    ignore_claim=True,
                    port_close_waiter=lambda *_: None,
                    claim_checker=lambda _: None,
                )
                self.assertTrue(stopped["ok"])
                self.assertIn("QUIT", adm_seen)
        finally:
            pub_srv.close()
            adm_srv.close()

    def _default_start_args(self, **overrides: Any) -> argparse.Namespace:
        base = {
            "public": "127.0.0.1:34019",
            "admin": "127.0.0.1:34029",
            "bootstrap_endpoint": "127.0.0.1:34119",
            "save": "Sunset Valley",
            "checkpoint_save": None,
            "connections": 32,
            "i_understand_public_exposure": False,
            "allow_stock": False,
            "skip_mod_sync": True,
            "skip_bind_probe": True,
            "front_door_bin": None,
            "ts3_path": None,
            "sync_policy": None,
            "build": False,
            "route_stream": False,
            "host_timeout": 120,
            "timeout": 30,
            "attach_existing": False,
            "attach_pid": None,
            "ignore_claim": True,
            "dry_run": False,
            "tester_player_id": 2,
            "tester_claim_sim": None,
            "json": False,
        }
        base.update(overrides)
        return argparse.Namespace(**base)

    def _ok_preflight(self, exe: Path, ts3: Path, saves: Path, public: str = "127.0.0.1:34019") -> dict[str, Any]:
        return {
            "ok": True,
            "errors": [],
            "warnings": [],
            "public": public,
            "admin": "127.0.0.1:34029",
            "bootstrap": "127.0.0.1:34119",
            "connections": 32,
            "frontDoorBin": str(exe),
            "frontDoorSha256": "deadbeef" * 8,
            "ts3Path": str(ts3),
            "sidecar": {"installed": "abc(v11)"},
            "fork": {"state": "fork"},
            "mod": {"stale": False},
            "save": str(saves / "Sunset Valley"),
            "syncPolicySha256": None,
        }


if __name__ == "__main__":
    unittest.main()
