#![forbid(unsafe_code)]
//! Wave 7 · the runnable Option 2 authority sidecar daemon (cards 31–33).
//!
//! Loopback-only TCP sidecar (card 31 §D + SECURITY.md: it binds
//! `127.0.0.1`, accepts no connection the controller did not authenticate,
//! and holds no client credentials). It speaks the `CONNECT|19` compat hello,
//! the `HELLO`/`CLIENT_ENV` gate, the card-32 `request_world` bootstrap
//! (fingerprint HIT/MISS -> reuse or five-phase checkpoint, 11 `WFC`
//! `world_file_chunk` frames, the read-only Martinez primary, the
//! `sim_claimed` replay, the history reset), the card-33 nine-step command
//! round trip with in-window CommandId idempotent replay, and the three
//! explicit KernelError rejections the controller relays verbatim.
//!
//! Flags: `--self-test` runs the whole adjudication surface in-process (no
//! sockets at all); `--demo-roundtrip` runs the scripted two-client loopback
//! TCP demo end to end; the default mode serves `--port` (default 34019, the
//! tester-kit public port) for `--ticks` ticks. `--resident` adds the live
//! sidecar service mode for the Wave-7 real-client TS3 run (card 31 §D): the
//! accept loop stays open for the process's whole life and a gated `QUIT|corr`
//! line retires the pump so the daemon prints its serve report and exits on
//! demand — batch callers (the wave-7 harness, the fuzz suite) keep the exact
//! 10 s-window behaviour without the flag. This process never launches or
//! attaches `TS3W.exe` and takes no live-stack claim.
use std::io::{BufRead, BufReader, Write};
use std::net::{SocketAddr, TcpStream};
use std::thread;
use std::time::Duration;

use sim96_kernel_cleanroom::runtime_bridge::{
    AUTHORITY_ROUTES, AuthorityDaemon, AuthorityOwner, AuthorityWorld,
};
use sim96_kernel_cleanroom::wan_transport::RoleTag;
use sim96_kernel_cleanroom::wire::PlayerId;

fn main() -> std::process::ExitCode {
    let argv: Vec<String> = std::env::args().collect();
    let flag = |name: &str| argv.iter().any(|a| a == name);
    let value = |name: &str, default: &str| -> String {
        argv.iter().position(|a| a == name)
            .and_then(|i| argv.get(i + 1))
            .cloned()
            .unwrap_or_else(|| default.to_string())
    };
    let bind_ip = value("--bind", "127.0.0.1");
    let port: u16 = value("--port", "34019").parse().unwrap_or(34019);
    let ticks: u64 = value("--ticks", "1200").parse().unwrap_or(1200);
    let connections: usize = value("--connections", "2").parse().unwrap_or(2);

    if flag("--private-navigation-stdio") {
        // Stdout is exclusively private binary records in this opt-in mode.
        // A shell/public socket cannot install navigation into another daemon.
        let listen: SocketAddr = match format!("{bind_ip}:{port}").parse() {
            Ok(addr) => addr,
            Err(_) => { eprintln!("invalid private daemon address"); return std::process::ExitCode::FAILURE; }
        };
        let result = AuthorityDaemon::bind(listen).and_then(|mut daemon| {
            daemon.resident = true;
            daemon.ticks = u64::MAX;
            daemon.max_connections = usize::MAX;
            daemon.serve_inherited(std::io::stdin(),std::io::stdout())
        });
        return match result {
            Ok(_) => std::process::ExitCode::SUCCESS,
            Err(_) => { eprintln!("private daemon transport failed"); std::process::ExitCode::FAILURE }
        };
    }
    let rust = AUTHORITY_ROUTES.iter().filter(|(_, o)| *o == AuthorityOwner::RustKernel).count();
    println!("authority sidecar (Option 2 seam): {} authority routes, {} Rust-kernel / {} native-TS3W",
        AUTHORITY_ROUTES.len(), rust, AUTHORITY_ROUTES.len() - rust);
    if flag("--self-test") {
        return match self_test() {
            Ok(summary) => {
                println!("self-test PASS: {}", summary);
                std::process::ExitCode::SUCCESS
            }
            Err(e) => {
                eprintln!("self-test FAIL: {e}");
                std::process::ExitCode::FAILURE
            }
        };
    }
    if flag("--demo-roundtrip") || flag("--demo") {
        return match demo_round_trip(ticks, connections) {
            Ok(summary) => {
                println!("demo-roundtrip PASS: {}", summary);
                std::process::ExitCode::SUCCESS
            }
            Err(e) => {
                eprintln!("DEMO FAILED: {e}");
                std::process::ExitCode::FAILURE
            }
        };
    }
    let listen: SocketAddr = format!("{}:{}", bind_ip, port)
        .parse()
        .unwrap_or_else(|_| SocketAddr::from(([127, 0, 0, 1], port)));
    let result = AuthorityDaemon::bind(listen).and_then(|mut d| {
        d.ticks = ticks;
        d.max_connections = connections;
        d.resident = flag("--resident");
        println!("listening on {}{}", d.local_addr()?, if d.resident { " (resident sidecar: QUIT|corr retires it with its report)" } else { "" });
        d.serve()
    });
    match result {
        Ok(r) => {
            println!("report: connections={} hello_rejections={} commands={} ticks={} join_drops={} clock_paused={}",
                r.connections_accepted, r.hello_rejections, r.commands_processed,
                r.ticks_committed, r.dropped_ticks_during_join, r.dialog_clock_paused);
            std::process::ExitCode::SUCCESS
        }
        Err(e) => {
            eprintln!("cannot serve: {e}");
            std::process::ExitCode::FAILURE
        }
    }
}

/// Item 31–33 in-process over the pure decision core: no sockets, no ports,
/// every verdict + rejection + the CONNECT/request_world bootstrap exercised
/// through `AuthorityWorld::handle_line` exactly as the TCP pipe would.
fn self_test() -> Result<String, String> {
    let mut w = AuthorityWorld::new();
    let mut out = Vec::new();
    // CONNECT|19 re-attach hello (card 31 row 1) grades through the gate.
    w.handle_line(PlayerId(1), RoleTag::Client, "CONNECT|19|1|1|17|15|client", &mut out);
    expect(&out, "WELCOME|0|1|17|15")?;
    // HELLO with the full CLIENT_ENV (card 19) bootstrap-gates a new join.
    w.handle_line(PlayerId(1), RoleTag::Client, "HELLO|1|1|1|17|15|client", &mut out);
    expect(&out, "WELCOME|1|1|17|15")?;
    // The card-32 eleven-step bootstrap, fingerprint MISS -> checkpoint path.
    out.clear();
    w.handle_line(PlayerId(1), RoleTag::Client, "CMD|2|RequestWorld|9|9|9", &mut out);
    expect(&out, "CHECKPOINT|2|five_phase")?;
    expect(&out, "WFC|10|11|")?; // the 11th world_file_chunk frame
    expect(&out, "PRIMARY|100|1")?; // Martinez pin, read-only
    expect(&out, "HISTRESET|2")?;
    // Card 33 §A: the nine-step round trip for ClaimSim + the Rl row.
    out.clear();
    w.handle_line(PlayerId(1), RoleTag::Client, "CMD|10|ClaimSim|1|100", &mut out);
    w.handle_line(PlayerId(2), RoleTag::Client2, "CMD|11|ClaimSim|2|200", &mut out);
    expect(&out, "ACK|10|SimClaimed|1|100|tick=")?;
    expect(&out, "Rl|11|")?; // claim replay + Rl mirror ride the next bootstrap
    out.clear();
    w.handle_line(PlayerId(1), RoleTag::Client, "CMD|12|RequestWorld|9|9|9", &mut out);
    expect(&out, "SIMC|1|100")?;
    expect(&out, "SIMC|2|200")?;
    // The three explicit rejections (card 33 §C), exact KernelError names.
    out.clear();
    w.handle_line(PlayerId(1), RoleTag::Client,
        "CMD|13|EnqueueInteraction|1|BedSleep|client|0|100", &mut out);
    expect(&out, "ERR|13|UnauthorizedClientInsertion|BedSleep")?;
    out.clear();
    w.handle_line(PlayerId(1), RoleTag::Client, "CMD|14|SpawnService|Maid", &mut out);
    expect(&out, "ERR|14|ClientWorldServiceSuppressed|Maid")?;
    w.handle_line(PlayerId(1), RoleTag::Client, "CMD|15|RouteSlotClaim|1|7|0|1", &mut out);
    out.clear();
    w.handle_line(PlayerId(2), RoleTag::Client2, "CMD|16|RouteSlotClaim|2|7|0|1", &mut out);
    expect(&out, "ERR|16|SlotAlreadyReserved|1")?;
    // CommandId window: in-window replay returns the original result.
    out.clear();
    w.handle_line(PlayerId(1), RoleTag::Client, "CMD|17|ModifyFunds|100|-500|purchase", &mut out);
    let first = out.clone();
    out.clear();
    w.handle_line(PlayerId(1), RoleTag::Client, "CMD|17|ModifyFunds|100|-500|purchase", &mut out);
    if out != first {
        return Err(format!("CommandId replay mismatch: {:?} vs {:?}", out, first));
    }
    if w.funds.ledger[&sim96_kernel_cleanroom::authority::HouseholdId(100)] != 19_500 {
        return Err("ledger double-applied inside the window".into());
    }
    Ok(format!("BOOTSTRAP+REJECTIONS+IDEMPOTENCY ({} recent cmds, ticks={})",
        w.recent_cmds.len(), w.clock.native_tick_cnt))
}

fn expect(out: &[String], prefix: &str) -> Result<(), String> {
    if out.iter().any(|l| l.starts_with(prefix)) {
        Ok(())
    } else {
        Err(format!("missing frame {:?} in {:?}", prefix, out))
    }
}

/// Item 31/32/33 end to end over loopback TCP, asserting every frame family.
fn demo_round_trip(ticks: u64, connections: usize) -> Result<String, String> {
    let mut daemon = AuthorityDaemon::bind("127.0.0.1:0".parse().unwrap()).map_err(|e| e.to_string())?;
    daemon.ticks = ticks;
    daemon.max_connections = connections;
    let addr = daemon.local_addr().map_err(|e| e.to_string())?;
    let serve_thread = thread::spawn(move || daemon.serve().map_err(|e| e.to_string()));
    thread::sleep(Duration::from_millis(30));

    let mut a = TcpStream::connect(addr).map_err(|e| format!("client A connect: {e}"))?;
    a.set_read_timeout(Some(Duration::from_millis(4_000))).map_err(|e| e.to_string())?;
    let mut a_r = BufReader::new(a.try_clone().map_err(|e| e.to_string())?);
    let mut b = TcpStream::connect(addr).map_err(|e| format!("client B connect: {e}"))?;
    b.set_read_timeout(Some(Duration::from_millis(4_000))).map_err(|e| e.to_string())?;
    let mut b_r = BufReader::new(b.try_clone().map_err(|e| e.to_string())?);

    let drain = |r: &mut BufReader<TcpStream>| -> Vec<String> {
        let mut v = Vec::new();
        loop {
            let mut line = String::new();
            match r.read_line(&mut line) {
                Ok(0) => break,
                Ok(_) => v.push(line.trim_end().to_string()),
                Err(_) => break,
            }
        }
        v
    };

    // Item 32: pipelined HELLO + RequestWorld bootstrap; the pin and
    // the eleven world_file_chunk frames must come back before any command.
    a.write_all(b"HELLO|1|1|1|17|15|client\nCMD|2|RequestWorld|9|9|9\n").map_err(|e| e.to_string())?;
    // A second, unpatched client gets a named refusal, not a reset.
    b.write_all(b"HELLO|3|0|1|17|15|client2\n").map_err(|e| e.to_string())?;
    thread::sleep(Duration::from_millis(80));

    // Item 33: the six live verbs + the rejection gate + native refusals.
    let cmds = [
        "CMD|10|ClaimSim|1|100\n",
        "CMD|11|ClaimSim|2|200\n",
        "CMD|12|EnqueueInteraction|1|SocializeWith|server|2|100\n",
        "CMD|13|ModifyFunds|100|-500|purchase\n",
        "CMD|14|RouteSlotClaim|1|7|0|1\n",
        "CMD|15|SwapClaim|1|3\n",
        "CMD|16|DialogAnswer|1|1\n",
        "CMD|17|EnqueueInteraction|1|BedSleep|client|0|100\n",
        "CMD|18|EnqueueInteraction|1|RouteAway|client|0|100\n",
        "CMD|19|SpawnService|Maid\n",
        "CMD|20|NavigateAStar|3|1000|1024|0\n",
    ];
    for c in cmds {
        a.write_all(c.as_bytes()).map_err(|e| e.to_string())?;
    }
    a.write_all(b"CMD|21|SetClientRole|player\n").map_err(|e| e.to_string())?;
    a.flush().map_err(|e| e.to_string())?;

    let mut fa = drain(&mut a_r);
    let fb = drain(&mut b_r);
    // Let the tick pump stream AS/Wd/V-frames, then read the tail.
    thread::sleep(Duration::from_millis(160));
    fa.extend(drain(&mut a_r));
    let _ = a.shutdown(std::net::Shutdown::Both);
    let _ = b.shutdown(std::net::Shutdown::Both);

    let report = serve_thread.join().map_err(|_| "serve thread panicked".to_string())??;
    let want = ["WELCOME|1", "SNAP|", "ACK|2|RequestWorld|checkpoint|tick=", "WFC|10|11|",
        "PRIMARY|100|1", "HISTRESET|2", "ACK|10|SimClaimed|1|100|tick=", "Rl|11|", "Od|", "Hd|13|100|-500|",
        "ACK|14|RouteSlotClaim", "ACK|15|SwapClaim|cancelled=1", "ACK|16|DialogAnswer", "DLGC|1|Selected|1",
        "ACK|21|SetClientRole|player",
        "ERR|17|UnauthorizedClientInsertion|BedSleep", "ERR|18|UnauthorizedClientInsertion|RouteAway",
        "ERR|19|ClientWorldServiceSuppressed|Maid", "ACK|20|NavigateAStar|legs=", "Rt|20|3|5|",
        "AS|", "Vh|", "Vs|", "Vp|"];
    let mut missing = Vec::new();
    for w in want {
        if !fa.iter().any(|l| l.starts_with(w)) {
            missing.push(w);
        }
    }
    if !fb.iter().any(|l| l.starts_with("ERR|3|hello_rejected|MissingSimlinkRing")) {
        missing.push("B hello_rejected");
    }
    if report.dropped_ticks_during_join != 0 || report.dialog_clock_paused {
        return Err(format!("join/clock invariant broke: {:?}", report));
    }
    if !missing.is_empty() {
        return Err(format!("missing frames {:?} in {} lines", missing, fa.len()));
    }
    Ok(format!(
        "{} A-frames (CONNECT/HELLO/WFC bootstrap/SIMC replay/ACK/Rl/Od/Hd/Vh/Vs/Vp/AS), B refused, drops={} ticks={}",
        fa.len(), report.dropped_ticks_during_join, report.ticks_committed
    ))
}
