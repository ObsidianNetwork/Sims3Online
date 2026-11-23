#![forbid(unsafe_code)]

pub mod common;

// Run the actual actor source with a test-only readiness-registration hook.
// The real sockets, protocol, session state, and clock implementation are used.
extern crate mio as real_mio;
extern crate self as mio;

pub use real_mio::{net, Events, Interest, Token};
pub use simlink_headless_server::*;
use std::cell::RefCell;
use std::io::{self, BufRead, BufReader, Write};
use std::net::{Shutdown, TcpListener, TcpStream};
use std::sync::{atomic::{AtomicU64, Ordering}, mpsc, Arc, Mutex};
use std::time::Duration;

#[path = "../src/serve.rs"]
pub mod actor;

struct DeregisterGate {
    entered: mpsc::Sender<()>,
    release: mpsc::Receiver<()>,
}

thread_local! {
    static NEXT_GATE: RefCell<Option<DeregisterGate>> = const { RefCell::new(None) };
}

pub struct Poll {
    inner: real_mio::Poll,
    gate: Mutex<Option<DeregisterGate>>,
}

impl Poll {
    pub fn new() -> io::Result<Self> {
        Ok(Self {
            inner: real_mio::Poll::new()?,
            gate: Mutex::new(NEXT_GATE.with(|gate| gate.borrow_mut().take())),
        })
    }

    pub fn registry(&self) -> Registry<'_> {
        Registry { inner: self.inner.registry(), gate: &self.gate }
    }

    pub fn poll(&mut self, events: &mut Events, timeout: Option<Duration>) -> io::Result<()> {
        self.inner.poll(events, timeout)
    }
}

pub struct Registry<'a> {
    inner: &'a real_mio::Registry,
    gate: &'a Mutex<Option<DeregisterGate>>,
}

impl Registry<'_> {
    pub fn register<S: real_mio::event::Source + ?Sized>(
        &self, source: &mut S, token: Token, interests: Interest,
    ) -> io::Result<()> {
        self.inner.register(source, token, interests)
    }

    pub fn reregister<S: real_mio::event::Source + ?Sized>(
        &self, source: &mut S, token: Token, interests: Interest,
    ) -> io::Result<()> {
        self.inner.reregister(source, token, interests)
    }

    pub fn deregister<S: real_mio::event::Source + ?Sized>(&self, source: &mut S) -> io::Result<()> {
        let gate = self.gate.lock().unwrap().take();
        if let Some(gate) = gate {
            gate.entered.send(()).map_err(io::Error::other)?;
            gate.release.recv_timeout(Duration::from_secs(10)).map_err(io::Error::other)?;
        }
        self.inner.deregister(source)
    }
}

pub struct Waker(real_mio::Waker);

impl Waker {
    pub fn new(registry: Registry<'_>, token: Token) -> io::Result<Self> {
        real_mio::Waker::new(registry.inner, token).map(Self)
    }

    pub fn wake(&self) -> io::Result<()> { self.0.wake() }
}

fn socket_line(peer: &mut BufReader<TcpStream>) -> Option<String> {
    let mut text = String::new();
    (peer.read_line(&mut text).unwrap() != 0).then(|| text.trim_end_matches(['\r', '\n']).to_owned())
}

#[test]
fn disconnect_clock_is_committed_before_socket_deregistration() {
    let (entered, at_deregister) = mpsc::channel();
    let (release, released) = mpsc::channel();
    NEXT_GATE.with(|gate| {
        assert!(gate.borrow_mut().replace(DeregisterGate { entered, release: released }).is_none());
    });
    let clock = Arc::new(AtomicU64::new(100));
    let actor_clock = Arc::clone(&clock);
    let server = actor::RunningServer::start_configured_with_clock(
        TcpListener::bind("127.0.0.1:0").unwrap(), None, || Ok(FakeDriver::new()), 8,
        actor::StreamOptions::default(), move || actor_clock.load(Ordering::SeqCst),
    ).unwrap();
    let stream = TcpStream::connect(server.local_addr()).unwrap();
    stream.set_read_timeout(Some(Duration::from_secs(10))).unwrap();
    stream.set_write_timeout(Some(Duration::from_secs(10))).unwrap();
    let mut peer = common::Peer::from(stream);
    let welcome = peer.exchange("HELLO|1|ring=1,wfs=1,role=client");
    let epoch = wire_v1::decode_welcome(&wire_v1::split(welcome.as_bytes()).unwrap()).unwrap().epoch;
    let reply = peer.exchange("CMD|1|JoinSession|1|7|61|~");
    assert!(reply.starts_with("ACK|1|JoinSession|"));
    let token = reply.split('|').find_map(|field| field.strip_prefix("resume_token=")).unwrap();
    assert!(wire_v1::valid_resume_token(token));

    peer.as_ref().shutdown(Shutdown::Write).unwrap();
    at_deregister.recv_timeout(Duration::from_secs(10)).unwrap();
    // This barrier is before socket deregistration/EOF in both implementations.
    // Only the fixed actor has already committed the disconnect timestamp.
    clock.store(100 + rejoin::RESUME_WINDOW_MAX_MS, Ordering::SeqCst);
    release.send(()).unwrap();
    for _ in peer {}

    let stream = TcpStream::connect(server.local_addr()).unwrap();
    stream.set_read_timeout(Some(Duration::from_secs(10))).unwrap();
    stream.set_write_timeout(Some(Duration::from_secs(10))).unwrap();
    let mut resumed = BufReader::new(stream);
    writeln!(resumed.get_mut(), "HELLO|1|ring=1,wfs=1,role=client,resume={epoch}.0,resume_token={token}").unwrap();
    assert!(socket_line(&mut resumed).unwrap().starts_with("WELCOME|1|"));
    let received = socket_line(&mut resumed).unwrap();
    server.shutdown().unwrap();
    eprintln!("disconnect_ordering|received={received}");
    assert_eq!(received, format!("RESUME_FRESH|{epoch}"));
}
