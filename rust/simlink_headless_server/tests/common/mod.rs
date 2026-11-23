#![forbid(unsafe_code)]

use std::collections::VecDeque;
use std::io::{BufRead, BufReader, Write};
use std::net::{SocketAddr, TcpListener, TcpStream};
use std::time::Duration;

use simlink_headless_server::bridge_driver::{BridgeGameDriver, OfflineBridge};
use simlink_headless_server::serve::RunningServer;

pub fn server() -> RunningServer {
    RunningServer::start(
        TcpListener::bind("127.0.0.1:0").unwrap(),
        BridgeGameDriver::new(OfflineBridge),
        32,
    ).unwrap()
}

pub struct Peer {
    reader: BufReader<TcpStream>,
    // Frames passed over while awaiting a reply, in their original wire order.
    pub pending: VecDeque<String>,
}

impl From<TcpStream> for Peer {
    fn from(stream: TcpStream) -> Self {
        Self { reader: BufReader::new(stream), pending: VecDeque::new() }
    }
}

impl AsRef<TcpStream> for Peer {
    fn as_ref(&self) -> &TcpStream { self.reader.get_ref() }
}

impl Iterator for Peer {
    type Item = String;

    fn next(&mut self) -> Option<Self::Item> {
        if let Some(line) = self.pending.pop_front() { return Some(line); }
        let mut line = String::new();
        (self.reader.read_line(&mut line).unwrap() != 0)
            .then(|| line.trim_end_matches(['\r', '\n']).to_owned())
    }
}

impl Peer {
    pub fn connect(server: &RunningServer) -> Self {
        Self::connect_to(server.local_addr())
    }

    pub fn connect_to(addr: SocketAddr) -> Self {
        let stream = TcpStream::connect(addr).unwrap();
        stream.set_read_timeout(Some(Duration::from_secs(10))).unwrap();
        stream.set_write_timeout(Some(Duration::from_secs(10))).unwrap();
        let mut peer = Self::from(stream);
        assert!(peer.exchange("HELLO|1|ring=1,wfs=1,role=client").starts_with("WELCOME|1|"));
        peer
    }

    pub fn exchange(&mut self, input: &str) -> String {
        let corr = input.strip_prefix("CMD|").and_then(|fields| fields.split('|').next())
            .and_then(|field| field.parse().ok()).unwrap_or(0);
        self.exchange_with_corr(input, corr)
    }

    // A command-id retry can replay the original reply, including its original corr.
    pub fn exchange_with_corr(&mut self, input: &str, corr: u64) -> String {
        eprintln!("request: {input}");
        writeln!(self.reader.get_mut(), "{input}").unwrap();
        let is_reply = |line: &str| {
            let mut fields = line.split('|');
            match fields.next() {
                Some("WELCOME") => input.starts_with("HELLO|"),
                Some("ACK" | "ERR") => fields.next().and_then(|field| field.parse().ok()) == Some(corr),
                _ => false,
            }
        };
        if let Some(index) = self.pending.iter().position(|line| is_reply(line)) {
            return self.pending.remove(index).unwrap();
        }
        loop {
            let mut line = String::new();
            assert_ne!(self.reader.read_line(&mut line).unwrap(), 0, "silent close for {input}");
            let line = line.trim_end_matches(['\r', '\n']).to_owned();
            eprintln!("received: {line}");
            if is_reply(&line) {
                return line;
            }
            self.pending.push_back(line);
        }
    }
}

pub fn artifact(name: &str, rows: &[String]) {
    if let Some(dir) = std::env::var_os("CARD57_ARTIFACT_DIR") {
        std::fs::create_dir_all(&dir).unwrap();
        std::fs::write(std::path::Path::new(&dir).join(name), format!("{}\n", rows.join("\n"))).unwrap();
    }
}
