#![forbid(unsafe_code)]
//! Card 55 inter-lane contract (`internal documentation`) section 3.1 and 3.3:
//! the `rust-navigation-sync` ownership-table grammar and the paged `RUST_ROUTE_PLANS` page.
//! Pure functions only; the driver owns the epoch, the triggers and the bridge IO.

use std::collections::VecDeque;

/// One page's `<entries>` field stays within this many ASCII bytes, so the whole
/// `SACS_RECORDER` line stays far under the 4080-byte v11 control payload.
pub const ENTRIES_PAGE_MAX: usize = 3500;
/// A `rust-route-plans` page body stays within this many ASCII bytes (3800 hex bytes on the wire).
pub const PLAN_PAGE_BODY_MAX: usize = 1900;
pub const PLAN_PAGE_LINES_MAX: u32 = 16;
/// The 4-byte token for an empty ownership table.
pub const EMPTY_TABLE: &str = "none";
pub const SYNC_VERB: &str = "rust-navigation-sync";

/// One active claim: the public session, the public signed player and the wire/kernel Sim pair.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Claimed {
    pub conn_id: u64,
    pub player_id: i32,
    pub wire_actor: u64,
    pub kernel_sim: u32,
}

impl Claimed {
    pub fn render(&self) -> String {
        format!("{}:{}:{}:{}", self.conn_id, self.player_id, self.wire_actor, self.kernel_sim)
    }
}

/// Sorted ascending by `kernel_sim`, comma-joined, split so no page exceeds `ENTRIES_PAGE_MAX`.
/// An empty table is exactly one page holding `none`.
pub fn pages(entries: &[Claimed]) -> Vec<String> {
    let mut sorted: Vec<_> = entries.iter().collect();
    sorted.sort_by_key(|entry| entry.kernel_sim);
    let mut pages = Vec::new();
    let mut page = String::new();
    for entry in sorted {
        let text = entry.render();
        // One entry is at most 64 bytes, so a fresh page always holds it.
        if !page.is_empty() && page.len() + 1 + text.len() > ENTRIES_PAGE_MAX {
            pages.push(std::mem::take(&mut page));
        }
        if !page.is_empty() { page.push(','); }
        page.push_str(&text);
    }
    if pages.is_empty() && page.is_empty() { page.push_str(EMPTY_TABLE); }
    pages.push(page);
    pages
}

/// The `SACS_RECORDER` args after the correlation: verb, epoch, index, count, entries.
pub fn command_args(epoch: u64, index: usize, count: usize, page: &str) -> Vec<String> {
    vec![SYNC_VERB.into(), epoch.to_string(), index.to_string(), count.to_string(), page.into()]
}

/// The decoded `AUTONOMY_PROBE` body the mod answers each page with.
pub fn accepted_body(epoch: u64, index: usize, count: usize, page: &str, active: usize) -> String {
    let entries = if page == EMPTY_TABLE { 0 } else { page.split(',').count() };
    format!("{SYNC_VERB}=accepted;epoch={epoch};index={index};count={count};entries={entries};active={active}")
}

#[derive(Debug, PartialEq, Eq)]
pub enum PlanPageError {
    Missing,
    CursorOutOfRange,
}

impl PlanPageError {
    pub fn text(&self) -> &'static str {
        match self {
            Self::Missing => "rust-route-plans=missing",
            Self::CursorOutOfRange => "rust-route-plans=blocked;reason=cursor-out-of-range",
        }
    }
}

/// One page of a retained plan. `cursor` indexes the plan's frames (0 is the `Rt` header).
/// The driver purges retained plans on every `nav_epoch` bump; correlations are epoch-scoped.
/// The newest retained plan with this correlation wins.
pub fn plan_page(plans: &VecDeque<Vec<String>>, epoch: u64, corr: u64, cursor: u32, max_lines: u32)
    -> Result<String, PlanPageError> {
    let wanted = corr.to_string();
    let frames = plans.iter().rev().find(|plan| plan.first().is_some_and(|header| {
        let mut fields = header.split('|');
        fields.next() == Some("Rt") && fields.next() == Some(wanted.as_str())
    })).ok_or(PlanPageError::Missing)?;
    let actor = frames[0].split('|').nth(2).unwrap_or("0");
    let (total, start) = (frames.len(), cursor as usize);
    if start > total { return Err(PlanPageError::CursorOutOfRange); }
    let limit = max_lines.clamp(1, PLAN_PAGE_LINES_MAX) as usize;
    let header = |count: usize| {
        let next = start + count;
        format!("rust-route-plans=page;epoch={epoch};corr={corr};actor={actor};cursor={cursor};count={count};total={total};next={next};more={}",
            u8::from(next < total))
    };
    // The first frame always fits: the widest `Vp|rt` frame is 105 bytes and the widest header 168.
    let (mut count, mut lines_len) = (0, 0);
    while count < limit && start + count < total {
        let line_len = 1 + frames[start + count].len();
        if count >= 1 && header(count + 1).len() + lines_len + line_len > PLAN_PAGE_BODY_MAX { break; }
        lines_len += line_len;
        count += 1;
    }
    let mut body = header(count);
    for frame in &frames[start..start + count] {
        body.push('\n');
        body.push_str(frame);
    }
    Ok(body)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::messages as m;
    use simlink_bridge::text::Guid;
    use simlink_bridge::verbs::BridgeCommand;

    const BRIDGE_CORR: &str = "0123456789abcdef0123456789abcdef";
    const ACTOR: u64 = 18031287978496800304;

    fn line(epoch: u64, index: usize, count: usize, page: &str) -> String {
        simlink_bridge::verbs::encode(&BridgeCommand::SacsRecorder {
            corr: Guid::parse_wire(BRIDGE_CORR).unwrap(),
            args: command_args(epoch, index, count, page),
        }).unwrap()
    }
    fn hex(text: &str) -> String { m::hex_encode(text).to_ascii_uppercase() }
    fn reply(body: &str) -> String { format!("AUTONOMY_PROBE|{BRIDGE_CORR}|ok|{}", hex(body)) }
    fn claimed(conn_id: u64, player_id: i32, wire_actor: u64, kernel_sim: u32) -> Claimed {
        Claimed { conn_id, player_id, wire_actor, kernel_sim }
    }

    #[test]
    fn example_1a_empty_table_bytes() {
        let pages = pages(&[]);
        assert_eq!(pages, ["none"]);
        let sent = line(1, 0, 1, &pages[0]);
        assert_eq!(sent, "SACS_RECORDER|0123456789abcdef0123456789abcdef|rust-navigation-sync|1|0|1|none");
        assert_eq!(sent.len(), 78);
        assert_eq!(hex(&sent), "534143535F5245434F524445527C30313233343536373839616263646566303132333435363738396162636465667C727573742D6E617669676174696F6E2D73796E637C317C307C317C6E6F6E65");
        let body = accepted_body(1, 0, 1, &pages[0], 0);
        assert_eq!(body, "rust-navigation-sync=accepted;epoch=1;index=0;count=1;entries=0;active=0");
        assert_eq!(body.len(), 72);
        let answer = reply(&body);
        assert_eq!(answer.len(), 195);
        assert_eq!(answer, "AUTONOMY_PROBE|0123456789abcdef0123456789abcdef|ok|727573742D6E617669676174696F6E2D73796E633D61636365707465643B65706F63683D313B696E6465783D303B636F756E743D313B656E74726965733D303B6163746976653D30");
    }

    #[test]
    fn example_1b_single_claimed_actor_bytes() {
        let pages = pages(&[claimed(3, 1, ACTOR, 2)]);
        assert_eq!(pages, ["3:1:18031287978496800304:2"]);
        let sent = line(1, 0, 1, &pages[0]);
        assert_eq!(sent, "SACS_RECORDER|0123456789abcdef0123456789abcdef|rust-navigation-sync|1|0|1|3:1:18031287978496800304:2");
        assert_eq!(sent.len(), 100);
        assert_eq!(hex(&sent), "534143535F5245434F524445527C30313233343536373839616263646566303132333435363738396162636465667C727573742D6E617669676174696F6E2D73796E637C317C307C317C333A313A31383033313238373937383439363830303330343A32");
        let answer = reply(&accepted_body(1, 0, 1, &pages[0], 1));
        assert_eq!(answer, "AUTONOMY_PROBE|0123456789abcdef0123456789abcdef|ok|727573742D6E617669676174696F6E2D73796E633D61636365707465643B65706F63683D313B696E6465783D303B636F756E743D313B656E74726965733D313B6163746976653D31");
    }

    #[test]
    fn example_1c_bumped_epoch_bytes() {
        let pages = pages(&[claimed(3, 1, ACTOR, 2)]);
        let sent = line(2, 0, 1, &pages[0]);
        assert_eq!(sent, "SACS_RECORDER|0123456789abcdef0123456789abcdef|rust-navigation-sync|2|0|1|3:1:18031287978496800304:2");
        assert_eq!(hex(&sent), "534143535F5245434F524445527C30313233343536373839616263646566303132333435363738396162636465667C727573742D6E617669676174696F6E2D73796E637C327C307C317C333A313A31383033313238373937383439363830303330343A32");
        let body = accepted_body(2, 0, 1, &pages[0], 1);
        assert_eq!(body, "rust-navigation-sync=accepted;epoch=2;index=0;count=1;entries=1;active=1");
        assert_eq!(reply(&body), "AUTONOMY_PROBE|0123456789abcdef0123456789abcdef|ok|727573742D6E617669676174696F6E2D73796E633D61636365707465643B65706F63683D323B696E6465783D303B636F756E743D313B656E74726965733D313B6163746976653D31");
    }

    #[test]
    fn public_player_ids_keep_their_sign() {
        assert_eq!(pages(&[claimed(9, -7, 5, 2)]), ["9:-7:5:2"]);
    }

    #[test]
    fn sixty_plus_actors_page_without_truncation_sorted_by_kernel_sim() {
        // Worst-case entry widths: u64 conn and wire ids, a negative player, a u32 kernel Sim.
        let mut entries: Vec<_> = (0..120u32).map(|n| claimed(u64::MAX - u64::from(n), i32::MIN + n as i32,
            u64::MAX - 1000 - u64::from(n), u32::MAX - n)).collect();
        entries.reverse();
        let pages = pages(&entries);
        assert!(pages.len() >= 2, "{} pages", pages.len());
        let mut rejoined = Vec::new();
        for (index, page) in pages.iter().enumerate() {
            assert!(!page.is_empty() && page != "none" && page.len() <= ENTRIES_PAGE_MAX, "page {index}: {}", page.len());
            let sent = line(u64::MAX, index, pages.len(), page);
            assert!(sent.len() <= 3611 && sent.len() < simlink_bridge::layout::MAX_LINE_BYTES, "{}", sent.len());
            let body = accepted_body(u64::MAX, index, pages.len(), page, entries.len());
            assert!(body.len() <= 130, "{}", body.len());
            rejoined.extend(page.split(',').map(str::to_owned));
        }
        entries.sort_by_key(|e| e.kernel_sim);
        assert_eq!(rejoined, entries.iter().map(Claimed::render).collect::<Vec<_>>());
    }

    fn six_frame_plan() -> Vec<String> {
        let a = ACTOR;
        vec![format!("Rt|42|{a}|5|0123456789abcdef|3250"), format!("Vp|rt|42|{a}|0|0|100|0|100"),
            format!("Vp|rt|42|{a}|1|0|200|0|100"), format!("Vp|rt|42|{a}|2|2147483648|210|0|100"),
            format!("Vp|rt|42|{a}|3|0|300|0|100"), format!("Vp|rt|42|{a}|4|0|300|0|100")]
    }

    #[test]
    fn route_plans_page_example_bytes() {
        let plans = VecDeque::from([six_frame_plan()]);
        let body = plan_page(&plans, 1, 42, 0, 2).unwrap();
        assert_eq!(body, "rust-route-plans=page;epoch=1;corr=42;actor=18031287978496800304;cursor=0;count=2;total=6;next=2;more=1\n\
Rt|42|18031287978496800304|5|0123456789abcdef|3250\nVp|rt|42|18031287978496800304|0|0|100|0|100");
        assert_eq!(body.len(), 198);
        let answer = reply(&body);
        assert_eq!(answer.len(), 447);
        assert_eq!(answer, "AUTONOMY_PROBE|0123456789abcdef0123456789abcdef|ok|727573742D726F7574652D706C616E733D706167653B65706F63683D313B636F72723D34323B6163746F723D31383033313238373937383439363830303330343B637572736F723D303B636F756E743D323B746F74616C3D363B6E6578743D323B6D6F72653D310A52747C34327C31383033313238373937383439363830303330347C357C303132333435363738396162636465667C333235300A56707C72747C34327C31383033313238373937383439363830303330347C307C307C3130307C307C313030");
        let command = simlink_bridge::verbs::encode(&BridgeCommand::SacsRecorder {
            corr: Guid::parse_wire(BRIDGE_CORR).unwrap(),
            args: ["rust-route-plans", "42", "18031287978496800304", "0", "2"].map(String::from).to_vec(),
        }).unwrap();
        assert_eq!(command.len(), 91);
        assert_eq!(command, "SACS_RECORDER|0123456789abcdef0123456789abcdef|rust-route-plans|42|18031287978496800304|0|2");
        assert_eq!(hex(&command), "534143535F5245434F524445527C30313233343536373839616263646566303132333435363738396162636465667C727573742D726F7574652D706C616E737C34327C31383033313238373937383439363830303330347C307C32");
    }

    #[test]
    fn route_plans_pages_walk_the_whole_plan_and_the_last_one_ends() {
        let plans = VecDeque::from([six_frame_plan()]);
        let second = plan_page(&plans, 1, 42, 2, 16).unwrap();
        assert!(second.starts_with("rust-route-plans=page;epoch=1;corr=42;actor=18031287978496800304;cursor=2;count=4;total=6;next=6;more=0\n"));
        assert_eq!(second.lines().count(), 5);
        let end = plan_page(&plans, 1, 42, 6, 16).unwrap();
        assert_eq!(end, "rust-route-plans=page;epoch=1;corr=42;actor=18031287978496800304;cursor=6;count=0;total=6;next=6;more=0");
        assert_eq!(plan_page(&plans, 1, 42, 7, 16), Err(PlanPageError::CursorOutOfRange));
        assert_eq!(plan_page(&plans, 1, 43, 0, 16), Err(PlanPageError::Missing));
        // max_lines is clamped to 1..=16, with 0 read as 1.
        assert_eq!(plan_page(&plans, 1, 42, 0, 0).unwrap().lines().count(), 2);
        assert_eq!(plan_page(&plans, 1, 42, 0, 99).unwrap().lines().count(), 7);
    }

    #[test]
    fn newest_retained_plan_wins_a_restarted_correlation() {
        let old = vec!["Rt|1|10|1|0000000000000001|0".to_string()];
        let new = vec!["Rt|1|20|1|0000000000000002|0".to_string()];
        let plans = VecDeque::from([old, new]);
        assert!(plan_page(&plans, 1, 1, 0, 16).unwrap().contains("actor=20;"));
    }

    #[test]
    fn max_envelope_plan_pages_stay_under_1900_bytes_and_reassemble() {
        // 19,782 legs plus the Rt header: the largest envelope the daemon can hold.
        let a = u64::MAX;
        let mut frames = vec![format!("Rt|{a}|{a}|19782|ffffffffffffffff|{a}")];
        frames.extend((0..19_782u32).map(|i| format!("Vp|rt|{a}|{a}|{i}|{}|{}|{}|{}", u32::MAX, i32::MIN, i32::MIN, i32::MIN)));
        let plans = VecDeque::from([frames.clone()]);
        let (mut cursor, mut seen, mut pages) = (0u32, Vec::new(), 0u32);
        loop {
            let body = plan_page(&plans, u64::MAX, a, cursor, 16).unwrap();
            assert!(body.len() <= PLAN_PAGE_BODY_MAX, "{}", body.len());
            let mut lines = body.lines();
            let header = lines.next().unwrap();
            let field = |name: &str| -> u32 { header.split(';').find_map(|f| f.strip_prefix(name)).unwrap().parse().unwrap() };
            let (count, next, more) = (field("count="), field("next="), field("more="));
            assert_eq!(field("total="), 19_783);
            assert!(count >= 1);
            seen.extend(lines.map(str::to_owned));
            pages += 1;
            cursor = next;
            if more == 0 { break; }
        }
        assert_eq!(seen, frames);
        assert_eq!(pages, 19_783u32.div_ceil(16));
    }

    #[test]
    fn epoch_delivery_section_3_8_byte_vectors_keep_rt_and_vp_sealed() {
        // These include the refused client inputs: the Rust transport carries bytes
        // opaquely; the presenter parser owns refusal/fencing (w1's companion tests).
        for (text, bytes) in [
            ("Ne|2","4E657C32"),
            ("0|Ne|1","307C4E657C31"),
            ("0|Ne|2","307C4E657C32"),
            ("0|Rt|1|18031287978496800304|2|0123456789abcdef|2000","307C52747C317C31383033313238373937383439363830303330347C327C303132333435363738396162636465667C32303030"),
            ("Rt|1|18031287978496800304|2|0123456789abcdef|2000","52747C317C31383033313238373937383439363830303330347C327C303132333435363738396162636465667C32303030"),
            ("0|Vp|rt|1|18031287978496800304|0|0|300|0|100","307C56707C72747C317C31383033313238373937383439363830303330347C307C307C3330307C307C313030"),
            ("0|Vp|rt|1|18031287978496800304|1|0|300|0|100","307C56707C72747C317C31383033313238373937383439363830303330347C317C307C3330307C307C313030"),
            ("0|Vp|rt|1|18031287978496800304|0|0|500|0|100","307C56707C72747C317C31383033313238373937383439363830303330347C307C307C3530307C307C313030"),
            ("0|Vp|rt|1|18031287978496800304|1|0|500|0|100","307C56707C72747C317C31383033313238373937383439363830303330347C317C307C3530307C307C313030"),
            ("0|Rt|x|18031287978496800304|2|0123456789abcdef|2000","307C52747C787C31383033313238373937383439363830303330347C327C303132333435363738396162636465667C32303030"),
            ("0|Rt|7|18031287978496800304|2|0123456789abcdef|2000","307C52747C377C31383033313238373937383439363830303330347C327C303132333435363738396162636465667C32303030"),
            ("0|Ne|0","307C4E657C30"), ("0|Ne|02","307C4E657C3032"), ("0|Ne|2|1","307C4E657C327C31"),
            ("rust-route-client=blocked;reason=epoch-required","727573742D726F7574652D636C69656E743D626C6F636B65643B726561736F6E3D65706F63682D7265717569726564"),
            ("rust-route-client=blocked;reason=epoch-mismatch","727573742D726F7574652D636C69656E743D626C6F636B65643B726561736F6E3D65706F63682D6D69736D61746368"),
        ] {
            assert_eq!(hex(text),bytes);
            assert_eq!(m::hex_decode(&bytes.to_ascii_lowercase()).unwrap(),text);
        }
        let plans = VecDeque::from([vec![format!("Rt|1|{ACTOR}|2|0123456789abcdef|2000"),
            format!("Vp|rt|1|{ACTOR}|0|0|500|0|100"),format!("Vp|rt|1|{ACTOR}|1|0|500|0|100")]]);
        let page = plan_page(&plans,2,1,0,2).unwrap();
        let header = page.lines().next().unwrap();
        assert_eq!(header.len(),102);
        assert_eq!(hex(header),"727573742D726F7574652D706C616E733D706167653B65706F63683D323B636F72723D313B6163746F723D31383033313238373937383439363830303330343B637572736F723D303B636F756E743D323B746F74616C3D333B6E6578743D323B6D6F72653D31");
        let command = simlink_bridge::verbs::encode(&BridgeCommand::SacsRecorder {
            corr:Guid::parse_wire(BRIDGE_CORR).unwrap(),
            args:["rust-route-plans","1","18031287978496800304","2","16","2"].map(String::from).to_vec(),
        }).unwrap();
        assert_eq!(command.len(),93);
        assert_eq!(hex(&command),"534143535F5245434F524445527C30313233343536373839616263646566303132333435363738396162636465667C727573742D726F7574652D706C616E737C317C31383033313238373937383439363830303330347C327C31367C32");
    }
}
