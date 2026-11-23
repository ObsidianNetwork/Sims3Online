#![forbid(unsafe_code)]
//! Card 57 §G: the front-door bind posture. The default binds loopback only; `--public` opts into
//! a non-loopback bind and prints the controller's `ExposureWarning` text. Also the gate-10
//! artifact `bind_posture.txt` (§H row 10, `non_loopback_default == 0`).

use std::net::{IpAddr, Ipv4Addr, SocketAddr, TcpListener};

use simlink_headless_server::bind::{
    bind_all, parse_endpoint, posture_from_args, unrecognized_args, BindError, BindPosture,
};

// ---- §G.2 the host parser: three forms, the controller's error text ----------------

#[test]
fn parses_the_three_forms() {
    assert_eq!(
        parse_endpoint("127.0.0.1:34019").unwrap(),
        SocketAddr::new(IpAddr::V4(Ipv4Addr::LOCALHOST), 34019)
    );
    assert_eq!(
        parse_endpoint("localhost:34019").unwrap(),
        SocketAddr::new(IpAddr::V4(Ipv4Addr::LOCALHOST), 34019)
    );
    assert_eq!(
        parse_endpoint("0.0.0.0:34019").unwrap(),
        SocketAddr::new(IpAddr::V4(Ipv4Addr::UNSPECIFIED), 34019)
    );
    // A private address form parses as an IP.
    assert_eq!(
        parse_endpoint("192.168.1.10:8080").unwrap(),
        SocketAddr::new("192.168.1.10".parse().unwrap(), 8080)
    );
    // An IPv6 bracket form.
    assert_eq!(
        parse_endpoint("[::1]:1234").unwrap(),
        SocketAddr::new("::1".parse().unwrap(), 1234)
    );
}

#[test]
fn parser_error_texts_match_the_controller() {
    assert_eq!(parse_endpoint(""), Err(BindError::Empty));
    assert_eq!(
        parse_endpoint("nocolon"),
        Err(BindError::NotInForm { value: "nocolon".into() })
    );
    assert_eq!(
        parse_endpoint("127.0.0.1:99999"),
        Err(BindError::BadPort { port: "99999".into() })
    );
    assert_eq!(
        parse_endpoint("not.an.host:80"),
        Err(BindError::NotAnAddress { host: "not.an.host".into() })
    );
    // The exact controller wording (§G.2).
    assert_eq!(
        BindError::Empty.to_string(),
        "Listener endpoint is empty."
    );
    assert_eq!(
        BindError::NotInForm { value: "x".into() }.to_string(),
        "Listener endpoint 'x' is not in <host>:<port> form."
    );
    assert_eq!(
        BindError::BadPort { port: "y".into() }.to_string(),
        "Listener endpoint port 'y' is not a valid TCP port."
    );
    assert!(BindError::NotAnAddress { host: "h".into() }
        .to_string()
        .starts_with("Listener host 'h' is not an IP address. Use 127.0.0.1"));
}

// ---- §G.1 loopback-only default ----------------------------------------------------

#[test]
fn default_posture_binds_loopback_only() {
    let p = BindPosture::default_loopback();
    assert!(p.public().is_loopback());
    assert_eq!(p.non_loopback_default(), 0);
    assert!(p.exposure_warning().is_none());
    for l in p.listeners() {
        assert!(l.is_loopback(), "default listener {l:?} is beyond loopback");
    }
}

#[test]
fn a_default_loopback_socket_really_binds_loopback() {
    // Offline-safe: an ephemeral loopback bind proves the default address binds. No LAN socket is
    // opened here (a non-loopback bind is only parsed, never bound).
    let p = BindPosture::default_loopback();
    let probe_addr = SocketAddr::new(p.public().addr.ip(), 0);
    let listener = TcpListener::bind(probe_addr).unwrap();
    assert!(listener.local_addr().unwrap().ip().is_loopback());
}

#[test]
fn bind_all_opens_a_real_loopback_socket() {
    // F4: `bind_all` binds each posture listener for real. Use an ephemeral loopback port (0) so the
    // test is offline-safe and never touches the default 34019 (the live stack may hold it). The
    // bound socket's own `local_addr` is loopback — §H row 10's "binds only loopback", by a real
    // bind rather than only the posture enum.
    let posture = posture_from_args(&["--public".into(), "127.0.0.1:0".into()]).unwrap();
    let bound = bind_all(&posture).unwrap();
    assert_eq!(bound.len(), posture.listeners().len());
    for listener in &bound {
        let addr = listener.local_addr().unwrap();
        assert!(addr.ip().is_loopback(), "bind_all opened a non-loopback socket: {addr}");
        assert_ne!(addr.port(), 0, "the OS should have assigned an ephemeral port");
    }
}

#[test]
fn unrecognized_args_names_an_ignored_flag() {
    // N8: the front door names what it ignores instead of silently taking the loopback default. The
    // controller's flag is `--listen`, so here it is reported as two unrecognized tokens.
    assert_eq!(
        unrecognized_args(&["--listen".into(), "192.168.1.5:34019".into()]),
        vec!["--listen".to_string(), "192.168.1.5:34019".to_string()]
    );
    // `--public` and its value are understood, so nothing is reported for them.
    assert!(unrecognized_args(&["--public".into(), "0.0.0.0:34019".into()]).is_empty());
    // No args, nothing ignored.
    assert!(unrecognized_args(&[]).is_empty());
    assert_eq!(
        unrecognized_args(&["--public".into(), "127.0.0.1:0".into(), "--public".into(), "0.0.0.0:9".into()]),
        vec!["--public".to_string(), "0.0.0.0:9".to_string()]
    );
}

#[test]
fn public_flag_moves_the_bind_and_names_the_warning() {
    // 0.0.0.0 is a non-loopback bind: only parsed, never bound here.
    let p = posture_from_args(&["--public".into(), "0.0.0.0:34019".into()]).unwrap();
    assert!(!p.public().is_loopback());
    assert_eq!(p.non_loopback_default(), 1);
    let warning = p.exposure_warning().unwrap();
    assert!(warning.starts_with("WARNING: the public client listener accepts connections from other machines (0.0.0.0:34019)."));
    assert!(warning.contains("no authentication"));
    assert!(warning.contains("SECURITY.md"));
    assert!(warning.contains("never forward this port on a router."));
}

#[test]
fn public_loopback_declaration_stays_loopback() {
    // --public that names a loopback address still yields no exposure warning (§G.1).
    let p = posture_from_args(&["--public".into(), "127.0.0.1:34019".into()]).unwrap();
    assert!(p.public().is_loopback());
    assert_eq!(p.non_loopback_default(), 0);
    assert!(p.exposure_warning().is_none());
}

#[test]
fn args_without_public_are_the_default() {
    assert_eq!(
        posture_from_args(&[]).unwrap(),
        BindPosture::default_loopback()
    );
    assert_eq!(
        posture_from_args(&["--other".into(), "x".into()]).unwrap(),
        BindPosture::default_loopback()
    );
    assert_eq!(
        posture_from_args(&["--public".into()]),
        Err(BindError::MissingPort)
    );
}

// ---- gate 10 artifact: bind_posture.txt --------------------------------------------

#[test]
fn emit_bind_posture_artifact() {
    let Some(dir) = std::env::var_os("CARD57_ARTIFACT_DIR") else {
        return;
    };
    let default = BindPosture::default_loopback();
    let public = posture_from_args(&["--public".into(), "0.0.0.0:34019".into()]).unwrap();
    // Bind a loopback listener on an ephemeral port for real (F4) — never the default 34019, which
    // the live stack may hold — so the artifact records a bound socket, not only the posture enum.
    let bind_probe = posture_from_args(&["--public".into(), "127.0.0.1:0".into()]).unwrap();

    let mut rows = Vec::new();
    for l in default.listeners() {
        rows.push(format!(
            "listener role={} addr={} loopback={}",
            l.role,
            l.addr,
            l.is_loopback()
        ));
    }
    let bound = bind_all(&bind_probe).expect("bind_all of a loopback:0 posture");
    for (listener, l) in bound.iter().zip(bind_probe.listeners()) {
        let addr = listener.local_addr().expect("bound socket local_addr");
        rows.push(format!(
            "bound_socket role={} intended_addr={} bound_addr={} loopback={}",
            l.role,
            l.addr,
            addr,
            addr.ip().is_loopback()
        ));
    }
    rows.push(format!(
        "default non_loopback_default={}",
        default.non_loopback_default()
    ));
    rows.push(format!(
        "public_with_flag exposure_warning_present={} non_loopback={}",
        public.exposure_warning().is_some(),
        public.non_loopback_default()
    ));
    // The §H row-10 pass-bar line, and the §K scorecard field, both read non_loopback_default.
    rows.push(format!(
        "non_loopback_default={}",
        BindPosture::default_loopback().non_loopback_default()
    ));

    std::fs::create_dir_all(&dir).unwrap();
    let path = std::path::Path::new(&dir).join("bind_posture.txt");
    std::fs::write(&path, format!("{}\n", rows.join("\n"))).expect("write bind_posture.txt");
}
