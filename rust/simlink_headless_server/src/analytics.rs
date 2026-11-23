#![forbid(unsafe_code)]
//! Card 49 B-E: read-only operator projection. Missing evidence stays unobserved.

use std::fmt::Write;

pub struct Panel {
    pub name: &'static str,
    pub source: &'static str,
    pub healthy: &'static str,
    pub alarm: &'static str,
}

pub const PANELS: [Panel; 6] = [
    Panel { name: "both_bridges_up", source: "bridge-health.py --client; stackctl.py status",
        healthy: "server + client1 bridges ready", alarm: "zombie client: boot-client-restart" },
    Panel { name: "authoritative_clock", source: "DIAG_STATE on :34029 (SimClock, inWorld)",
        healthy: "advancing tick, inWorld=True", alarm: "frozen tick: check controller liveness; stop-controller" },
    Panel { name: "sidecar_budgets", source: "records.json (card 39 A)",
        healthy: "latency / RSS / queue / handles / ring watermark within card 20/29 budgets",
        alarm: "budget breach: card 39 runbook C" },
    Panel { name: "rejections_by_name", source: "SIDECAR|tick|cmd|verdict log",
        healthy: "expected named counts, none silent", alarm: "unexpected rejection spike: card 42 divergence check" },
    Panel { name: "crashes_exposure", source: "crash_catch.py uptime; crash_ledger.py",
        healthy: "0 crashes over the exposure window", alarm: "analyze dump and live change set" },
    Panel { name: "player_visible_desync", source: "sim_compare.py --clients 1,2,3",
        healthy: "all clients match server", alarm: "diverge: classify card 42 C" },
];

/// Collected by the operator's signal readers, not inferred from listener liveness.
pub struct Observation<'a> {
    pub panel: &'a str,
    pub signal: &'a str,
}

pub struct Reference {
    pub name: &'static str,
    pub commands: &'static str,
}

pub const RECOVERY_COMMANDS: [Reference; 3] = [
    Reference { name: "backup", commands: "AtomicSaveCommitter rotates target/ -> backup/ -> lastgood/ after verified commits (card 11). Before a risky step: copy <saves>/...SimsOnline/{target,backup,lastgood}/* -> <archive>/<stamp>/; preserve the whole rotation set." },
    Reference { name: "restore", commands: "Stop sidecar, controller, then server TS3. Copy <archive>/<stamp>/lastgood/* back to live target/. stackctl.py boot-server; client rejoins (card 32). Verify hash_state against the archived continuity report (card 28). Never hand-edit target/." },
    Reference { name: "runtime_rollback", commands: "One step RC N+1 -> N (card 47 D): uninstall using install-log.json; run verify.ps1 and reinstall N from rc-manifest(N). No rebuild and no multi-step rollback." },
];

pub struct IncidentStep {
    pub step: u8,
    pub incident: &'static str,
    pub action: &'static str,
}

pub const INCIDENT_STEPS: [IncidentStep; 8] = [
    IncidentStep { step: 1, incident: "single client desync",
        action: "boot-client-restart; keep server warm; do not tune a client timeout (card 39 C)" },
    IncidentStep { step: 2, incident: "bridge down, process alive",
        action: "stackctl.py status -> boot-client-restart; if controller hung and locking the DLL, stop-controller before rebuilding" },
    IncidentStep { step: 3, incident: "restart controller against warm TS3",
        action: "stackctl.py reattach-server; DEV-104 DIAG_STATE re-attach; old mod warning at Program.cs:302" },
    IncidentStep { step: 4, incident: "interpreter crash",
        action: "crash_catch.py analyze dump; crash_ledger.py triage live change set; reattach-server/boot-server; retain dump for card 42 C" },
    IncidentStep { step: 5, incident: "save looks wrong",
        action: "restore last-good using the restore reference; never hand-edit target/" },
    IncidentStep { step: 6, incident: "sidecar latency or handles misbehaving",
        action: "restart loopback sidecar alone (card 38 B step 2); it holds no save handle; server/controller keep native authority (card 31 C)" },
    IncidentStep { step: 7, incident: "full teardown",
        action: "stackctl.py down; only stack-owned TS3, never an operator-started game" },
    IncidentStep { step: 8, incident: "cross-machine/WAN fault",
        action: "card 37 reconnect path, ring reclaim resume; persistent WAN breach is a documented limit, not a knob" },
];

pub const LIMITATIONS: [Reference; 4] = [
    Reference { name: "supported", commands: "Card 46 B gameplay list on TS3 build 1.67.2.024037 only." },
    Reference { name: "blocked_by_design", commands: "Clean rejections: client-side autonomy/interaction execution; client-spawned world services; reserved-slot double-takes; out-of-range funds; stale handles; joining without CLIENT_ENV." },
    Reference { name: "out_of_scope", commands: "Card 30 ledger: full native route/interaction reimplementation; native GC ownership; independently-authored DBPF byte-compat; renderer/animation/EP-content parity; public-internet hosting without a VPN." },
    Reference { name: "supervised", commands: "Bounded tester cohort; report issues using tester-receipts.jsonl fields (card 41 D) for card 42 C classification." },
];

pub fn render(observations: &[Observation<'_>]) -> String {
    let mut out = String::from("/analytics (read-only; card 49 B-E)\npanel | signal | source | healthy | alarm\n");
    for panel in &PANELS {
        let signal = observations.iter().find(|row| row.panel == panel.name)
            .map_or("unobserved", |row| row.signal);
        writeln!(out, "{} | {} | {} | {} | {}", panel.name, signal, panel.source, panel.healthy, panel.alarm)
            .expect("string write");
    }
    out.push_str("\nBackup / restore / runtime rollback (card 49 C)\n");
    for entry in &RECOVERY_COMMANDS {
        writeln!(out, "{}: {}", entry.name, entry.commands).expect("string write");
    }
    out.push_str("\nIncident recovery (card 49 D; operator reference, no actions executed)\n");
    for entry in &INCIDENT_STEPS {
        writeln!(out, "{}. {}: {}", entry.step, entry.incident, entry.action).expect("string write");
    }
    out.push_str("After every recovery step, record the outcome in records.json and the run folder (card 35 B).\n\nKnown limitations (card 30 ledger; card 49 E)\n");
    for entry in &LIMITATIONS {
        writeln!(out, "{}: {}", entry.name, entry.commands).expect("string write");
    }
    out
}
