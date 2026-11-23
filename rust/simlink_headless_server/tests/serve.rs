#![forbid(unsafe_code)]

mod common;

use std::io::{BufRead, BufReader, Read, Write};
use std::net::TcpStream;
use std::time::Duration;
use simlink_headless_server::serve::read_bounded_line;
use simlink_headless_server::messages::PUBLIC_LINE_MAX_BYTES;

#[test]
fn bounded_reader_accepts_exact_cap_and_rejects_overflow_and_partial_eof() {
    let exact = [vec![b'x'; PUBLIC_LINE_MAX_BYTES], b"\n".to_vec()].concat();
    assert_eq!(read_bounded_line(&mut &exact[..]).unwrap().unwrap().len(), PUBLIC_LINE_MAX_BYTES);
    let oversized = vec![b'x'; PUBLIC_LINE_MAX_BYTES + 1];
    assert!(read_bounded_line(&mut &oversized[..]).is_err());
    assert!(read_bounded_line(&mut &b"partial"[..]).is_err());
}

#[test]
fn malformed_lines_are_named_before_close_and_client_bulk_is_refused() {
    let server = common::server();
    for (input, expected) in [
        (b"HELLO|2|ring=1,wfs=1\n".to_vec(), "ERR|0|hello_rejected|HELLO|76657273696f6e5f756e737570706f72746564"),
        ([vec![b'x'; PUBLIC_LINE_MAX_BYTES + 1], b"\n".to_vec()].concat(), "ERR|0|line_too_long|-|"),
        (b"\xff\n".to_vec(), "ERR|0|bad_line|-|"),
    ] {
        let mut socket = TcpStream::connect(server.local_addr()).unwrap();
        socket.set_read_timeout(Some(Duration::from_secs(10))).unwrap();
        socket.write_all(&input).unwrap();
        let mut reader = BufReader::new(socket);
        let mut line = String::new();
        reader.read_line(&mut line).unwrap();
        assert_eq!(line.trim_end(), expected);
        let mut rest = Vec::new();
        reader.read_to_end(&mut rest).unwrap();
        assert!(rest.is_empty());
    }
    let mut peer = common::Peer::connect(&server);
    assert_eq!(peer.exchange("BULK|1|0|1|AAAA"), "ERR|0|bad_line|-|");
    common::artifact("serve_probe.txt", &["bounded_reader=pass".into()]);
    server.shutdown().unwrap();
}
