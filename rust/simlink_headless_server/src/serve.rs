#![forbid(unsafe_code)]
//! Readiness-driven TCP actor: the live handle and all sessions remain on one
//! thread. A waker cancels even an idle listener without blocking socket readers.

use std::collections::{HashMap, VecDeque};
use std::io::{self, BufRead, Read, Write};
use std::net::{Shutdown, SocketAddr, TcpListener};
use std::sync::{mpsc, Arc, atomic::{AtomicBool, Ordering}};
use std::thread::{self, JoinHandle};
use std::time::{Duration, Instant};
use mio::{Events, Interest, Poll, Token, Waker};
use crate::messages::{hex_encode, Command, ErrFrame, RefusalCode, Welcome, PUBLIC_LINE_MAX_BYTES};
use crate::session::{ConnId, Server};
use crate::transport::GameDriver;
use crate::wire_v1 as w;
use crate::{admin, stream::Stream};
pub use crate::stream::StreamOptions;

// Reuse the existing peer HELLO budget for terminal draining:
// a dead bridge must not wait longer for a stalled peer than for its handshake.
const PEER_TIMEOUT: Duration = Duration::from_secs(5);

pub fn read_bounded_line<R: BufRead>(reader: &mut R) -> Result<Option<Vec<u8>>, RefusalCode> {
    let mut line = Vec::new();
    let mut cr_at_cap = false;
    loop {
        let bytes = reader.fill_buf().map_err(|_| RefusalCode::BadLine)?;
        if bytes.is_empty() {
            return if line.is_empty() { Ok(None) } else { Err(RefusalCode::BadLine) };
        }
        let mut consumed = 0;
        let mut complete = false;
        for byte in bytes {
            consumed += 1;
            if frame_byte(&mut line, &mut cr_at_cap, *byte)? {
                complete = true;
                break;
            }
        }
        reader.consume(consumed);
        if complete { return Ok(Some(line)); }
    }
}

/// One framing boundary shared by stdin and TCP. Keep the optional CR beyond
/// an exact-cap content buffer as a bit, never a cap+1 byte allocation.
fn frame_byte(line: &mut Vec<u8>, cr_at_cap: &mut bool, byte: u8) -> Result<bool, RefusalCode> {
    if byte == b'\n' {
        if !*cr_at_cap && line.last() == Some(&b'\r') { line.pop(); }
        *cr_at_cap = false;
        return Ok(true);
    }
    if *cr_at_cap { return Err(RefusalCode::LineTooLong); }
    if line.len() == PUBLIC_LINE_MAX_BYTES {
        if byte != b'\r' { return Err(RefusalCode::LineTooLong); }
        *cr_at_cap = true;
    } else {
        line.push(byte);
    }
    Ok(false)
}

#[derive(Clone)]
pub struct ShutdownHandle {
    stop: Arc<AtomicBool>,
    waker: Arc<Waker>,
}
impl ShutdownHandle {
    pub fn shutdown(&self) {
        self.stop.store(true, Ordering::Release);
        let _ = self.waker.wake();
    }
}

pub struct RunningServer {
    addr: SocketAddr,
    admin_addr: Option<SocketAddr>,
    shutdown: ShutdownHandle,
    thread: Option<JoinHandle<io::Result<()>>>,
}

impl RunningServer {
    pub fn start<D: GameDriver + Send + 'static>(listener: TcpListener, driver: D, limit: usize) -> io::Result<Self> {
        Self::start_with(listener, move || Ok(driver), limit)
    }
    /// Construct non-Send live driver handles on the actor thread, never transfer them.
    pub fn start_with<D: GameDriver + 'static, F>(listener: TcpListener, factory: F, limit: usize) -> io::Result<Self>
    where F: FnOnce() -> io::Result<D> + Send + 'static {
        Self::start_configured(listener, None, factory, limit, StreamOptions::default())
    }
    pub fn start_configured<D: GameDriver + 'static, F>(
        listener: TcpListener, admin: Option<TcpListener>, factory: F, limit: usize, options: StreamOptions,
    ) -> io::Result<Self>
    where F: FnOnce() -> io::Result<D> + Send + 'static {
        let started = Instant::now();
        Self::start_configured_with_clock(listener, admin, factory, limit, options,
            move || started.elapsed().as_millis() as u64)
    }

    /// The lease clock seam lets offline socket probes advance monotonic time
    /// without sleeping for the two-minute lease. Readiness still uses Instant.
    pub fn start_configured_with_clock<D: GameDriver + 'static, F, C>(
        listener: TcpListener, admin: Option<TcpListener>, factory: F, limit: usize, options: StreamOptions, clock: C,
    ) -> io::Result<Self>
    where F: FnOnce() -> io::Result<D> + Send + 'static, C: Fn() -> u64 + Send + 'static {
        if limit == 0 { return Err(io::Error::new(io::ErrorKind::InvalidInput, "connections must be positive")); }
        let admin_addr = admin.as_ref().map(TcpListener::local_addr).transpose()?;
        if admin_addr.is_some_and(|addr| !addr.ip().is_loopback()) {
            return Err(io::Error::new(io::ErrorKind::PermissionDenied, "admin listener must bind loopback"));
        }
        let addr = listener.local_addr()?;
        listener.set_nonblocking(true)?;
        let mut listener = mio::net::TcpListener::from_std(listener);
        let mut poll = Poll::new()?;
        poll.registry().register(&mut listener, Token(0), Interest::READABLE)?;
        let mut admin = admin.map(|listener| {
            listener.set_nonblocking(true)?;
            Ok::<_, io::Error>(mio::net::TcpListener::from_std(listener))
        }).transpose()?;
        if let Some(listener) = &mut admin { poll.registry().register(listener, Token(2), Interest::READABLE)?; }
        let shutdown = ShutdownHandle {
            stop: Arc::new(AtomicBool::new(false)),
            waker: Arc::new(Waker::new(poll.registry(), Token(1))?),
        };
        let stop = Arc::clone(&shutdown.stop);
        let (ready_tx, ready_rx) = mpsc::sync_channel(1);
        let thread = thread::spawn(move || {
            let mut driver = match factory() {
                Ok(driver) => driver,
                Err(error) => {
                    eprintln!("serve_report|attach_failure|{error}");
                    let _ = ready_tx.send(Err(io::Error::other(error.to_string())));
                    return Err(error);
                }
            };
            if let Err(error) = new_resume_token() {
                let _ = ready_tx.send(Err(io::Error::other(error.to_string())));
                return Err(error);
            }
            let _ = ready_tx.send(Ok(()));
            run_actor(&mut poll, &mut listener, admin.as_mut(), &stop, &mut driver, limit, (options, &clock))
        });
        match ready_rx.recv().map_err(io::Error::other)? {
            Ok(()) => Ok(Self { addr, admin_addr, shutdown, thread: Some(thread) }),
            Err(error) => { let _ = thread.join(); Err(error) }
        }
    }
    pub fn local_addr(&self) -> SocketAddr { self.addr }
    pub fn admin_addr(&self) -> Option<SocketAddr> { self.admin_addr }
    pub fn shutdown_handle(&self) -> ShutdownHandle { self.shutdown.clone() }
    pub fn shutdown(mut self) -> io::Result<()> {
        self.shutdown.shutdown();
        self.finish()
    }
    pub fn wait(mut self) -> io::Result<()> { self.finish() }
    fn finish(&mut self) -> io::Result<()> {
        match self.thread.take() {
            Some(thread) => thread.join().map_err(|_| io::Error::other("server actor panicked"))?,
            None => Ok(()),
        }
    }
}
impl Drop for RunningServer {
    fn drop(&mut self) { self.shutdown.shutdown(); let _ = self.finish(); }
}

struct Peer {
    socket: mio::net::TcpStream,
    input: Vec<u8>,
    cr_at_cap: bool,
    output: Vec<u8>,
    written: usize,
    welcomed: bool,
    close: bool,
    hello_deadline: Instant,
    admin: bool,
    relay: bool,
    remote: SocketAddr,
    last_seq: u64,
    presentation_sent: u64,
    presentation_lines: u64,
    presentation_attempt: u64,
    presentation_last_queued: u64,
    relay_gaps: u64,
    presentation_kinds: std::collections::BTreeMap<String, u64>,
    drops: u64,
    upload: Option<(String, usize, Vec<u8>)>,
    pending_output: VecDeque<PendingOutput>,
    bulk_output: Option<mpsc::Receiver<String>>,
    pending_bytes: usize,
    held_input: Vec<u8>,
    deferred_read: bool,
}
enum PendingOutput {
    Line(Vec<u8>),
    World { header: String, data: String, bulk_id: u64 },
}
impl Peer {
    fn queue(&mut self, bytes: &[u8]) {
        if self.bulk_output.is_some() || !self.pending_output.is_empty() {
            let mut line = bytes.to_vec();
            line.push(b'\n');
            self.pending_bytes += line.len();
            self.pending_output.push_back(PendingOutput::Line(line));
        } else {
            self.output.extend_from_slice(bytes);
            self.output.push(b'\n');
        }
    }
    fn has_output(&self) -> bool {
        !self.output.is_empty() || self.bulk_output.is_some() || !self.pending_output.is_empty()
    }
    fn refuse(&mut self, code: RefusalCode) {
        eprintln!("serve_report|refusal|remote={}|code={}", self.remote, code.as_str());
        self.queue(&err(0, code, "-", ""));
        self.close = true;
    }
    fn flush(&mut self, deadline: Option<Instant>) -> io::Result<()> {
        loop {
            if deadline.is_some_and(|deadline| Instant::now() >= deadline) { return Ok(()); }
            while self.written < self.output.len() {
                if deadline.is_some_and(|deadline| Instant::now() >= deadline) { return Ok(()); }
                match self.socket.write(&self.output[self.written..]) {
                    Ok(0) => return Err(io::ErrorKind::WriteZero.into()),
                    Ok(n) => self.written += n,
                    Err(error) if error.kind() == io::ErrorKind::WouldBlock => return Ok(()),
                    Err(error) if error.kind() == io::ErrorKind::Interrupted => {}
                    Err(error) => return Err(error),
                }
            }
            self.output.clear();
            self.written = 0;
            if let Some(rx) = &self.bulk_output {
                match rx.try_recv() {
                    Ok(chunk) => {
                        self.output.extend_from_slice(chunk.as_bytes());
                        self.output.push(b'\n');
                        continue;
                    }
                    Err(mpsc::TryRecvError::Empty) => return Ok(()),
                    Err(mpsc::TryRecvError::Disconnected) => self.bulk_output = None,
                }
            }
            match self.pending_output.pop_front() {
                Some(PendingOutput::Line(line)) => {
                    self.pending_bytes -= line.len();
                    self.output = line;
                }
                Some(PendingOutput::World { header, data, bulk_id }) => {
                    self.pending_bytes -= header.len() + 1;
                    let (tx, rx) = mpsc::sync_channel(1);
                    self.bulk_output = Some(rx);
                    self.output.extend_from_slice(header.as_bytes());
                    self.output.push(b'\n');
                    thread::spawn(move || {
                        let bytes = w::b64_decode(&data).expect("internal world base64");
                        let count = bytes.len().div_ceil(crate::world_save::WORLD_CHUNK_RAW_BYTES).max(1);
                        for index in 0..count {
                            let offset = index * crate::world_save::WORLD_CHUNK_RAW_BYTES;
                            let end = (offset + crate::world_save::WORLD_CHUNK_RAW_BYTES).min(bytes.len());
                            let line = format!("BULK|{bulk_id}|{index}|{count}|{}", w::b64_encode(&bytes[offset..end]));
                            if tx.send(line).is_err() { break; }
                        }
                    });
                }
                None => return Ok(()),
            }
        }
    }
}

fn run_actor<D: GameDriver>(
    poll: &mut Poll, listener: &mut mio::net::TcpListener, mut admin_listener: Option<&mut mio::net::TcpListener>,
    stop: &AtomicBool, driver: &mut D, limit: usize, config: (StreamOptions, &dyn Fn() -> u64),
) -> io::Result<()> {
    let (options, clock) = config;
    let mut events = Events::with_capacity(256);
    let mut peers = HashMap::<Token, Peer>::new();
    let mut server = Server::new(fresh_epoch());
    let mut next_token = 3usize;
    let mut stream = Stream::new(options);
    let mut next_pump = Instant::now();
    let mut report_index = 0;
    let mut presentation_gap_max = 0u64;
    let mut presentation_gap = crate::stream::JoinGap::default();
    let mut exit_deadline: Option<Instant> = None;
    while !stop.load(Ordering::Acquire)
        && (exit_deadline.is_none() || peers.values().any(|peer| !peer.admin && peer.has_output())) {
        if exit_deadline.is_some_and(|deadline| Instant::now() >= deadline) { break; }
        // This is a deadline wait, not a tick/poll interval. With no pending
        // hello deadline we block until readiness or explicit shutdown.
        let timeout = if let Some(deadline) = exit_deadline {
            Some(Duration::from_millis(10).min(deadline.saturating_duration_since(Instant::now())))
        } else { peers.values().filter(|p| !p.welcomed && !p.close)
            .map(|p| p.hello_deadline.saturating_duration_since(Instant::now()))
            .chain(std::iter::once(next_pump.saturating_duration_since(Instant::now()))).min() };
        match poll.poll(&mut events, timeout) {
            Ok(()) => {}
            Err(error) if error.kind() == io::ErrorKind::Interrupted => continue,
            Err(error) => return Err(error),
        }
        for event in events.iter() {
            if exit_deadline.is_some() && event.token() == Token(0) { continue; }
            match event.token() {
                Token(0) | Token(2) => loop {
                    let admin = event.token() == Token(2);
                    let accept = if admin {
                        admin_listener.as_deref_mut().expect("registered admin listener").accept()
                    } else { listener.accept() };
                    match accept {
                        Ok((mut socket, remote)) => {
                            socket.set_nodelay(true)?;
                            if peers.len() >= limit {
                                // Tiny named refusal is attempted once on an excess connection;
                                // it never allocates an additional peer or reader.
                                let _ = socket.write_all(b"ERR|0|bad_line|-|\n");
                                continue;
                            }
                            let token = Token(next_token);
                            next_token = next_token.checked_add(1).ok_or_else(|| io::Error::other("connection id space exhausted"))?;
                            poll.registry().register(&mut socket, token, Interest::READABLE)?;
                            server.connect(ConnId(token.0 as u64));
                            if admin { server.set_admin(ConnId(token.0 as u64)); }
                            peers.insert(token, Peer {
                                socket, input: Vec::new(), cr_at_cap: false, output: Vec::new(), written: 0,
                                welcomed: false, close: false,
                                hello_deadline: Instant::now() + PEER_TIMEOUT,
                                admin, relay: false, remote, last_seq: 0,
                                presentation_sent: 0, presentation_lines: 0, presentation_attempt: 0,
                                presentation_last_queued: 0, relay_gaps: 0, drops: 0, upload: None,
                                presentation_kinds: std::collections::BTreeMap::new(),
                                pending_output: VecDeque::new(), bulk_output: None, pending_bytes: 0,
                                held_input: Vec::new(), deferred_read: false,
                            });
                        }
                        Err(error) if error.kind() == io::ErrorKind::WouldBlock => break,
                        Err(error) if error.kind() == io::ErrorKind::Interrupted => {}
                        Err(error) => return Err(error),
                    }
                },
                Token(1) => {}
                token => {
                    let diagnostics = front_door_state(&server, &peers, &stream, presentation_gap_max, driver);
                    let presentation = presentation_state(&server, &peers);
                    if let Some(peer) = peers.get_mut(&token) {
                        if event.is_readable() && !peer.close && !peer.has_output()
                            && !driver.baseline_pending(ConnId(token.0 as u64))
                            && (exit_deadline.is_none() || peer.admin) {
                            let context = ReadContext { stream: &mut stream, stop, diagnostics: &diagnostics, presentation: &presentation,
                                now_ms: clock(), exit_deadline };
                            if let Err(error) = read_peer(peer, ConnId(token.0 as u64), &mut server, driver, context) {
                                eprintln!("client {}: {error}", token.0);
                                peer.close = true;
                            }
                        }
                    }
                }
            }
        }
        if exit_deadline.is_none() && Instant::now() >= next_pump {
            next_pump = Instant::now() + Duration::from_millis(10);
            let diagnostics = front_door_state(&server, &peers, &stream, presentation_gap_max, driver);
            let presentation = presentation_state(&server, &peers);
            for (token, peer) in &mut peers {
                let id = ConnId(token.0 as u64);
                if peer.deferred_read && !peer.close && !peer.has_output() && !driver.baseline_pending(id) {
                    peer.deferred_read = false;
                    let context = ReadContext { stream: &mut stream, stop, diagnostics: &diagnostics,
                        presentation: &presentation, now_ms: clock(), exit_deadline };
                    if let Err(error) = read_peer(peer, id, &mut server, driver, context) {
                        eprintln!("client {}: {error}", token.0);
                        peer.close = true;
                    }
                }
            }
            let joining = stream.joining();
            let pump_start_ms = clock();
            let routed_events = match stream.pump(pump_start_ms, &mut server, driver) {
                Ok(events) => events,
                Err(error) => {
                    eprintln!("serve_report|bridge_failure|{error}");
                    if error.0.contains("process_exited") {
                        exit_deadline = Some(Instant::now() + PEER_TIMEOUT);
                        stream.terminal_events(driver, &error)
                    } else { stream.take_events(driver) }
                }
            };
            // Card 10 measures stalled pump windows, not time between sparse
            // recorder events. Empty pumps still advance the observation.
            let pump_end_ms = clock();
            let gap = presentation_gap.observe(pump_end_ms, joining
                && routed_events.iter().any(|event| event.event.kind == "SimAnimation"),
                stream.options.presentation_poll_ms);
            if cfg!(feature = "join-gap-trace") && gap > 1 {
                eprintln!("serve_report|presentation_join_gap|windows={gap}|pump_ms={}|joined={}|start_ms={pump_start_ms}|end_ms={pump_end_ms}",
                    pump_end_ms.saturating_sub(pump_start_ms), server.joined_connections().len());
            }
            presentation_gap_max = presentation_gap.max_during_join;
            for mut routed in routed_events {
                let mut world_data = if routed.event.kind == "WorldFile" { routed.event.fields.pop() } else { None };
                for id in crate::routing::recipients(&server, &routed) {
                    if matches!(routed.event.kind.as_str(), "SnapshotDelta" | "SimAnimation") && driver.baseline_pending(id) {
                        continue;
                    }
                    let Some(seq) = server.next_event_seq(id) else {
                        if let Some(peer) = peers.get_mut(&Token(id.0 as usize)) { peer.close = true; }
                        continue;
                    };
                    let mut event = routed.event.clone();
                    event.seq = seq;
                    let epoch = server.epoch;
                    let conn = server.connection_mut(id).expect("event recipient");
                    let world_bulk = if world_data.is_some() {
                        let bulk_id = conn.next_bulk;
                        conn.next_bulk = bulk_id.checked_add(1).ok_or_else(|| io::Error::other("bulk id exhausted"))?;
                        event.fields.push(crate::messages::enc_bulk_ref(bulk_id));
                        Some(bulk_id)
                    } else { None };
                    let lines = if world_bulk.is_some() { vec![w::encode_event(&event).map_err(io::Error::other)?] }
                        else { encode_event_lines(&event, &mut conn.next_bulk)? };
                    conn.ring.append(epoch, seq, lines[0].clone(), event_names_bulk(&lines[0]));
                    let Some(peer) = peers.get_mut(&Token(id.0 as usize)).filter(|p| p.welcomed && !p.close) else { continue; };
                    peer.last_seq = seq;
                    if event.kind == "SimAnimation" {
                        peer.presentation_attempt += 1;
                    }
                    if peer.output.len() - peer.written + peer.pending_bytes > 16 * crate::messages::PUBLIC_LINE_MAX_BYTES {
                        peer.drops += 1;
                        peer.close = true;
                        eprintln!("serve_report|drop|remote={}|reason=outbound_queue_full", peer.remote);
                        continue;
                    }
                    if let Some(bulk_id) = world_bulk {
                        peer.pending_bytes += lines[0].len() + 1;
                        peer.pending_output.push_back(PendingOutput::World {
                            header: lines.into_iter().next().expect("world header"),
                            data: world_data.take().expect("requester-only world data"), bulk_id,
                        });
                    } else {
                        for line in lines { peer.queue(line.as_bytes()); }
                    }
                    if event.kind == "CommandRejected" && driver.baseline_failed(id) { peer.close = true; }
                    if event.kind == "SimAnimation" {
                        let gap = peer.presentation_attempt - peer.presentation_last_queued;
                        peer.relay_gaps += gap.saturating_sub(1);
                        peer.presentation_last_queued = peer.presentation_attempt;
                        peer.presentation_sent += 1;
                        peer.presentation_lines += event.fields.first().and_then(|s| crate::messages::dec_list(s).ok()).map_or(0, |v| v.len() as u64);
                        if let Some(lines) = event.fields.first().and_then(|s| crate::messages::dec_list(s).ok()) {
                            for hex in lines {
                                if let Ok(line) = crate::messages::hex_decode(&hex) {
                                    if let Some(kind) = line.split('|').nth(1).filter(|s| !s.is_empty()) {
                                        let key = if peer.presentation_kinds.contains_key(kind) || peer.presentation_kinds.len() < 64 { kind } else { "?" };
                                        *peer.presentation_kinds.entry(key.into()).or_default() += 1;
                                    }
                                }
                            }
                        }
                    }
                }
            }
            if exit_deadline.is_some() {
                // Keep writable readiness alive until every queued refusal has
                // reached its socket; do not call the dead bridge again.
                for peer in peers.values_mut() {
                    peer.close = !peer.admin;
                    peer.upload = None;
                }
            }
            for line in stream.ladder.report.iter().skip(report_index) { eprintln!("serve_report|{line}"); }
            report_index = stream.ladder.report.len();
            if exit_deadline.is_none() {
                for (id, player, reason) in server.expired_sessions(clock()) {
                    driver.release_player(player);
                    server.disconnect(id);
                    stream.left(id);
                    eprintln!("serve_report|lease_end|player={player}|reason={reason:?}");
                }
            }
        }
        let mut closed = Vec::new();
        for (token, peer) in &mut peers {
            if !peer.welcomed && !peer.close && Instant::now() >= peer.hello_deadline {
                peer.refuse(RefusalCode::BadLine);
            }
            if peer.flush(exit_deadline).is_err() || (peer.close && !peer.has_output()) {
                closed.push(*token);
            } else {
                // Backpressure: stop reading while any reply is waiting for a
                // writable socket, so a slow reader cannot grow the output queue.
                let interest = if peer.output.is_empty() { Interest::READABLE } else { Interest::WRITABLE };
                poll.registry().reregister(&mut peer.socket, *token, interest)?;
            }
        }
        for token in closed {
            if let Some(mut peer) = peers.remove(&token) {
                let id = ConnId(token.0 as u64);
                if driver.baseline_pending(id) {
                    if let Some(player) = server.connection(id).and_then(|connection| connection.player) { driver.release_player(player); }
                    driver.baseline_closed(id);
                    server.disconnect(id);
                    stream.left(id);
                } else if let Some(player) = server.connection(id).and_then(|connection| connection.player)
                    .filter(|player| server.active_connection(*player) == Some(id)) {
                    server.connection_mut(id).expect("active connection").ring
                        .mark_disconnected(peer.last_seq, clock());
                    eprintln!("serve_report|disconnected|player={player}|last_seq={}", peer.last_seq);
                } else {
                    server.disconnect(id);
                    stream.left(id);
                }
                let _ = poll.registry().deregister(&mut peer.socket);
                let _ = peer.socket.shutdown(Shutdown::Both);
            }
        }
    }
    if exit_deadline.is_some() {
        for peer in peers.values() {
            eprintln!("serve_report|drain_abandoned|remote={}|queued_bytes={}|bulk_pending={}|reason={}",
                peer.remote, peer.output.len() - peer.written + peer.pending_bytes, peer.bulk_output.is_some(),
                if stop.load(Ordering::Acquire) { "shutdown" } else { "deadline" });
        }
    }
    eprintln!("serve_report|QUIT|{}", front_door_state(&server, &peers, &stream, presentation_gap_max, driver).replace('\n', "|"));
    for line in &stream.report { eprintln!("serve_report|{line}"); }
    for peer in peers.values() { let _ = peer.socket.shutdown(Shutdown::Both); }
    Ok(())
}

struct ReadContext<'a> {
    stream: &'a mut Stream,
    stop: &'a AtomicBool,
    diagnostics: &'a str,
    presentation: &'a str,
    now_ms: u64,
    exit_deadline: Option<Instant>,
}
fn read_peer<D: GameDriver>(peer: &mut Peer, id: ConnId, server: &mut Server, driver: &mut D,
    context: ReadContext<'_>) -> io::Result<()> {
        let ReadContext { stream, stop, diagnostics, presentation, now_ms, exit_deadline } = context;
    let bridge_exited = exit_deadline.is_some();
    let mut chunk = [0u8; 8192];
    loop {
        if exit_deadline.is_some_and(|deadline| Instant::now() >= deadline) { return Ok(()); }
        let read = if peer.held_input.is_empty() {
            peer.socket.read(&mut chunk)
        } else {
            let count = peer.held_input.len();
            chunk[..count].copy_from_slice(&peer.held_input);
            peer.held_input.clear();
            Ok(count)
        };
        let count = match read {
            Ok(0) => {
                if peer.input.is_empty() { peer.close = true; } else { peer.refuse(RefusalCode::BadLine); }
                return Ok(());
            }
            Ok(n) => n,
            Err(error) if error.kind() == io::ErrorKind::WouldBlock => return Ok(()),
            Err(error) if error.kind() == io::ErrorKind::Interrupted => continue,
            Err(error) => return Err(error),
        };
        for (index, byte) in chunk[..count].iter().enumerate() {
            if peer.welcomed && !peer.relay && driver.baseline_pending(id) {
                peer.held_input.extend_from_slice(&chunk[index..count]);
                peer.deferred_read = true;
                return Ok(());
            }
            if let Some((_, length, bytes)) = &mut peer.upload {
                bytes.push(*byte);
                if bytes.len() == *length {
                    let (blob, _, bytes) = peer.upload.take().expect("complete upload");
                    let reply = driver.admin_upload(&blob, &bytes).unwrap_or_else(admin::error);
                    peer.queue(reply.as_bytes());
                }
                continue;
            }
            match frame_byte(&mut peer.input, &mut peer.cr_at_cap, *byte) {
                Ok(false) => continue,
                Err(code) => { peer.refuse(code); return Ok(()); }
                Ok(true) => {}
            }
            // Framing removed exactly one optional CR, including at the cap.
            if peer.input.contains(&b'\r') {
                if !peer.welcomed || peer.relay {
                    peer.refuse(RefusalCode::BadLine);
                    return Ok(());
                }
                let fields: Vec<_> = peer.input.split(|byte| *byte == b'|').collect();
                let corr = fields.get(1).and_then(|field| std::str::from_utf8(field).ok())
                    .and_then(|field| field.parse().ok()).unwrap_or(0);
                let verb = fields.get(2).and_then(|field| std::str::from_utf8(field).ok())
                    .filter(|field| crate::messages::is_identifier(field)).unwrap_or("-");
                peer.queue(&err(corr, RefusalCode::BadLine, verb, ""));
                peer.input.clear();
                peer.close = false;
                continue;
            }
                let (reply, close) = if peer.relay || (peer.admin && !peer.welcomed && !peer.input.starts_with(b"HELLO|")) {
                    peer.relay = true;
                    peer.welcomed = true;
                    let input = String::from_utf8(peer.input.clone()).map_err(io::Error::other)?;
                    if let Some(rest) = input.trim().strip_prefix("BULK_UPLOAD ").filter(|_| !bridge_exited) {
                        let tokens: Vec<_> = rest.split_whitespace().collect();
                        if let [blob, length] = tokens.as_slice() {
                            if let Some(length) = length.parse::<usize>().ok().filter(|n| *n > 0 && *n <= 524224) {
                                peer.upload = Some(((*blob).into(), length, Vec::with_capacity(length)));
                                peer.input.clear();
                                continue;
                            }
                        }
                        (admin::error("invalid BULK_UPLOAD (max 524224 bytes)").into_bytes(), false)
                    } else {
                        let reply = if input.trim().eq_ignore_ascii_case("commands") {
                            admin::commands_reply()
                        } else { match admin::parse_request(&input) {
                            Ok(request) => match request.name.as_str() {
                                "FRONT_DOOR_STATE" => admin::report(diagnostics),
                                "PRESENTATION_SENT" => admin::report(presentation),
                                "EVENTS_DROPPED" => admin::report(&driver.event_drop_state()),
                                "QUIT" => {
                                    stop.store(true, Ordering::Release);
                                    admin::report("QUIT")
                                }
                                _ if bridge_exited => admin::error("process_exited"),
                                _ => stream.admin_reply(&request)
                                    .unwrap_or_else(|| driver.admin_request(&request).unwrap_or_else(admin::error)),
                            },
                            Err(error) => admin::error(error),
                        }};
                        (reply.into_bytes(), false)
                    }
                } else if bridge_exited && peer.welcomed {
                    (err(0, RefusalCode::NotRun, "-", &hex_encode("process_exited")), false)
                } else if peer.welcomed {
                    dispatch(server, driver, id, &peer.input, stream)
                } else {
                    resume_hello(peer, id, server, driver, stream, now_ms)
                };
                if reply.starts_with(b"EV|") {
                    let epoch = server.epoch;
                    if let (Ok(line), Some(conn)) = (std::str::from_utf8(&reply), server.connection_mut(id)) {
                        let seq = line.split('|').nth(1).and_then(|s| s.parse().ok()).expect("encoded event seq");
                        conn.ring.append(epoch, seq, line.into(), event_names_bulk(line));
                        peer.last_seq = seq;
                    }
                }
                peer.queue(&reply);
                peer.deferred_read |= driver.baseline_pending(id);
                peer.input.clear();
                peer.close = close || driver.baseline_failed(id);
                if peer.close { return Ok(()); }
        }
        peer.flush(exit_deadline)?;
        if peer.has_output() { return Ok(()); }
    }
}

fn resume_hello<D: GameDriver>(peer: &mut Peer, id: ConnId, server: &mut Server,
    driver: &mut D, stream: &mut Stream, now_ms: u64) -> (Vec<u8>, bool) {
    let epoch = server.epoch;
    let (welcome, close) = hello(&peer.input, epoch, &mut peer.welcomed, peer.admin);
    if close { return (welcome, close); }
    let declaration = w::decode_hello(&w::split(&peer.input).expect("accepted hello")).expect("accepted hello");
    let Some(cap) = declaration.caps.get("resume") else { return (welcome, false); };
    let Some((resume_epoch, last_seq)) = crate::rejoin::parse_resume_cap(cap).filter(|_| !peer.admin) else {
        peer.welcomed = false;
        return (err(0, RefusalCode::BadLine, "HELLO", ""), true);
    };
    let mut reply = welcome;
    reply.push(b'\n');
    if let Some(old) = declaration.caps.get("resume_token").and_then(|token| server.disconnected_session(token)) {
        match server.resume_identified_session(old, id, resume_epoch, last_seq, now_ms) {
            crate::rejoin::ResumeOutcome::Ok { epoch, from_seq, replayed_lines } => {
                stream.rebound(old, id);
                reply.extend_from_slice(format!("RESUME_OK|{epoch}|{from_seq}|{}", replayed_lines.len()).as_bytes());
                for line in replayed_lines {
                    reply.push(b'\n');
                    reply.extend_from_slice(line.as_bytes());
                }
                peer.last_seq = server.connection(id).expect("resumed").ring.last_seq();
                return (reply, false);
            }
            crate::rejoin::ResumeOutcome::Fresh { reason, .. } => {
                let player = server.connection(old).expect("identified lease").player.expect("joined");
                driver.release_player(player);
                server.disconnect(old);
                stream.left(old);
                eprintln!("serve_report|lease_end|player={player}|reason={reason:?}");
            }
        }
    }
    reply.extend_from_slice(format!("RESUME_FRESH|{epoch}").as_bytes());
    (reply, false)
}

fn hello(raw: &[u8], epoch: u32, welcomed: &mut bool, admin: bool) -> (Vec<u8>, bool) {
    match w::split(raw) {
        Ok(_) => match w::judge_hello(raw).or_else(|error| {
            if admin && matches!(error, w::HelloRefusal::Rejected(w::HelloToken::MissingSimlinkRing | w::HelloToken::MissingWorldFromServer)) {
                let fields = w::split(raw).map_err(|_| w::HelloRefusal::BadLine)?;
                w::decode_hello(&fields).map_err(|_| w::HelloRefusal::BadLine)
            } else { Err(error) }
        }) {
            Ok(_) => {
                *welcomed = true;
                (w::encode_welcome(&Welcome { version: 1, epoch, server_caps: String::new() })
                    .expect("validated welcome").into_bytes(), false)
            }
            Err(w::HelloRefusal::Rejected(token)) => (
                err(0, RefusalCode::HelloRejected, "HELLO", &hex_encode(token.as_str())), true),
            Err(w::HelloRefusal::BadLine) => (err(0, RefusalCode::BadLine, "-", ""), true),
        },
        Err(_) => (err(0, RefusalCode::BadLine, "-", ""), true),
    }
}

fn err(corr: u64, code: RefusalCode, verb: &str, detail: &str) -> Vec<u8> {
    w::encode_err(&ErrFrame { corr, code, verb: verb.into(), hex_text: detail.into() })
        .expect("validated refusal").into_bytes()
}

fn dispatch<D: GameDriver>(server: &mut Server, driver: &mut D, id: ConnId, raw: &[u8], stream: &mut Stream) -> (Vec<u8>, bool) {
    let fields = match w::split(raw) {
        Ok(fields) => fields, Err(_) => return (err(0, RefusalCode::BadLine, "-", ""), false),
    };
    if fields.as_slice() == ["QUIT"] { return (b"ACK|0|Quit".to_vec(), true); }
    if fields.first().map(String::as_str) != Some("CMD") {
        return (err(0, RefusalCode::BadLine, "-", ""), false);
    }
    let cmd = match w::decode_command(&fields) {
        Ok(cmd) => cmd,
        Err(_) => {
            let corr = fields.get(1).and_then(|s| s.parse().ok()).unwrap_or(0);
            let verb = fields.get(2).filter(|s| crate::messages::is_identifier(s)).map_or("-", String::as_str);
            return (err(corr, RefusalCode::BadLine, verb, ""), false);
        }
    };
    let duplicate_join = cmd.verb == "JoinSession" && server.connection(id).is_some_and(|c| c.player.is_some());
    let admin = server.connection(id).is_some_and(|connection| connection.admin);
    let seq = server.connection(id).map_or(0, |connection| connection.next_seq);
    let requester_id = server.connection(id).map_or(id, |connection| connection.event_id);
    let tick = stream.last_tick.max(0) as u64;
    let mut driver_failure = None;
    let result = server.handle_command(id, &cmd, |cmd: &Command| {
        if admin && cmd.verb == "JoinSession" { return err(cmd.corr, RefusalCode::UnauthorizedClientInsertion, &cmd.verb, ""); }
        if duplicate_join { return err(cmd.corr, RefusalCode::BadLine, &cmd.verb, ""); }
        driver.adjudicate_for(requester_id, cmd).unwrap_or_else(|error| {
            eprintln!("serve_report|command_driver_failure|verb={}|{error}", cmd.verb);
            driver_failure = Some(error.0.clone());
            w::encode_event(&crate::messages::Event { seq, tick, corr: cmd.corr, kind: "CommandRejected".into(),
                fields: vec![cmd.command_id.to_string(), hex_encode(&error.0)] })
                .expect("validated command and encoded error").into_bytes()
        })
    });
    match result {
        Ok(mut outcome) => {
            if let Some(error) = driver_failure {
                stream.command_failed(id, cmd.corr, &error);
            }
            if !outcome.replayed && outcome.reply.starts_with(b"ACK|") {
                if cmd.verb == "JoinSession" && !admin {
                    let token = match new_resume_token() {
                        Ok(token) => token,
                        Err(error) => {
                            eprintln!("serve_report|token_entropy_failure|{error}");
                            return (err(cmd.corr, RefusalCode::NotRun, &cmd.verb, ""), true);
                        }
                    };
                    driver.release_player(cmd.player_id);
                    if let Some(old) = server.join(id, cmd.player_id) {
                        if server.connection(old).is_some_and(|connection| connection.ring.disconnected()) {
                            server.disconnect(old);
                            stream.left(old);
                        }
                    }
                    stream.joined(id);
                    outcome.reply = server.issue_resume_token(id, cmd.command_id, token);
                } else if cmd.verb == "LeaveSession" && !admin {
                    driver.release_player(cmd.player_id);
                    server.leave(id);
                    stream.left(id);
                } else if cmd.verb == "AckSnapshot" {
                    if let Some(tick) = cmd.args.first().and_then(|s| s.parse::<i64>().ok()) {
                        let target = if admin { server.active_connection(cmd.player_id) } else { Some(id) };
                        if let Some(conn) = target.and_then(|id| server.connection_mut(id)) { conn.cadence.ack(tick); }
                    }
                }
            }
            (outcome.reply, false)
        }
        Err(refusal) => (err(refusal.corr, refusal.code, &refusal.verb, ""), false),
    }
}

/// Safe std-only OS entropy. RandomState is a hash seed, not a credential RNG:
/// its thread-local keys are cached. Do not substitute time/counters or hashes.
fn new_resume_token() -> io::Result<String> {
    #[cfg(unix)]
    {
        let mut bytes = [0u8; 16];
        std::fs::File::open("/dev/urandom")?.read_exact(&mut bytes)?;
        Ok(bytes.iter().map(|byte| format!("{byte:02x}")).collect())
    }
    #[cfg(windows)]
    {
        use std::os::windows::process::CommandExt;
        struct Entropy {
            child: std::process::Child,
            output: io::BufReader<std::process::ChildStdout>,
        }
        impl Drop for Entropy {
            fn drop(&mut self) {
                let _ = self.child.kill();
                let _ = self.child.wait();
            }
        }
        thread_local! {
            static ENTROPY: std::cell::RefCell<Option<Entropy>> = const { std::cell::RefCell::new(None) };
        }
        ENTROPY.with(|slot| {
            let mut slot = slot.borrow_mut();
            if slot.is_none() {
                let executable = std::path::PathBuf::from(std::env::var_os("SystemRoot")
                    .ok_or_else(|| io::Error::other("SystemRoot missing"))?)
                    .join("System32/WindowsPowerShell/v1.0/powershell.exe");
                // One helper per actor thread; startup is primed before readiness.
                // Requests contain no credential or game data; EOF/Drop ends the helper.
                let mut child = std::process::Command::new(executable)
                    .args(["-NoLogo", "-NoProfile", "-NonInteractive", "-Command",
                        "$r=[System.Security.Cryptography.RandomNumberGenerator]::Create(); $b=New-Object byte[] 16; while($null -ne [Console]::ReadLine()) { $r.GetBytes($b); [Console]::WriteLine(([BitConverter]::ToString($b)).Replace('-','').ToLowerInvariant()) }; $r.Dispose()"])
                    .stdin(std::process::Stdio::piped()).stdout(std::process::Stdio::piped())
                    .stderr(std::process::Stdio::null()).creation_flags(0x0800_0000).spawn()?;
                let output = io::BufReader::new(child.stdout.take().expect("piped entropy"));
                *slot = Some(Entropy { child, output });
            }
            let source = slot.as_mut().expect("initialized entropy");
            let input = source.child.stdin.as_mut().expect("piped entropy");
            input.write_all(b"\n")?;
            input.flush()?;
            let mut line = String::new();
            source.output.read_line(&mut line)?;
            let issued = line.trim_end_matches(['\r', '\n']);
            if !w::valid_resume_token(issued) { return Err(io::Error::other("OS random generator failed")); }
            Ok(issued.to_owned())
        })
    }
}

pub fn fresh_epoch() -> u32 {
    use std::hash::{BuildHasher, Hasher};
    let mut hash = std::collections::hash_map::RandomState::new().build_hasher();
    hash.write_u32(std::process::id());
    hash.finish() as u32
}

pub fn event_names_bulk(line: &str) -> bool {
    let fields: Vec<_> = line.split('|').collect();
    let field = match fields.get(4).copied() {
        Some("FullSnapshot" | "SnapshotDelta") => fields.get(5),
        Some("WorldFile") => fields.get(10),
        _ => None,
    };
    field.is_some_and(|field| crate::messages::dec_bulk_ref(field).is_some())
}

/// Encode before recording so the history holds exactly the EV the socket sees.
pub fn encode_event_lines(event: &crate::messages::Event, next_bulk: &mut u64) -> io::Result<Vec<String>> {
    let mut event = event.clone();
    let mut bulk = Vec::new();
    // The introducing EV precedes its chunks (card 57 F.2).
    let world = event.kind == "WorldFile";
    let body_index = if world { 5 } else { 0 };
    if world || matches!(event.kind.as_str(), "FullSnapshot" | "SnapshotDelta") {
        if let Some(body) = event.fields.get_mut(body_index) {
            let id = *next_bulk;
            *next_bulk = id.checked_add(1).ok_or_else(|| io::Error::other("bulk id exhausted"))?;
            let bytes = if world { w::b64_decode(body).map_err(io::Error::other)? } else { body.as_bytes().to_vec() };
            let size = if world { crate::world_save::WORLD_CHUNK_RAW_BYTES } else { crate::messages::PUBLIC_BULK_CHUNK_RAW_BYTES };
            let chunks: Vec<_> = if bytes.is_empty() { vec![&[][..]] } else { bytes.chunks(size).collect() };
            for (index, bytes) in chunks.iter().enumerate() {
                let chunk = crate::messages::BulkChunk { bulk_id: id, index: index as u64, count: chunks.len() as u64, b64: w::b64_encode(bytes) };
                bulk.push(w::encode_bulk(&chunk).map_err(io::Error::other)?);
            }
            *body = crate::messages::enc_bulk_ref(id);
        }
    }
    let mut lines = vec![w::encode_event(&event).map_err(io::Error::other)?];
    lines.extend(bulk);
    Ok(lines)
}

fn front_door_state(server: &Server, peers: &HashMap<Token, Peer>, stream: &Stream, presentation_gap_max: u64,
    driver: &impl GameDriver) -> String {
    let mut rows = Vec::new();
    let mut drops = 0;
    for id in server.joined_connections() {
        let Some(peer) = peers.get(&Token(id.0 as usize)) else { continue; };
        let conn = server.connection(id).expect("joined");
        drops += peer.drops;
        rows.push(format!("client|{}|remote={}|queue={}|last_seq={}|ack_lag={}|drops={}|relay_gaps={}|history_trimmed={}",
            conn.player.expect("joined"), peer.remote, peer.output.len() - peer.written + peer.pending_bytes, peer.last_seq,
            conn.cadence.ack_tick.map_or(-1, |ack| crate::cadence::ack_lag(stream.last_tick, ack)),
            peer.drops, peer.relay_gaps, stream.history_trimmed.get(&id).copied().unwrap_or(0)));
    }
    let tick_drops = if server.joins_completed == 0 { "unmeasured" } else { "0" };
    let routing = driver.routing_state().map(|state| format!("|{state}")).unwrap_or_default();
    let epoch_dump = if stream.epoch_dump_limit_hits == 0 { String::new() }
        else { format!("|navigation_epoch_dump_limit_hits={}",stream.epoch_dump_limit_hits) };
    rows.push(format!("totals|joins_completed={}|tick_drops_during_join={tick_drops}|outbound_drops={drops}|presentation_sent_gap_max_during_join={presentation_gap_max}|{}{routing}{epoch_dump}",
        server.joins_completed, stream.ladder.status()));
    rows.join("\n")
}
fn presentation_state(server: &Server, peers: &HashMap<Token, Peer>) -> String {
    let mut rows: Vec<_> = server.joined_connections().iter().filter_map(|id| {
        let peer = peers.get(&Token(id.0 as usize))?;
        if peer.presentation_sent == 0 { return None; }
        let kinds = if peer.presentation_kinds.is_empty() { "-".into() } else {
            peer.presentation_kinds.iter().map(|(kind, count)| format!("{kind}:{count}")).collect::<Vec<_>>().join("/")
        };
        Some((server.connection(*id)?.player?, kinds))
    }).collect();
    rows.sort_by_key(|(player, _)| *player);
    if rows.is_empty() { return "-".into(); }
    rows.iter().map(|(player, kinds)| format!("p{player}={kinds}")).collect::<Vec<_>>().join(";")
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::transport::FakeDriver;

    #[test]
    fn epoch_dump_limit_counter_is_visible_in_existing_diagnostics_only_after_failure() {
        let server = Server::new(7);
        let driver = FakeDriver::new();
        let mut stream = Stream::new(StreamOptions::default());
        let peers = HashMap::new();
        let baseline = front_door_state(&server,&peers,&stream,0,&driver);
        assert!(!baseline.contains("navigation_epoch_dump_limit_hits"));
        stream.epoch_dump_limit_hits = 2;
        assert_eq!(front_door_state(&server,&peers,&stream,0,&driver),
            format!("{baseline}|navigation_epoch_dump_limit_hits=2"));
    }

    #[test]
    fn cold_join_removes_the_superseded_disconnected_connection_and_stream_state() {
        let mut server = Server::new(7);
        let mut driver = FakeDriver::new();
        let mut stream = Stream::new(StreamOptions::default());
        let old = ConnId(1);
        let new = ConnId(2);
        server.connect(old);
        server.join(old, 7);
        server.connection_mut(old).unwrap().ring.mark_disconnected(0, 100);
        stream.joined(old);
        stream.history_trimmed.insert(old, 3);
        server.connect(new);
        let (reply, close) = dispatch(&mut server, &mut driver, new, b"CMD|1|JoinSession|1|7|61|~", &mut stream);
        assert!(reply.starts_with(b"ACK|1|JoinSession|ok|resume_token="));
        assert!(!close);
        assert!(server.connection(old).is_none());
        assert_eq!(server.active_connection(7), Some(new));
        assert!(!stream.history_trimmed.contains_key(&old));
        stream.left(new);
        assert!(!stream.joining());
    }

    fn test_peer(raw: &[u8]) -> (Peer, std::net::TcpStream) {
        let listener = TcpListener::bind("127.0.0.1:0").unwrap();
        let client = std::net::TcpStream::connect(listener.local_addr().unwrap()).unwrap();
        client.set_read_timeout(Some(Duration::from_secs(10))).unwrap();
        let (socket, remote) = listener.accept().unwrap();
        socket.set_nonblocking(true).unwrap();
        (Peer {
            socket: mio::net::TcpStream::from_std(socket), input: raw.into(),
            cr_at_cap: false, output: vec![], written: 0, welcomed: false, close: false,
            hello_deadline: Instant::now(), admin: false, relay: false, remote, last_seq: 0,
            presentation_sent: 0, presentation_lines: 0, presentation_attempt: 0, presentation_last_queued: 0,
            relay_gaps: 0, presentation_kinds: std::collections::BTreeMap::new(),
            drops: 0, upload: None,
            pending_output: VecDeque::new(), bulk_output: None, pending_bytes: 0,
            held_input: Vec::new(), deferred_read: false,
        }, client)
    }

    #[test]
    fn card59_bulk_wait_never_blocks_the_actor_or_overtakes_deferred_lines() {
        let (mut peer, client) = test_peer(b"");
        let (tx, rx) = mpsc::channel();
        peer.bulk_output = Some(rx);
        peer.queue(b"ACK|1|RequestWorld");
        // The producer has not been released. flush must return immediately,
        // leaving the following event behind the bulk rather than sending it.
        peer.flush(None).unwrap();
        assert!(peer.output.is_empty());
        assert!(peer.has_output());
        assert!(peer.pending_bytes > 0);
        tx.send("BULK|1|0|1|".into()).unwrap();
        drop(tx);
        peer.flush(None).unwrap();
        let mut client = std::io::BufReader::new(client);
        let mut line = String::new();
        client.read_line(&mut line).unwrap();
        assert_eq!(line, "BULK|1|0|1|\n");
        line.clear();
        client.read_line(&mut line).unwrap();
        assert_eq!(line, "ACK|1|RequestWorld\n");
        assert!(!peer.has_output());
        assert_eq!(peer.pending_bytes, 0);
    }

    #[test]
    fn card59_cursor_only_hello_never_selects_or_releases_a_players_session() {
        for cap in [
            "role=client,resume=123.1",
            "role=client2,player=7,resume=123.1",
            "role=client,resume=122.1",
        ] {
            let mut server = Server::new(123);
            let mut driver = crate::BridgeGameDriver::new(crate::bridge_driver::OfflineBridge);
            server.connect(ConnId(1));
            server.join(ConnId(1), 7);
            let conn = server.connection_mut(ConnId(1)).unwrap();
            conn.ring.append(123, 1, "EV|1|10|0|SimStateChanged|55".into(), false);
            conn.ring.mark_disconnected(1, 100);
            conn.ring.append(123, 2, "EV|2|10|0|SimStateChanged|55".into(), false);
            conn.next_seq = 3;
            driver.adjudicate(&Command { corr: 1, command_id: 1, player_id: 7, verb: "ClaimSim".into(), args: vec!["55".into()] }).unwrap();
            server.connect(ConnId(2));
            let (mut peer, _client) = test_peer(format!("HELLO|1|ring=1,wfs=1,{cap}").as_bytes());
            let (reply, close) = resume_hello(&mut peer, ConnId(2), &mut server, &mut driver,
                &mut Stream::new(StreamOptions::default()), 101);
            assert!(!close);
            let reply = String::from_utf8(reply).unwrap();
            assert!(reply.starts_with("WELCOME|1|123|\n"));
            assert!(reply.ends_with("RESUME_FRESH|123"));
            assert_eq!(server.connection(ConnId(2)).unwrap().player, None);
            assert_eq!(server.active_connection(7), Some(ConnId(1)));
            assert_eq!(driver.owner(55), Some(7));
            assert_eq!(server.connection(ConnId(1)).unwrap().next_seq, 3);
        }
    }

    #[test]
    fn token_ack_replay_leave_supersede_and_epoch_invalidation() {
        let mut server = Server::new(7);
        let mut driver = FakeDriver::new();
        let mut stream = Stream::new(StreamOptions::default());
        let id = ConnId(1);
        server.connect(id);
        let raw = b"CMD|1|JoinSession|1|7|61|~";
        let first = dispatch(&mut server, &mut driver, id, raw, &mut stream).0;
        assert_eq!(dispatch(&mut server, &mut driver, id, raw, &mut stream).0, first);
        assert_eq!(driver.sent_count(), 1);
        let issued = String::from_utf8(first).unwrap().split("resume_token=").nth(1).unwrap().to_owned();
        assert!(w::valid_resume_token(&issued));
        assert_eq!(server.disconnected_session(&issued), None, "active socket cannot be taken over");
        server.connection_mut(id).unwrap().ring.mark_disconnected(0, 100);
        assert_eq!(server.disconnected_session(&issued), Some(id));
        for bad in ["".to_owned(), "f".repeat(32), issued.to_uppercase(), format!("{issued}0")] {
            assert_eq!(server.disconnected_session(&bad), None);
        }
        server.leave(id);
        assert_eq!(server.disconnected_session(&issued), None);
        let second = dispatch(&mut server, &mut driver, id, b"CMD|2|JoinSession|2|7|61|~", &mut stream).0;
        let second = String::from_utf8(second).unwrap().split("resume_token=").nth(1).unwrap().to_owned();
        assert_ne!(issued, second);
        server.connection_mut(id).unwrap().ring.mark_disconnected(0, 100);
        assert_eq!(server.disconnected_session(&second), Some(id));
        server.connect(ConnId(2));
        server.join(ConnId(2), 7);
        assert_eq!(server.disconnected_session(&second), None, "cold join retires old credential");
        server.connect(ConnId(3));
        let third = dispatch(&mut server, &mut driver, ConnId(3), b"CMD|1|JoinSession|1|8|61|~", &mut stream).0;
        let third = String::from_utf8(third).unwrap().split("resume_token=").nth(1).unwrap().to_owned();
        server.connection_mut(ConnId(3)).unwrap().ring.mark_disconnected(0, 100);
        server.epoch = 8;
        assert_eq!(server.disconnected_session(&third), None);
    }

    #[test]
    fn b2_connection_seq_increases_across_leave_and_rejoin() {
        let mut server = Server::new(1);
        let mut driver = FakeDriver::new();
        let mut stream = Stream::new(StreamOptions::default());
        let id = ConnId(1);
        server.connect(id);
        assert!(dispatch(&mut server, &mut driver, id, b"CMD|1|JoinSession|1|7|61|~", &mut stream).0.starts_with(b"ACK|1|JoinSession|ok|resume_token="));
        let after_join = server.connection(id).unwrap().next_seq;
        assert_eq!(dispatch(&mut server, &mut driver, id, b"CMD|2|LeaveSession|2|7", &mut stream).0, b"ACK|2|LeaveSession|ok");
        let after_leave = server.connection(id).unwrap().next_seq;
        assert!(after_leave > after_join);
        assert!(dispatch(&mut server, &mut driver, id, b"CMD|3|JoinSession|3|7|61|~", &mut stream).0.starts_with(b"ACK|3|JoinSession|ok|resume_token="));
        assert!(server.connection(id).unwrap().next_seq > after_leave);
    }
}
