#![forbid(unsafe_code)]
//! Ownership of native push configuration; the Stream still owns every IO.

#[derive(Debug)]
pub(crate) struct Control {
    pub requested: bool,
    pub manual_stop: bool,
    pub quarantine: bool,
    pub failure: Option<String>,
    pub generation: u64,
    pub off_pending: bool,
    pub off_ready: bool,
    pub retry_at: u64,
    pub may_push: bool,
    pub startup_discarded: u64,
    pub idle_discarded: u64,
    pub off_retries: u64,
    pub stale_receipts: u64,
    /// Count of native `sPushBehind` reports while the recorder ring lagged behind the push cursor.
    pub behind_reports: u64,
    epoch: Option<u64>,
    behind: Option<u64>,
    pub native_report_hex: String,
}
impl Control {
    pub fn new(requested: bool) -> Self {
        Self { requested, manual_stop: false, quarantine: requested, failure: None,
            generation: 0, off_pending: requested, off_ready: false, retry_at: 0,
            may_push: false, startup_discarded: 0, idle_discarded: 0,
            off_retries: 0, stale_receipts: 0, behind_reports: 0, epoch: None, behind: None, native_report_hex:"-".into() }
    }
    pub fn observe(&mut self, generation: u64) {
        if !self.requested || self.generation == generation { return; }
        self.generation = generation;
        if self.may_push || self.off_pending { self.stop(); }
    }
    pub fn epoch(&mut self, epoch: Option<u64>) {
        if self.requested && self.epoch != epoch {
            self.epoch = epoch;
            if epoch.is_some() { self.stop(); }
        }
    }
    pub fn stop(&mut self) {
        if !self.requested { return; }
        self.off_pending = true;
        self.off_ready = false;
        self.retry_at = 0;
    }
    pub fn manual(&mut self) {
        if !self.manual_stop { self.manual_stop = true; self.stop(); }
    }
    pub fn can_start(&self) -> bool {
        !self.requested || (!self.manual_stop && !self.off_pending && !self.quarantine && self.failure.is_none())
    }
    pub fn start(&mut self) { self.may_push = true; self.off_ready = false; }
    pub fn failed_off(&mut self, now: u64) {
        self.off_pending = true;
        self.off_retries = self.off_retries.saturating_add(1);
        self.retry_at = now.saturating_add(250);
    }
    pub fn acknowledge_off(&mut self, generation: u64, report: &str, now: u64) -> bool {
        if generation != self.generation {
            self.stale_receipts = self.stale_receipts.saturating_add(1);
            self.failed_off(now);
            return false;
        }
        if !push_ack(report, "off") { self.failed_off(now); return false; }
        // Stream drains the final ring prefix BEFORE it calls this method.
        self.off_pending = false;
        self.off_ready = true;
        self.may_push = false;
        self.retry_at = 0;
        true
    }
    /// True when this server's own scope cut or stop overtook an in-flight push-on.
    pub fn overtaken(&self, generation: u64) -> bool {
        self.requested && self.failure.is_none() && (generation != self.generation || self.off_pending || self.manual_stop)
    }
    pub fn acknowledge_on(&mut self, generation: u64, report: &str) -> bool {
        if generation == self.generation && self.can_start() && push_ack(report, "on") { return true; }
        self.stale_receipts = self.stale_receipts.saturating_add(u64::from(generation != self.generation));
        self.stop();
        false
    }
    pub fn native_status(&mut self, report: &str) -> bool {
        self.native_report_hex=crate::messages::hex_encode(&report.chars().take(2048).collect::<String>());
        let mut values=report.split(',').filter_map(|s|s.split_once('='))
            .filter(|(key,_)|*key=="behind").map(|(_,v)|v.parse::<u64>());
        let Some(Ok(behind))=values.next() else { self.fail("native push status missing behind counter");return false; };
        if values.next().is_some() {self.fail("native push status duplicate behind counter");return false;}
        // The game's recorder ring wrapped past the push cursor, so some lines are gone, as with a
        // slow dump. Exiting here would also cut both clients off; instead count it and take the
        // push through an acknowledged off, whose dump re-reads from the game before the re-arm.
        if let Some(before)=self.behind.filter(|before|behind!=*before) {
            self.behind_reports=self.behind_reports.saturating_add(behind.saturating_sub(before).max(1));
            eprintln!("serve_report|push_behind|behind={behind}|was={before}|total={}",self.behind_reports);
            self.stop();
        }
        self.behind=Some(behind);true
    }
    pub fn fail(&mut self, reason: &str) {
        self.failure.get_or_insert_with(|| reason.chars().take(512).collect());
        self.off_ready = false;
    }
    pub fn report(&self) -> String {
        format!("presentation_ring_managed={}|presentation_ring_generation={}|presentation_ring_quarantine={}|presentation_ring_off_pending={}|presentation_ring_off_ready={}|presentation_ring_manual_stop={}|presentation_ring_startup_discarded={}|presentation_ring_idle_discarded={}|presentation_ring_off_retries={}|presentation_ring_stale_receipts={}|presentation_ring_failure_hex={}|presentation_ring_native_status_hex={}|presentation_ring_behind_reports={}",
            u8::from(self.requested), self.generation, u8::from(self.quarantine), u8::from(self.off_pending),
            u8::from(self.off_ready), u8::from(self.manual_stop), self.startup_discarded, self.idle_discarded,
            self.off_retries, self.stale_receipts, self.failure.as_deref().map(crate::messages::hex_encode).unwrap_or_else(|| "-".into()), self.native_report_hex, self.behind_reports)
    }
}
/// DescribePush is comma-delimited. Never accept suffixes/duplicates or a refusal.
pub(crate) fn push_state(report: &str, expected: &str) -> bool {
    if reports_error(report) || report.contains("ERROR|") { return false; }
    let mut fields = report.split(',').filter_map(|part| part.trim().split_once('='))
        .filter(|(key, _)| *key == "push").map(|(_, value)| value);
    fields.next() == Some(expected) && fields.next().is_none()
}
/// Native DescribePush includes a coherent examined/source cursor pair.
/// A recorder reply carries its whole status text, whose sub-reports each end in
/// `error=-` while healthy. Only an `error=` with a value is a reported error.
/// Recorder verbs owned by the presentation ring transport (ring/recorder/route lifecycle,
/// push configuration, sidecar switching, cursor-bearing dumps/snapshots, and Rust route or
/// navigation mutations). Empty verbs are also refused.
pub(crate) fn recorder_transport_owned(verb: &str) -> bool {
    verb.is_empty()
        || matches!(
            verb,
            "on" | "off"
                | "reset"
                | "ring-on"
                | "ring-off"
                | "routes-on"
                | "routes-off"
                | "push-on"
                | "push-off"
                | "push-targets"
                | "push-every"
                | "pump-ms"
                | "sidecar-on"
                | "sidecar-off"
                | "sidecar-client-on"
                | "sidecar-client-off"
                | "sidecar-client-ring"
                | "sidecar-client-ring-off"
                | "dump"
                | "snapshot"
                | "snapshot-driver"
                | "rust-route-on"
                | "rust-route-off"
                | "rust-plan"
                | "rust-plan-none"
                | "rust-road-place-on"
                | "rust-road-place"
                | "rust-road-place-none"
                | "rust-road-place-decline"
                | "rust-navigation-sync"
                | "rust-navigation-result"
                | "rust-navigation-offer"
                | "rust-simnav-install"
        )
}

/// Recorder verbs not owned by the presentation ring transport (diagnostics such as `watch`,
/// `watch-dump`, `sidecar-status`, read-only inspection queries, and newly added sub-verbs)
/// pass through to the game mod while the transport owns the ring.
pub(crate) fn recorder_diagnostic(verb: &str) -> bool {
    !recorder_transport_owned(verb)
}

pub(crate) fn reports_error(report: &str) -> bool {
    report.match_indices("error=").any(|(at, key)| {
        let value = report[at + key.len()..].split([',', ';', '|', '\n']).next().unwrap_or("").trim();
        !(value.is_empty() || value == "-")
    })
}
pub(crate) fn push_ack(report:&str,expected:&str)->bool {
    if !push_state(report,expected){return false;}
    let number=|key:&str|->Option<i64>{
        let mut values=report.split(',').filter_map(|s|s.trim().split_once('='))
            .filter(|(name,_)|*name==key).map(|(_,s)|s.parse::<i64>().ok());
        let value=values.next()??;if values.next().is_some()||value<0{return None;}Some(value)
    };
    matches!((number("cursor"),number("sequence")),(Some(cursor),Some(sequence)) if cursor<=sequence)
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn a_scope_cut_during_push_on_is_overtaken_not_refused() {
        let mut ring = Control::new(true);
        ring.quarantine = false; ring.off_pending = false; ring.generation = 4;
        assert!(!ring.overtaken(4));
        ring.observe(5);
        assert!(ring.overtaken(4), "the newer scope overtook the request");
        ring.off_pending = false;
        assert!(!ring.overtaken(5));
        ring.stop();
        assert!(ring.overtaken(5), "a pending off barrier overtook the request");
        ring.fail("native refusal");
        assert!(!ring.overtaken(5), "a recorded failure is never an overtake");
    }

    #[test]
    fn transport_owned_recorder_controls_are_refused_while_diagnostics_pass_through() {
        for verb in [
            "watch",
            "watch-dump",
            "sidecar-status",
            "probe",
            "stalls",
            "peek",
            "actor",
            "local",
            "rust-navigation-status",
            "rust-route-plans",
            "custom-diag",
        ] {
            assert!(!recorder_transport_owned(verb), "{verb}");
            assert!(recorder_diagnostic(verb), "{verb}");
        }
        for verb in [
            "",
            "on",
            "off",
            "reset",
            "ring-on",
            "ring-off",
            "routes-on",
            "routes-off",
            "push-on",
            "push-off",
            "push-targets",
            "push-every",
            "pump-ms",
            "sidecar-on",
            "sidecar-off",
            "sidecar-client-on",
            "sidecar-client-off",
            "sidecar-client-ring",
            "sidecar-client-ring-off",
            "dump",
            "snapshot",
            "snapshot-driver",
            "rust-route-on",
            "rust-route-off",
            "rust-plan",
            "rust-plan-none",
            "rust-road-place-on",
            "rust-road-place",
            "rust-road-place-none",
            "rust-road-place-decline",
            "rust-navigation-sync",
            "rust-navigation-result",
            "rust-navigation-offer",
            "rust-simnav-install",
        ] {
            assert!(recorder_transport_owned(verb), "{verb}");
            assert!(!recorder_diagnostic(verb), "{verb}");
        }
    }

    #[test]
    fn healthy_status_error_placeholders_are_not_refusals() {
        assert!(!reports_error("recorder=on;ring=on;picks=on,ready=1,error=-;order=1,error=-;gcGate=off,error=-"));
        assert!(!reports_error("recorder=on;ring=on"));
        assert!(reports_error("recorder=on;ring=on;picks=on,error=patch failed;order=1,error=-"));
        assert!(reports_error("error=ring unavailable"));
    }
    #[test]
    fn exact_ack_and_old_scope_cannot_release_new_off_barrier() {
        let mut c=Control::new(true);
        for text in ["push=offish", "push=off,push=off", "push=off,error=bad", "push=refused"] {
            assert!(!c.acknowledge_off(0,text,0)); assert!(c.off_pending);
        }
        c.observe(2);
        assert!(!c.acknowledge_off(0,"push=off,cursor=9,sequence=9",1));
        assert!(c.acknowledge_off(2,"push=off,cursor=9,sequence=9",300));
        c.quarantine=false;c.start();c.manual();
        assert!(!c.acknowledge_on(2,"push=on"));assert!(!c.off_ready);
        assert!(c.acknowledge_off(2,"push=off,cursor=9,sequence=9",301));assert!(c.off_ready);
        assert!(!c.can_start());
    }
}

/// Validate framing only. Payload records remain opaque and retain their source bytes.
pub(crate) fn valid_page(page:&str)->bool { page_fault(page).is_none() }

/// Why a page's framing is refused: the rule broken and the start of the line that broke it.
pub(crate) fn page_fault(page:&str)->Option<String> {
    let mut lines=page.split('\n');
    let header=lines.next().unwrap_or("");
    let field=|key:&str|->Option<&str>{
        let mut values=header.split(';').filter_map(|part|part.split_once('='))
            .filter(|(k,_)|*k==key).map(|(_,v)|v);
        let value=values.next()?;if values.next().is_some(){return None;}Some(value)
    };
    let head=|text:&str|text.chars().take(120).collect::<String>();
    let Some(cursor)=field("seq").and_then(|s|s.parse::<i64>().ok()).filter(|n|*n>=0) else{return Some(format!("header seq: {}",head(header)));};
    if !matches!(field("more"),Some("0"|"1")){return Some(format!("header more: {}",head(header)));}
    // Sequences need not rise: the recorder sends an object's kept lines, with their original
    // sequences, ahead of the line that first made the object matter to a target.
    for (index,line) in lines.enumerate() {
        if line.is_empty(){continue;}
        if line.split_once('|').and_then(|(s,_)|s.parse::<i64>().ok()).filter(|n|*n>=0&&*n<=cursor).is_none() {
            return Some(format!("line {index} sequence outside 0..={cursor}: {}",head(line)));
        }
    }
    None
}

#[cfg(test)]
mod page_tests {
    use super::*;
    #[test]
    fn page_cursor_is_signed64_text_not_outer_sequence_and_zero_preamble_is_not_a_watermark(){
        assert!(valid_page("seq=2147483650;more=0\n0|A|7|10|1|2|3|0\n2147483650|X|7"));
        for bad in ["seq=1;more=2","seq=-1;more=0","seq=9;seq=9;more=0","seq=2;more=0\n3|X|7","seq=9;more=0\n-8|X|7"] {assert!(!valid_page(bad),"{bad}");}
    }
    #[test]
    fn a_page_may_carry_the_recorders_kept_back_lines_below_the_lines_before_them(){
        assert_eq!(page_fault("seq=9;more=0\n8|X|7\n7|Ob|7|SetPosition:v|1~2~3\n9|T|7"),None);
    }
    #[test]
    fn overwrite_detection_is_retrospective_and_publish_full_is_not_lost_prefix_proof(){
        let mut c=Control::new(true);
        assert!(c.native_status("push=on,behind=5,drops=99"));
        assert!(c.native_status("push=on,behind=5,drops=100"));
        assert!(c.native_status("push=on,behind=6,drops=100"));
        assert!(c.failure.is_none(),"an overwrite resyncs; it does not end the session");
        assert_eq!(c.behind_reports,1);assert!(c.off_pending);
        assert!(!c.native_status("push=on,drops=100"));assert!(c.failure.is_some());
    }
}
