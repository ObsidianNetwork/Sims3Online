#![forbid(unsafe_code)]
//! Explicit resident/live modes; the default remains a short bind-posture probe.

use std::io::{self, BufRead, Write};
use simlink_headless_server::bind::{bind_all, posture_from_args, unrecognized_args};
use simlink_headless_server::bridge_driver::{BridgeGameDriver, OfflineBridge};
use simlink_headless_server::serve::{RunningServer, StreamOptions, read_bounded_line};
use simlink_headless_server::wire_v1 as w;

fn value<'a>(args: &'a [String], flag: &str) -> io::Result<Option<&'a str>> {
    args.iter().position(|arg| arg == flag).map(|i| args.get(i + 1)
        .map(String::as_str).ok_or_else(|| io::Error::other(format!("{flag} needs a value")))).transpose()
}

fn codec_stdio() -> io::Result<()> {
    let mut input = io::stdin().lock();
    let mut output = io::stdout().lock();
    loop {
        let raw = match read_bounded_line(&mut input) {
            Ok(Some(raw)) => raw,
            Ok(None) => break,
            Err(code) => { writeln!(output, "ERR|0|{}|-|", code.as_str())?; break; }
        };
        let encoded = w::split(&raw).and_then(|fields| match fields.first().map(String::as_str) {
            Some("HELLO") => w::decode_hello(&fields).and_then(|v| w::encode_hello(&v)),
            Some("WELCOME") => w::decode_welcome(&fields).and_then(|v| w::encode_welcome(&v)),
            Some("CMD") => w::decode_command(&fields).and_then(|v| w::encode_command(&v)),
            Some("ACK") => w::decode_ack(&fields).and_then(|v| w::encode_ack(&v)),
            Some("ERR") => w::decode_err(&fields).and_then(|v| w::encode_err(&v)),
            Some("EV") => w::decode_event(&fields).and_then(|v| w::encode_event(&v)),
            Some("BULK") => w::decode_bulk(&fields).and_then(|v| w::encode_bulk(&v)),
            Some("Rf") => w::decode_menu_refusal(&fields).and_then(|v| w::encode_menu_refusal(&v)),
            _ => Err(simlink_headless_server::WireError::BadIdentifier),
        });
        match encoded {
            Ok(line) => writeln!(output, "{line}")?,
            Err(_) => writeln!(output, "ERR|0|bad_line|-|")?,
        }
        output.flush()?;
    }
    Ok(())
}

#[cfg(windows)]
fn ts3w_pid() -> io::Result<u32> {
    let result = std::process::Command::new("tasklist")
        .args(["/FI", "IMAGENAME eq TS3W.exe", "/FO", "CSV", "/NH"]).output()?;
    if !result.status.success() { return Err(io::Error::other("tasklist failed")); }
    let pids: Vec<u32> = String::from_utf8_lossy(&result.stdout).lines().filter_map(|line| {
        let fields: Vec<_> = line.split(',').collect();
        if !fields.first()?.trim_matches('"').eq_ignore_ascii_case("TS3W.exe") { return None; }
        fields.get(1)?.trim_matches('"').parse().ok()
    }).collect();
    match pids.as_slice() {
        [pid] => Ok(*pid),
        _ => Err(io::Error::other("--attach-ts3w requires exactly one TS3W; use --pid to choose the server")),
    }
}
#[cfg(not(windows))]
fn ts3w_pid() -> io::Result<u32> {
    Err(io::Error::other("--attach-ts3w is Windows-only; use --pid on a supported bridge host"))
}

fn run() -> io::Result<()> {
    let args: Vec<String> = std::env::args().skip(1).collect();
    if args.iter().any(|arg| arg == "--codec-stdio") { return codec_stdio(); }
    if args.iter().any(|arg| arg == "--trace-stdio") { return simlink_headless_server::trace::stdio(); }
    if let Some(dir) = value(&args, "--scorecard-dir")? {
        return simlink_headless_server::scorecard::emit(
            &std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("../.."),
            std::path::Path::new(dir),
        );
    }
    if let Some(dir) = value(&args, "--card58-scorecard-dir")? {
        return simlink_headless_server::card58_scorecard::emit(
            &std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("../.."), std::path::Path::new(dir));
    }
    if let Some(dir) = value(&args, "--card59-scorecard-dir")? {
        return simlink_headless_server::card59_scorecard::emit(std::path::Path::new(dir));
    }
    for ignored in unrecognized_args(&args) {
        eprintln!("simlink_headless_server: ignoring unrecognized argument '{ignored}'");
    }
    let has = |flag: &str| args.iter().any(|arg| arg == flag);
    let pid = value(&args, "--pid")?.map(|s| s.parse::<u32>().map_err(io::Error::other)).transpose()?;
    if has("--fake-driver") && (pid.is_some() || has("--attach-ts3w")) {
        return Err(io::Error::other("--fake-driver cannot be combined with a live attach"));
    }
    if pid.is_some() && has("--attach-ts3w") {
        return Err(io::Error::other("choose --pid or --attach-ts3w, not both"));
    }
    if has("--bind-only") && (has("--serve") || has("--resident") || has("--fake-driver") || pid.is_some() || has("--attach-ts3w")) {
        return Err(io::Error::other("--bind-only cannot be combined with resident/live flags"));
    }
    let limit = value(&args, "--connections")?.unwrap_or("32").parse::<usize>().map_err(io::Error::other)?;
    let resident = has("--serve") || has("--resident") || has("--fake-driver") || pid.is_some() || has("--attach-ts3w");
    let admin_addr = simlink_headless_server::bind::admin_from_args(&args, resident).map_err(io::Error::other)?;
    let options = StreamOptions::from_args(&args).map_err(io::Error::other)?;
    let posture = posture_from_args(&args).map_err(|e| io::Error::new(io::ErrorKind::InvalidInput, e))?;
    if let Some(warning) = posture.exposure_warning() { println!("{warning}"); }
    let mut bound = bind_all(&posture)?;
    let admin_listener = admin_addr.map(std::net::TcpListener::bind).transpose()?;
    for (listener, spec) in bound.iter().zip(posture.listeners()) {
        let addr = listener.local_addr()?;
        let scope = if addr.ip().is_loopback() { "loopback" } else { "public" };
        println!("simlink_headless_server: {} listener bound to {} ({scope})", spec.role, addr);
    }
    if let Some(listener) = &admin_listener {
        println!("simlink_headless_server: admin listener bound to {} (loopback)", listener.local_addr()?);
    }
    io::stdout().flush()?;
    if !resident {
        return Ok(());
    }
    let listener = bound.pop().ok_or_else(|| io::Error::other("missing public listener"))?;
    let save = value(&args, "--save")?.unwrap_or(simlink_headless_server::checkpoint::DEFAULT_SAVE_NAME).to_owned();
    let checkpoint = value(&args, "--checkpoint-save")?.map(str::to_owned)
        .or_else(|| std::env::var("SIMSONLINE_CHECKPOINT_SAVE").ok());
    let live_pid = if has("--attach-ts3w") { Some(ts3w_pid()?) } else { pid };
    let server = if let Some(pid) = live_pid {
        RunningServer::start_configured(listener, admin_listener, move || {
            let mut driver = BridgeGameDriver::attach_pid(pid).map_err(io::Error::other)?;
            driver.configure_saves(save, checkpoint);
            Ok(driver)
        }, limit, options)?
    } else if has("--fake-driver") {
        RunningServer::start_configured(listener, admin_listener, move || {
            let mut driver = BridgeGameDriver::new(OfflineBridge);
            driver.configure_saves(save, checkpoint);
            Ok(driver)
        }, limit, options)?
    } else {
        return Err(io::Error::other("resident mode requires --fake-driver, --pid <pid>, or --attach-ts3w"));
    };
    let shutdown = server.shutdown_handle();
    ctrlc::set_handler(move || shutdown.shutdown()).map_err(io::Error::other)?;
    let shutdown = server.shutdown_handle();
    std::thread::spawn(move || {
        for line in io::stdin().lock().lines() {
            match line {
                Ok(line) if line.trim() == "QUIT" => { shutdown.shutdown(); break; }
                Ok(_) => {}
                Err(_) => break,
            }
        }
    });
    println!("simlink_headless_server: serving {}", server.local_addr());
    server.wait()
}

fn main() {
    if let Err(error) = run() {
        eprintln!("simlink_headless_server: {error}");
        // A rejected listener endpoint is a usage error (exit 2); everything else is a runtime failure (exit 1).
        std::process::exit(if error.kind() == io::ErrorKind::InvalidInput { 2 } else { 1 });
    }
}
