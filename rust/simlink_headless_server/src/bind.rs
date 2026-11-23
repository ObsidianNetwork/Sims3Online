#![forbid(unsafe_code)]
//! The front-door bind posture (§G).
//!
//! Nothing binds beyond loopback unless told to (§G.1). `--public <host>:<port>` is the only flag
//! that opts in (§G.2, ruling 10: no new environment variable); on a non-loopback bind the server
//! prints the controller's `ExposureWarning` text (§G.2, `ClientListenerServer.cs:178-183`). The
//! public default port is [`DEFAULT_WAN_PORT`] (§G.4). The host parser reproduces the controller's
//! three forms with its error text (§G.2, `ClientListenerServer.ParseEndpoint`).
//!
//! This module resolves an endpoint, names the warning, and binds every listener for real
//! ([`bind_all`]), so the front door reports the address that actually opened rather than the one it
//! meant to use (F4). The offline bind-posture gate enumerates every default listener and proves
//! `non_loopback_default == 0`, and now proves the loopback bind itself (§H row 10). The accept
//! loop that keeps a bound listener resident (§G.3) lives in `crate::serve`.

use std::io;
use std::net::{IpAddr, SocketAddr, TcpListener};

use crate::messages::DEFAULT_WAN_PORT;

/// The loopback address the public listener binds by default (§G.1).
#[must_use]
pub const fn default_bind_host() -> IpAddr {
    IpAddr::V4(std::net::Ipv4Addr::LOCALHOST)
}

/// Why a `--public` / `--listen` endpoint string was refused, with the controller's own text
/// (§G.2).
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum BindError {
    /// `ClientListenerServer.ParseEndpoint`: the string is empty.
    Empty,
    /// The string is not `<host>:<port>`.
    NotInForm {
        /// The offending value.
        value: String,
    },
    /// The port is not a valid TCP port.
    BadPort {
        /// The port text.
        port: String,
    },
    /// The host is not an IP address.
    NotAnAddress {
        /// The host text.
        host: String,
    },
    /// A `--public` value was present but not parseable.
    MissingPort,
}

impl core::fmt::Display for BindError {
    fn fmt(&self, f: &mut core::fmt::Formatter<'_>) -> core::fmt::Result {
        match self {
            Self::Empty => f.write_str("Listener endpoint is empty."),
            Self::NotInForm { value } => {
                write!(f, "Listener endpoint '{value}' is not in <host>:<port> form.")
            }
            Self::BadPort { port } => {
                write!(f, "Listener endpoint port '{port}' is not a valid TCP port.")
            }
            Self::NotAnAddress { host } => write!(
                f,
                "Listener host '{host}' is not an IP address. Use 127.0.0.1 (this machine only), a private address, or 0.0.0.0."
            ),
            Self::MissingPort => f.write_str("--public requires <host>:<port>."),
        }
    }
}

impl std::error::Error for BindError {}

/// Parse an endpoint the way `ClientListenerServer.ParseEndpoint` does, accepting its three host
/// forms (`0.0.0.0`, `localhost`/`127.0.0.1`, a private address) with its error text (§G.2).
pub fn parse_endpoint(endpoint: &str) -> Result<SocketAddr, BindError> {
    if endpoint.trim().is_empty() {
        return Err(BindError::Empty);
    }
    let colon = endpoint.rfind(':');
    let split = match colon {
        Some(c) if c > 0 && c < endpoint.len() - 1 => c,
        _ => return Err(BindError::NotInForm { value: endpoint.to_string() }),
    };
    let host = &endpoint[..split];
    let port_text = &endpoint[split + 1..];
    let port = port_text
        .parse::<u16>()
        .map_err(|_| BindError::BadPort { port: port_text.to_string() })?;
    let addr = match host {
        "0.0.0.0" => IpAddr::V4(std::net::Ipv4Addr::UNSPECIFIED),
        "localhost" | "127.0.0.1" => IpAddr::V4(std::net::Ipv4Addr::LOCALHOST),
        other => other
            .trim_matches(|c| c == '[' || c == ']')
            .parse::<IpAddr>()
            .map_err(|_| BindError::NotAnAddress { host: host.to_string() })?,
    };
    Ok(SocketAddr::new(addr, port))
}

/// One resolved front-door listener and its loopback status.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Listener {
    /// A short role name (`public`).
    pub role: &'static str,
    /// The bound socket address.
    pub addr: SocketAddr,
}

impl Listener {
    /// Whether this listener is confined to loopback (§G.1).
    #[must_use]
    pub fn is_loopback(&self) -> bool {
        self.addr.ip().is_loopback()
    }
}

/// The front-door bind posture after argument parsing: the public listener, loopback by default,
/// moved only by `--public` (§G).
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct BindPosture {
    public: Listener,
}

impl BindPosture {
    /// The default posture: the public listener binds `127.0.0.1:DEFAULT_WAN_PORT` (§G.1, §G.4).
    #[must_use]
    pub fn default_loopback() -> Self {
        Self {
            public: Listener {
                role: "public",
                addr: SocketAddr::new(default_bind_host(), DEFAULT_WAN_PORT),
            },
        }
    }

    /// Resolve from a parsed `--public <host>:<port>` value. A non-loopback result is what makes
    /// [`Self::exposure_warning`] name itself (§G.2).
    pub fn with_public(public: &str) -> Result<Self, BindError> {
        let addr = parse_endpoint(public)?;
        Ok(Self {
            public: Listener { role: "public", addr },
        })
    }

    /// Every front-door listener, for the bind-posture enumeration. The card-58 admin listener is
    /// loopback-only and not owned by this crate (§G.5), so it is not a public listener here.
    #[must_use]
    pub fn listeners(&self) -> Vec<Listener> {
        vec![self.public.clone()]
    }

    /// The public listener.
    #[must_use]
    pub fn public(&self) -> &Listener {
        &self.public
    }

    /// How many listeners bind beyond loopback (§H row 10: `non_loopback_default`).
    #[must_use]
    pub fn non_loopback_default(&self) -> usize {
        self.listeners().iter().filter(|l| !l.is_loopback()).count()
    }

    /// The controller's `ExposureWarning` text when a listener binds beyond loopback, else `None`
    /// (§G.2, `ClientListenerServer.cs:178-183`). The protocol has no authentication (SECURITY.md).
    #[must_use]
    pub fn exposure_warning(&self) -> Option<String> {
        if self.public.is_loopback() {
            return None;
        }
        Some(format!(
            "WARNING: the public client listener accepts connections from other machines ({}). \
             The protocol has no authentication: anyone who can reach this port can join and \
             command Sims. Use a private network (see SECURITY.md) and never forward this port on a router.",
            self.public.addr
        ))
    }
}

/// Parse the front-door's argv into a [`BindPosture`]. `--public <host>:<port>` opts into a
/// non-loopback bind (§G.2); without it the posture is loopback-only (§G.1). Only `--public`
/// changes the bind; no environment variable does (ruling 10).
pub fn posture_from_args(args: &[String]) -> Result<BindPosture, BindError> {
    let mut iter = args.iter();
    while let Some(arg) = iter.next() {
        if arg == "--public" {
            let value = iter.next().ok_or(BindError::MissingPort)?;
            return BindPosture::with_public(value);
        }
    }
    Ok(BindPosture::default_loopback())
}

/// The argv tokens the front door does not understand (N8): anything that is neither `--public` nor
/// the value that follows it. The controller's flag is named `--listen` (`docs/protocol.md`,
/// `docs/configuration.md`), so an operator who types it here would otherwise get the loopback
/// default silently. `main` prints one line per returned token so the ignore is never silent.
#[must_use]
pub fn unrecognized_args(args: &[String]) -> Vec<String> {
    let mut out = Vec::new();
    let mut iter = args.iter();
    let mut public_seen = false;
    while let Some(arg) = iter.next() {
        if arg == "--public" {
            if public_seen {
                out.push(arg.clone());
                if let Some(value) = iter.next() { out.push(value.clone()); }
            } else {
                public_seen = true;
                iter.next();
            }
            continue;
        }
        if ["--serve", "--resident", "--fake-driver", "--bind-only", "--attach-ts3w", "--codec-stdio", "--enable-admin", "--trace-stdio"].contains(&arg.as_str()) {
            continue;
        }
        if ["--pid", "--connections", "--scorecard-dir", "--admin", "--resync-warn-ticks",
            "--resync-hard-ticks", "--resync-max-interval-seconds", "--sync-broadcast-seconds", "--card58-scorecard-dir",
            "--card59-scorecard-dir", "--save", "--checkpoint-save"].contains(&arg.as_str()) {
            iter.next();
            continue;
        }
        out.push(arg.clone());
    }
    out
}

/// Bind every front-door listener in the posture for real (§H row 10; F4). `main` calls this at
/// startup and prints each bound socket's `local_addr()`, so the operator is told the address that
/// actually opened, not the one the posture meant to use. With no `--public` each address is
/// loopback (§G.1), and a bind that the OS cannot honor (e.g. the port is already taken) is returned
/// to the caller to report — never swallowed. The listeners this returns are open only for the
/// caller's ownership of them: `crate::serve` takes the resident listener in serve mode.
pub fn bind_all(posture: &BindPosture) -> io::Result<Vec<TcpListener>> {
    posture.listeners().iter().map(|l| TcpListener::bind(l.addr)).collect()
}

/// The admin bind is independent of --public. No public endpoint can grant admin authority.
pub fn admin_from_args(args: &[String], resident: bool) -> Result<Option<SocketAddr>, String> {
    let explicit = args.iter().position(|s| s == "--admin");
    if let Some(i) = explicit {
        return crate::admin::resolve_endpoint(args.get(i + 1).ok_or("--admin needs a value")?).map(Some);
    }
    if resident || args.iter().any(|s| s == "--enable-admin") {
        return crate::admin::resolve_endpoint(crate::admin::DEFAULT_ENDPOINT).map(Some);
    }
    Ok(None)
}
