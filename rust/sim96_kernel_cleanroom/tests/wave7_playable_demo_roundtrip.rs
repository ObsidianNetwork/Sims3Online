#![forbid(unsafe_code)]
//! Wave 7 · Items 31–33 end to end over real loopback TCP against the
//! `simlink_authority_daemon` seam: two-client HELLO + CoW bootstrap, the
//! six playable verbs, the structured rejection gate, and every broadcast
//! delta frame family (`AS`, `Wd`, `Od`/`Hd`/`Rl`, `Vh`/`Vs`/`Vp`/`Ve`).
//!
//! Binds `127.0.0.1:0` (ephemeral) so this test never needs, never takes and
//! never disturbs the live-stack claim; no TS3 process is involved.
//!
//! The pump broadcasts `AS` every millisecond, so a client that drains mid
//! run never sees an idle gap: everything is pipelined up front and read to
//! server shutdown, exactly once the frame families have all been produced.

use std::io::{BufRead, BufReader, Write};
use std::net::TcpStream;
use std::thread;
use std::time::Duration;

use sim96_kernel_cleanroom::runtime_bridge::{AUTHORITY_ROUTES, AuthorityDaemon, AuthorityOwner};
use sim96_kernel_cleanroom::vehicles::parse_vehicle_vp;

fn drain_to_eof(r: &mut BufReader<TcpStream>) -> Vec<String> {
    let mut out = Vec::new();
    loop {
        let mut line = String::new();
        match r.read_line(&mut line) {
            Ok(0) => return out,
            Ok(_) => out.push(line.trim_end().to_string()),
            Err(_) => return out, // read timeout: the pipe is momentarily dry
        }
    }
}

#[test]
fn wave7_playable_demo_roundtrip() {
    // The expanded runtime acceptance commands are Rust-owned (incl. the
    // card-49 DIAG_STATE operator surface); the five native engine
    // responsibilities remain outside the kernel.
    let rust = AUTHORITY_ROUTES.iter().filter(|(_, o)| *o == AuthorityOwner::RustKernel).count();
    assert_eq!((rust, AUTHORITY_ROUTES.len() - rust), (27, 3));
    assert_eq!(AUTHORITY_ROUTES.len(), 30);

    let mut daemon = AuthorityDaemon::bind("127.0.0.1:0".parse().unwrap()).expect("bind");
    daemon.ticks = 1_200; // ~1.3 s of pump: covers the 12-segment demo travel
    daemon.max_connections = 4;
    let addr = daemon.local_addr().expect("local addr");
    let serve_thread = thread::spawn(move || daemon.serve());

    let connect = || -> (TcpStream, BufReader<TcpStream>) {
        let s = TcpStream::connect(addr).expect("connect");
        s.set_read_timeout(Some(Duration::from_millis(4_000))).expect("rto");
        let r = BufReader::new(s.try_clone().expect("clone"));
        (s, r)
    };

    // Client A: one pipelined burst - a pre-HELLO probe (refused), the
    // Item 32 HELLO, then the six Item 33 verbs and every rejection case.
    let (mut a, mut a_r) = connect();
    let burst = "CMD|1|ClaimSim|1|100\n\
                 HELLO|2|1|1|17|15|client\n\
                 CMD|3|RequestWorld|9|9|9\n\
                 CMD|4|SetClientRole|player\n\
                 CMD|10|ClaimSim|1|100\n\
                 CMD|11|ClaimSim|2|200\n\
                 CMD|12|EnqueueInteraction|1|SocializeWith|server|2|100\n\
                 CMD|13|ModifyFunds|100|-500|purchase\n\
                 CMD|14|RouteSlotClaim|1|7|0|1\n\
                 CMD|15|SwapClaim|1|3\n\
                 CMD|16|DialogAnswer|1|1\n\
                 CMD|17|EnqueueInteraction|1|BedSleep|client|0|100\n\
                 CMD|18|EnqueueInteraction|1|RouteAway|client|0|100\n\
                 CMD|19|SpawnService|Maid\n\
                 CMD|20|NavigateAStar|3|1000|1024|0\n\
                 CMD|21|PlayJazzClip|3|1|50\n\
                 FindBestSocial|22|1\n";
    a.write_all(burst.as_bytes()).unwrap();
    a.flush().unwrap();

    // Client B: an unpatched CLIENT_ENV (missing SimLink ring) is refused
    // with a named diagnostic; client C: the wrong bridge version too.
    let (mut b, mut b_r) = connect();
    b.write_all(b"HELLO|30|0|1|17|15|client2\n").unwrap();
    let (mut c, mut c_r) = connect();
    c.write_all(b"HELLO|31|1|1|16|15|client3\n").unwrap();

    // One client hammers a duplicate CommandId inside the 32-frame window
    // (card 33 §D.2: replay the original result, never a second effect) and
    // a stale corr outside it (card 10 §B.1 rule 3: silent drop).
    let (mut d, mut d_r) = connect();
    d.write_all(b"HELLO|1|1|1|17|15|client3\nCMD|38|ClaimSim|4|300\nCMD|40|ModifyFunds|300|-10|purchase\nCMD|40|ModifyFunds|300|-10|purchase\nCMD|39|ModifyFunds|300|-10|purchase\n")
        .unwrap();

    let fa = drain_to_eof(&mut a_r);
    let fb = drain_to_eof(&mut b_r);
    let fc = drain_to_eof(&mut c_r);
    let fd = drain_to_eof(&mut d_r);

    // Item 33 precondition: a command before HELLO is refused, structured.
    let has = |f: &[String], p: &str| f.iter().any(|l| l.starts_with(p));
    assert!(has(&fa, "ERR|0|bad_line|say_hello_first"), "{:?}", &fa[..fa.len().min(6)]);
    // Item 32: HELLO -> WELCOME + non-blocking CoW SNAP bootstrap, then the
    // card-32 eleven-step request_world bootstrap and set_client_role.
    assert!(fa.iter().any(|l| l == "WELCOME|2|1|17|15"), "{:?}", fa);
    assert!(has(&fa, "SNAP|"), "SNAP bootstrap");
    assert!(has(&fa, "ACK|3|RequestWorld|checkpoint"), "fingerprint MISS: {:?}", fa);
    assert_eq!(fa.iter().filter(|l| l.starts_with("WFC|")).count(), 11, "11 world_file_chunk frames");
    assert!(has(&fa, "PRIMARY|100|1"), "Martinez pin is read-only on join");
    assert!(has(&fa, "HISTRESET|3"), "late-join history reset");
    assert!(has(&fa, "ACK|4|SetClientRole|player"), "step 9 role declaration");

    // Item 33: authoritative command round-trips and delta frames.
    assert!(has(&fa, "ACK|10|SimClaimed|1|100|tick="), "ACKs carry the authoritative tick");
    assert!(has(&fa, "ACK|11|SimClaimed|2|200"));
    assert!(has(&fa, "ACK|12|InteractionQueued"), "server-side enqueue");
    assert!(has(&fa, "Rl|11|"), "Rl relationship mirror: {:?}", fa);
    assert!(has(&fa, "Hd|13|100|-500"), "Hd funds delta");
    assert!(has(&fa, "ACK|14|RouteSlotClaim"));
    assert!(has(&fa, "ACK|15|SwapClaim|cancelled=1"), "cancel: {:?}", fa);
    assert!(has(&fa, "ACK|16|DialogAnswer"));
    assert!(has(&fa, "DLGC|1|Selected|1"), "dialog_answer_captured");
    // The rejection gate, deterministic and structured: exact KernelError
    // names the controller relays verbatim (card 33 §C).
    assert!(has(&fa, "ERR|17|UnauthorizedClientInsertion|BedSleep"), "{:?}", fa);
    assert!(has(&fa, "ERR|18|UnauthorizedClientInsertion|RouteAway"));
    assert!(has(&fa, "ERR|19|ClientWorldServiceSuppressed|Maid"), "{:?}", fa);
    assert!(has(&fa, "ACK|20|NavigateAStar|legs="), "A* is kernel route #25 (Wave 11)");
    assert!(has(&fa, "ACK|21|PlayJazzClip|clip="), ".jazz is kernel route #26 (Wave 12)");
    assert!(has(&fa, "ERR|0|ClientReselectForbidden|FindBestSocial"), "no reselect");
    // Broadcast frame families: seed packets, world ticks, whole travel.
    assert!(has(&fa, "AS|"), "AS seed packets");
    assert!(fa.iter().any(|l| l.starts_with("Wd|")), "Wd world frames");
    assert!(has(&fa, "Vs|") && has(&fa, "Vh|"), "mount lines");
    // A vehicle reader skips route points (card 54 §C.2), so A*'s `Vp|rt` lines cannot stand in.
    assert!(fa.iter().any(|l| parse_vehicle_vp(l).is_some()), "Vp stream");
    assert!(fa.iter().any(|l| l.starts_with("Ve|")), "Ve arrival");
    // Item 32 refusals on the other sockets, with named diagnostics.
    assert!(fb.iter().any(|l| l == "ERR|30|hello_rejected|MissingSimlinkRing"), "{:?}", fb);
    assert!(fc.iter().any(|l| l.contains("hello_rejected|WrongBridgeVersion")), "{:?}", fc);
    // CommandId idempotency: the in-window duplicate replays the ORIGINAL
    // frame twice (same tx id, one ledger effect); the stale corr outside
    // the window drops silently, answering nothing at all.
    assert!(has(&fd, "ACK|38|SimClaimed|4|300|"), "funds replay requires its household claim");
    let dup: Vec<&String> = fd.iter().filter(|l| l.starts_with("Hd|40|")).collect();
    assert_eq!(dup.len(), 2, "in-window dupe must replay the original: {:?}", fd);
    assert_eq!(dup[0], dup[1], "replayed frame must equal the original");
    assert!(!fd.iter().any(|l| l.starts_with("Hd|39|")), "stale corr must drop: {:?}", fd);
    assert!(fd.iter().any(|l| l == "WELCOME|1|4|17|15"), "{:?}", fd);

    let _ = a.shutdown(std::net::Shutdown::Both);
    let _ = b.shutdown(std::net::Shutdown::Both);
    let _ = c.shutdown(std::net::Shutdown::Both);
    let _ = d.shutdown(std::net::Shutdown::Both);
    let report = serve_thread.join().expect("serve join").expect("serve io");
    // The non-blocking join invariant (card 10 §D.3) and's rule.
    assert_eq!(report.connections_accepted, 4, "{:?}", report);
    assert_eq!(report.hello_rejections, 2, "{:?}", report);
    assert_eq!(report.dropped_ticks_during_join, 0, "{:?}", report);
    assert!(!report.dialog_clock_paused, "the dialog never pauses the clock");
    assert!(report.ticks_committed >= 1_200);
}
