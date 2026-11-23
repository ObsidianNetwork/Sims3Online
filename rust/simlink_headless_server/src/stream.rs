#![forbid(unsafe_code)]
//! Cadence and presentation pump shared by the resident actor and offline replay.

use std::collections::{HashMap, HashSet, VecDeque};

/// A full live transform this recent means the object is streaming its pose to the clients.
const STREAMING_WINDOW_MS: u64 = 1_000;
const MAX_STREAMING_OBJECTS: usize = 4_096;
/// Above this many known drivers the ownership table is dropped; waiting gathers then hold every driver line.
const MAX_KNOWN_DRIVERS: usize = 8_192;

/// The lot id the game gives everything that is on no lot: the streets of the whole world.
const WORLD_LOT: u64 = u64::MAX;
/// Off-lot Sims are of interest by distance to an off-lot claimed Sim, not by sharing the world lot.
const WORLD_ENTER_M: f32 = 60.0;
/// A Sim already streamed stays of interest out to here, so one at the edge does not flap.
const WORLD_EXIT_M: f32 = 90.0;
/// A Sim must stay near this long before it is adopted: a car driving past is not worth a history gather.
const WORLD_DWELL_MS: u64 = 1_500;
use crate::cadence::{Action, Thresholds};
use crate::messages::{enc_list, hex_encode, Event};
use crate::presentation::{self, Ladder, LadderStep};
use crate::routing::RoutedEvent;
use crate::{ConnId, DriverError, GameDriver, Server};
use crate::transport::{StreamReply, StreamRequest};

// One page per retained recorder event (SacsRecorder.RingCapacity = 4096),
// plus an end probe, counted across deferred replies and pumps. Once exhausted,
// more=1 fails closed; a later end probe can finish the still-pending epoch.
const MAX_EPOCH_DUMP_PAGES: usize = 4096 + 1;

#[derive(Clone, Debug)]
pub struct StreamOptions {
    pub sync_policy: Option<std::path::PathBuf>,
    pub base_interval_ms: f64,
    pub thresholds: Thresholds,
    pub presentation_poll_ms: u64,
    pub presentation_min_interval_ms: u64,
    pub ring_requested: bool,
    /// Existing native presentation routes, explicitly opted in by the launcher.
    pub route_stream_requested: bool,
}

#[cfg(test)]
mod frozen_cutover_tests {
    use super::*;

    struct ReadyPage(Option<String>);
    impl GameDriver for ReadyPage {
        fn adjudicate(&mut self, _: &crate::messages::Command) -> Result<Vec<u8>, DriverError> { unreachable!() }
        fn poll_event_lines(&mut self) -> Result<Vec<Vec<u8>>, DriverError> { Ok(Vec::new()) }
        fn presentation_pages(&mut self) -> Result<Vec<String>, DriverError> { Ok(self.0.take().into_iter().collect()) }
        fn claimed_sims(&self) -> Vec<u64> { vec![10] }
    }

    fn animation(events: &[RoutedEvent], id: u64) -> Vec<String> {
        events.iter().filter(|event| event.event.kind == "SimAnimation"
            && (event.requester.is_none() || event.requester == Some(ConnId(id))))
            .flat_map(|event| crate::messages::dec_list(&event.event.fields[0]).unwrap())
            .map(|line| crate::messages::hex_decode(&line).unwrap()).collect()
    }

    fn pending(fresh: bool) -> (Stream, Server, String) {
        let mut stream = Stream::new(StreamOptions { presentation_min_interval_ms: 10_000, ..StreamOptions::default() });
        stream.ladder.pushing = true;
        stream.initialization = Initialization::Ready;
        stream.last_presentation_ms = 1000;
        let mut server = Server::new(1);
        for id in [1, 2] { server.connect(ConnId(id)); server.join(ConnId(id), id as i32); }
        let mut history = presentation::HistoryGather::new(&[10]);
        history.accept("drivers=7").unwrap();
        let snapshot_id = history.request().unwrap()[3].clone();
        stream.gathers.push_back(JoinGather { history,
            epoch_participants_ready: None,
            audience: if fresh { GatherAudience::Fresh { counted: vec![ConnId(1), ConnId(2)], joiners: vec![ConnId(2)] } }
                else { GatherAudience::Join(vec![ConnId(2)]) },
            held: Vec::new(), started_ms: Some(1000), catchup_started_ms: None, timing: GatherTiming::default(), dump_turn: false, locked: true, abandoned: false });
        (stream, server, snapshot_id)
    }

    #[test]
    fn final_reply_ready_push_is_cut_over_before_join_removal_even_inside_pacing_window() {
        for fresh in [false, true] {
            let (mut stream, server, snapshot_id) = pending(fresh);
            let reply = StreamReply::Recorder(format!("next=-1;snapshot={snapshot_id};cursor=9\n0|A|7|1|2|3|4|-2"));
            let mut driver = ReadyPage(Some("seq=10;more=0\n0|A|7|1|2|3|4|-2\n0|S|7|5|10\n9|R|7|5|6|0|10\n10|AW|7".into()));
            let mut events = Vec::new();
            stream.prepare_gather_reply(&StreamIo::Gather, &reply, 1001, &server, &mut driver, &mut events).unwrap();
            stream.complete_io(StreamIo::Gather, reply, 1001, &mut events).unwrap();
            stream.finish_frozen_gathers(1001, &server, &mut events);
            stream.flush_presentation_inner(1001, &server, &mut events, true);
            let joiner = animation(&events, 2);
            assert_eq!(joiner, ["0|A|7|1|2|3|4|-2", "10|AW|7"], "fresh={fresh}");
            if !fresh {
                assert!(animation(&events, 1).contains(&"9|R|7|5|6|0|10".into()), "existing client loses no live line");
            }
            assert!(stream.pushed.is_empty());
            assert!(stream.gathers.is_empty());
        }
    }

    #[test]
    fn failed_fresh_or_join_gather_discards_partial_history_and_releases_live_unfiltered() {
        for fresh in [false, true] {
            let (mut stream, server, snapshot_id) = pending(fresh);
            stream.complete_io(StreamIo::Gather, StreamReply::Recorder(format!("next=1;snapshot={snapshot_id};cursor=9\n0|A|7|1|2|3|4|-2")), 1000, &mut Vec::new()).unwrap();
            let live = "9|R|7|5|6|0|10".to_string();
            if fresh { stream.pushed.push(live.clone()); }
            else { stream.gathers.front_mut().unwrap().held.push(live.clone()); }
            let mut events = Vec::new();
            let error = stream.complete_io(StreamIo::Gather,
                StreamReply::Recorder(format!("next=-1;snapshot={snapshot_id};cursor=10")), 1001, &mut events).unwrap_err();
            assert!(error.0.contains("metadata changed"));
            stream.flush_presentation_inner(1001, &server, &mut events, true);
            assert_eq!(animation(&events, 2), [live], "fresh={fresh}");
            assert!(stream.gathers.is_empty());
            assert!(stream.report.iter().any(|line| line.contains("join_history_failed") && line.contains("metadata changed")));
            assert!(stream.history_pending());
            assert_eq!(stream.history_failures, 1);
            let failure = crate::messages::hex_decode(&stream.history_last_failure_hex).unwrap();
            assert!(failure.contains("snapshot metadata changed between pages"));
            assert!(failure.contains("presentation_history_driver=7|presentation_history_offset=1"));
            assert!(stream.state_report().contains("presentation_history_stage=idle"));
            assert!(stream.state_report().contains("presentation_history_last_progress_ms=1000"));
        }
    }

    #[test]
    fn live_failure_context_bounds_and_hex_frames_native_error_text() {
        let (mut stream, _, _) = pending(false);
        let reason = "😀|x=y\n".repeat(1000);
        stream.failed_io(StreamIo::Gather, &mut Vec::new(), &reason, 1234);
        let failure = crate::messages::hex_decode(&stream.history_last_failure_hex).unwrap();
        assert!(failure.ends_with(&reason.chars().take(256).collect::<String>()));
        assert!(stream.history_last_failure_hex.len() < 4096);
        let report = stream.state_report();
        assert!(!report.contains('😀'));
        assert!(!report.contains('\n'));
        assert_eq!(report.matches("presentation_history_last_failure_hex=").count(), 1);
    }
    #[test]
    fn participant_claim_without_joined_audience_keeps_initial_history_debt_unplanned() {
        struct Claimed;
        impl GameDriver for Claimed {
            fn adjudicate(&mut self, _: &crate::messages::Command) -> Result<Vec<u8>, DriverError> { unreachable!() }
            fn poll_event_lines(&mut self) -> Result<Vec<Vec<u8>>, DriverError> { Ok(Vec::new()) }
            fn participant_lifecycle_enabled(&self) -> bool { true }
            fn claimed_sims(&self) -> Vec<u64> { vec![20] }
            fn start_stream_request(&mut self, _: &StreamRequest) -> Result<Option<StreamReply>, DriverError> { Ok(None) }
        }
        let mut stream = Stream::new(StreamOptions::default());
        let mut server = Server::new(1);
        let mut events = Vec::new();
        stream.pump_into(0, &mut server, &mut Claimed, &mut events).unwrap();
        assert!(stream.gathers.is_empty(), "an empty Fresh audience would vacuously pass participant readiness");
        assert!(stream.targets.is_empty());
        assert!(stream.participant_fresh_due, "first actual recipient is still owed participant history");
        assert!(events.is_empty());
        server.connect(ConnId(1));
        server.join(ConnId(1), 1);
        stream.joined(ConnId(1));
        stream.pump_into(1, &mut server, &mut Claimed, &mut events).unwrap();
        let gather = stream.gathers.front().expect("actual recipient is owed history");
        assert!(matches!(&gather.audience, GatherAudience::Fresh { counted, joiners }
            if counted == &[ConnId(1)] && joiners == &[ConnId(1)]));
        assert!(!stream.gather_participants_ready(gather));
        assert!(events.is_empty());
    }

    #[test]
    fn pending_initial_join_rebind_moves_the_immediate_planning_obligation() {
        let mut stream = Stream::new(StreamOptions::default());
        stream.joined(ConnId(1));
        stream.rebound(ConnId(1), ConnId(2));
        assert!(!stream.initial_joiners.contains(&ConnId(1)));
        assert!(stream.initial_joiners.contains(&ConnId(2)));
        assert!(!stream.joiners.contains(&ConnId(1)));
        assert!(stream.joiners.contains(&ConnId(2)));
    }

    #[test]
    fn completed_snapshot_waits_for_late_dump_prefix_before_delivery() {
        for fresh in [false, true] {
            let (mut stream, mut server, snapshot_id) = pending(fresh);
            let mut events = Vec::new();
            stream.complete_io(StreamIo::Gather,
                StreamReply::Recorder(format!("next=-1;snapshot={snapshot_id};cursor=9\n0|A|7|1|2|3|4|-2")),
                1001, &mut events).unwrap();
            stream.finish_frozen_gathers(1001, &server, &mut events);
            assert!(animation(&events, 2).is_empty());
            assert_eq!(stream.gathers.len(), 1);
            assert!(matches!(stream.next_io(&mut server, &mut Vec::new(), 0), Some(StreamIo::Dump { .. })));
            stream.complete_io(StreamIo::Dump { generation: stream.drain.generation() },
                StreamReply::Recorder("seq=10;more=0\n0|A|7|1|2|3|4|-2\n0|S|7|5|10\n9|R|7|5|6|0|10\n10|AW|7".into()),
                1002, &mut events).unwrap();
            stream.finish_frozen_gathers(1002, &server, &mut events);
            stream.flush_presentation_inner(1002, &server, &mut events, true);
            assert_eq!(animation(&events, 2), ["0|A|7|1|2|3|4|-2", "10|AW|7"]);
        }
    }

    #[test]
    fn slow_complete_history_gets_its_own_cursor_catchup_window() {
        for fresh in [false, true] {
            let (mut stream, server, snapshot_id) = pending(fresh);
            let mut events = Vec::new();
            stream.complete_io(StreamIo::Gather,
                StreamReply::Recorder(format!("next=-1;snapshot={snapshot_id};cursor=9\n0|A|7|1|2|3|4|-2")),
                40_001, &mut events).unwrap();
            stream.abandon_gathers(40_002, &mut events);
            assert_eq!(stream.gathers.len(), 1, "completed history must not inherit gather elapsed time; fresh={fresh}");
            stream.complete_io(StreamIo::Dump { generation: stream.drain.generation() }, StreamReply::Recorder("seq=10;more=0\n10|AW|7".into()),
                40_003, &mut events).unwrap();
            stream.finish_frozen_gathers(40_003, &server, &mut events);
            stream.flush_presentation_inner(40_003, &server, &mut events, true);
            assert_eq!(animation(&events, 2), ["0|A|7|1|2|3|4|-2", "10|AW|7"]);
        }
    }

    #[test]
    fn stalled_frozen_catchup_retries_full_known_target_history_without_clearing_failure() {
        for fresh in [false, true] {
            let (mut stream, server, snapshot_id) = pending(fresh);
            let mut events = Vec::new();
            stream.targets.insert(10);
            stream.complete_io(StreamIo::Gather,
                StreamReply::Recorder(format!("next=-1;snapshot={snapshot_id};cursor=9\n0|A|7|1|2|3|4|-2")),
                40_001, &mut events).unwrap();
            let live = "9|R|7|5|6|0|10".to_string();
            if fresh { stream.pushed.push(live.clone()); }
            else { stream.gathers.front_mut().unwrap().held.push(live.clone()); }
            stream.abandon_gathers(70_001, &mut events);
            stream.flush_presentation_inner(70_001, &server, &mut events, true);
            assert_eq!(animation(&events, 2), [live]);
            assert_eq!(stream.history_failures, 1);
            assert!(stream.history_pending(), "held-live alone cannot complete readiness");
            assert_eq!(stream.next_presentation_ms, 71_001);
            assert!(stream.gathers.is_empty());
            stream.plan_presentation(71_001, &server, &ReadyPage(None));
            assert!(matches!(&stream.gathers.front().unwrap().audience,
                GatherAudience::Join(ids) if ids.contains(&ConnId(2))));
            stream.complete_io(StreamIo::Gather, StreamReply::Recorder("drivers=7".into()),
                71_002, &mut events).unwrap();
            let snapshot_id = stream.gathers.front().unwrap().history.request().unwrap()[3].clone();
            stream.complete_io(StreamIo::Gather,
                StreamReply::Recorder(format!("next=-1;snapshot={snapshot_id};cursor=10\n0|A|7|1|2|3|4|-2\n0|E|7|5|6|0|10")),
                71_003, &mut events).unwrap();
            stream.cursor = 10;
            stream.finish_frozen_gathers(71_003, &server, &mut events);
            assert!(!stream.history_pending());
            assert_eq!(stream.history_failures, 1, "recovery preserves cumulative failures");
            let lines = animation(&events, 2);
            assert!(lines.contains(&"0|A|7|1|2|3|4|-2".into()));
            assert!(lines.contains(&"0|E|7|5|6|0|10".into()));
        }
    }

    #[test]
    fn fresh_history_yields_dumps_and_orders_fresh_object_placements_after_baseline() {
        let (mut stream, mut server, _) = pending(true);
        stream.ladder.pushing = false;
        let mut history = presentation::HistoryGather::new(&[20]);
        history.accept("drivers=4294967280,8").unwrap();
        stream.gathers.front_mut().unwrap().history = history;
        stream.want_dump = true;
        let mut events = Vec::new();
        assert!(matches!(stream.next_io(&mut server, &mut events, 0), Some(StreamIo::Gather)));
        stream.complete_io(StreamIo::Gather, StreamReply::Recorder("next=1\n0|Ob|20|baseline\n0|Ob|42|prop_baseline".into()), 1001, &mut events).unwrap();
        assert!(matches!(stream.next_io(&mut server, &mut events, 0), Some(StreamIo::Gather)), "BoundaryState owns target selection through its final page");
        stream.complete_io(StreamIo::Gather, StreamReply::Recorder("next=-1".into()), 1002, &mut events).unwrap();
        assert!(matches!(stream.next_io(&mut server, &mut events, 0), Some(StreamIo::Dump { .. })), "slow driver history must yield a live dump");
        stream.complete_io(StreamIo::Dump { generation: stream.drain.generation() }, StreamReply::Recorder("seq=13;more=0\n8|Pz|18031287978496800272|1|2|3|0|0|1\n9|R|7|5|6|0|10\n10|R|8|5|6|0|20\n11|Pz|20|5|2|3|0|0|1\n12|Po|20|SittingInVehicle|30\n13|Ob|42|SetPosition:v|9~2~3".into()), 1003, &mut events).unwrap();
        stream.flush_presentation_inner(1003, &server, &mut events, true);
        assert_eq!(animation(&events, 1), ["8|Pz|18031287978496800272|1|2|3|0|0|1", "9|R|7|5|6|0|10"]);
        let snapshot_id = stream.gathers.front().unwrap().history.request().unwrap()[3].clone();
        stream.complete_io(StreamIo::Gather, StreamReply::Recorder(format!("next=1;snapshot={snapshot_id};cursor=9\n0|A|8|20|2|3|4|-2")), 1004, &mut events).unwrap();
        assert!(matches!(stream.next_io(&mut server, &mut events, 0), Some(StreamIo::Dump { .. })), "one dump turn per history page");
        stream.complete_io(StreamIo::Dump { generation: stream.drain.generation() }, StreamReply::Recorder("seq=15;more=0\n14|Pz|18031287978496800272|2|2|3|0|0|1\n15|R|7|5|6|0|10".into()), 1005, &mut events).unwrap();
        stream.flush_presentation_inner(1005, &server, &mut events, true);
        assert!(!animation(&events, 1).iter().any(|line| line.starts_with("10|R|8")));
        stream.complete_io(StreamIo::Gather, StreamReply::Recorder(format!("next=-1;snapshot={snapshot_id};cursor=9\n0|E|8|5|6|0|20")), 1006, &mut events).unwrap();
        stream.finish_frozen_gathers(1006, &server, &mut events);
        stream.flush_presentation_inner(1006, &server, &mut events, true);
        let lines = animation(&events, 1);
        assert_eq!(&lines[lines.len()-8..], ["0|Ob|20|baseline", "0|Ob|42|prop_baseline", "0|A|8|20|2|3|4|-2", "0|E|8|5|6|0|20", "10|R|8|5|6|0|20", "11|Pz|20|5|2|3|0|0|1", "12|Po|20|SittingInVehicle|30", "13|Ob|42|SetPosition:v|9~2~3"]);
    }

    #[test]
    fn delta_after_full_updates_interest_before_advancing_its_watermark() {
        let snapshot = |tick, lot| crate::snapshot::Snapshot {
            tick, world_name: "Fixture".into(), save_name: "Fixture".into(), speed: "Normal".into(),
            sims: [(10, 100), (20, lot)].map(|(object_id, lot)| crate::snapshot::Sim {
                object_id, name: "Fixture".into(), household_id: 1, position: [0.0; 3],
                interaction_id: None, owning_player_id: None, interaction_key: None, lot_id: Some(lot),
                queue_count: None, posture_container: None, posture_type: None, description_id: Some(object_id),
            }).to_vec(), objects: vec![],
        };
        let mut stream = Stream::new(StreamOptions::default());
        stream.state_index_ready = false;
        stream.cadence_batch = Some(CadenceBatch { decisions: vec![], sessions: vec![], stage: CadenceStage::Publish,
            full: Some(snapshot(10, 100)), delta: Some(snapshot(11, 200)) });
        let mut server = Server::new(1);
        stream.publish_cadence(&mut server, &mut vec![]);
        stream.refresh_state_scope(&server, &ReadyPage(None));
        assert!(stream.state_index_ready);
        assert_eq!(stream.last_tick, 11);
        assert_eq!(stream.sim_lots[&20], Some(200));
        assert!(stream.visible_state.scope.candidates.is_empty(), "the full's older lot cannot survive the newer delta watermark");
    }

    fn model_fixture() -> (Stream, Server, ConnId) {
        let mut server = Server::new(1);
        let id = ConnId(1);
        server.connect(id); server.join(id, 1);
        let mut stream = Stream::new(StreamOptions::default());
        stream.refresh_state_scope(&server, &ReadyPage(None));
        (stream, server, id)
    }
    fn model_snapshot(tick: i64) -> crate::snapshot::Snapshot {
        crate::snapshot::Snapshot {tick,world_name:"Fixture".into(),save_name:"Fixture".into(),speed:"Normal".into(),sims:vec![],objects:vec![]}
    }
    #[test]
    fn initial_model_session_preserves_existing_join_owned_baseline_cadence() {
        let (stream,server,id)=model_fixture();
        assert!(!stream.model_cuts.unready(id));
        assert_eq!(stream.cadence_decision(id,&server).action,Action::PublishDelta);
        assert_eq!(server.connection(id).unwrap().cadence.resync_from_tick,None);
    }
    fn forced_model(stream: &mut Stream, server: &mut Server, id: ConnId, tick: i64) -> RoutedEvent {
        stream.cadence_batch = Some(CadenceBatch {decisions:vec![(id,crate::cadence::Decision{action:Action::ForceFull,interval_ms:100.0,resync_from_tick:tick})],
            sessions:vec![stream.model_cuts.session(id).unwrap()],stage:CadenceStage::Publish,full:Some(model_snapshot(tick)),delta:None});
        let mut events=vec![]; stream.publish_cadence(server,&mut events);
        events.pop().unwrap()
    }
    #[test]
    fn rejected_or_skipped_current_full_recovers_without_clearing_a_newer_cut() {
        for rejected in [true,false] {
            let (mut stream,mut server,id)=model_fixture();
            let mut event=forced_model(&mut stream,&mut server,id,10);
            assert_eq!(server.connection(id).unwrap().cadence.resync_from_tick,Some(10));
            if rejected {
                event.event.fields[0].push('x');
                assert!(!stream.model_event_current(&event,&mut server,&ReadyPage(None)));
            } else {stream.model_event_delivered(&event,false,&mut server,&ReadyPage(None));}
            assert!(stream.model_cuts.unready(id));
            assert_eq!(server.connection(id).unwrap().cadence.resync_from_tick,None);
            assert_eq!(stream.cadence_decision(id,&server).action,Action::ForceFull);
            let current=forced_model(&mut stream,&mut server,id,11);
            stream.model_event_delivered(&event,false,&mut server,&ReadyPage(None));
            assert_eq!(server.connection(id).unwrap().cadence.resync_from_tick,Some(11),"old completion cannot clear newer recovery");
            stream.model_event_delivered(&current,true,&mut server,&ReadyPage(None));
            assert!(!stream.model_cuts.unready(id));
        }
    }
    #[test]
    fn suppressed_model_stays_suppressed_and_recovery_waits_for_an_eligible_turn() {
        let (mut stream,mut server,id)=model_fixture();
        let initial=forced_model(&mut stream,&mut server,id,1);
        stream.model_event_delivered(&initial,true,&mut server,&ReadyPage(None));
        server.connection_mut(id).unwrap().cadence.ack(1);
        assert!(!stream.model_cuts.unready(id));
        stream.cadence_batch=Some(CadenceBatch {decisions:vec![(id,crate::cadence::Decision {action:Action::Suppress,interval_ms:100.0,resync_from_tick:10})],
            sessions:vec![stream.model_cuts.session(id).unwrap()],stage:CadenceStage::Publish,full:None,delta:Some(crate::snapshot::Snapshot {
                objects:vec![crate::snapshot::Object{object_id:42,script_class:"Fixture".into(),position:[0.0;3],lot_id:None}],..model_snapshot(10)})});
        let mut events=vec![];stream.publish_cadence(&mut server,&mut events);
        assert!(events.is_empty());assert!(stream.model_cuts.unready(id));
        stream.last_tick=10_000;
        server.connection_mut(id).unwrap().cadence.force_full(10_000);
        assert_eq!(stream.cadence_decision(id,&server).action,Action::Suppress,"backpressure must win over model recovery");
        server.connection_mut(id).unwrap().cadence.ack(10_000);
        assert_eq!(stream.cadence_decision(id,&server).action,Action::ForceFull);
    }
}
impl Default for StreamOptions {
    fn default() -> Self {
        Self { base_interval_ms: 2000.0, thresholds: Thresholds::default(),
            presentation_poll_ms: 250, presentation_min_interval_ms: 0, ring_requested: false,
            route_stream_requested: false, sync_policy: None }
    }
}
impl StreamOptions {
    pub fn from_args(args: &[String]) -> Result<Self, String> {
        let mut options = Self { thresholds: Thresholds::from_args(args)?, ..Self::default() };
        if let Some(i) = args.iter().position(|s| s == "--sync-policy") {
            options.sync_policy = Some(args.get(i + 1).filter(|s| !s.starts_with("--"))
                .ok_or("--sync-policy needs a file")?.into());
        }
        if let Some(i) = args.iter().position(|s| s == "--sync-broadcast-seconds") {
            let value: i32 = args.get(i + 1).ok_or("--sync-broadcast-seconds needs a value")?
                .parse().map_err(|_| "invalid --sync-broadcast-seconds")?;
            options.base_interval_ms = f64::from(value.max(0)) * 1000.0;
        }
        options.ring_requested = std::env::var("SIMSONLINE_SIMLINK_SERVER_RING").as_deref() == Ok("1");
        options.route_stream_requested = std::env::var("SIMSONLINE_ROUTE_STREAM").as_deref() == Ok("1");
        options.presentation_min_interval_ms = std::env::var("SIMSONLINE_PRESENTATION_MIN_INTERVAL_MS")
            .ok().and_then(|v| v.parse().ok()).unwrap_or(0);
        options.presentation_poll_ms = std::env::var("SIMSONLINE_PRESENTATION_POLL_MS").ok()
            .and_then(|v| v.parse::<u64>().ok()).filter(|n| (50..=1000).contains(n)).unwrap_or(250);
        Ok(options)
    }
}

/// Elapsed pump windows when a join relays presentation. Sample every pump,
/// including empty ones: recorder silence is not a stalled presentation pump.
#[derive(Default)]
pub struct JoinGap {
    last_pump_ms: Option<u64>,
    pub max_during_join: u64,
}
impl JoinGap {
    pub fn observe(&mut self, now: u64, joining: bool, interval_ms: u64) -> u64 {
        let gap = if joining {
            let gap = self.last_pump_ms.map_or(1, |previous|
                (now.saturating_sub(previous) / interval_ms.max(1)).max(1));
            self.max_during_join = self.max_during_join.max(gap);
            gap
        } else { 0 };
        self.last_pump_ms = Some(now);
        gap
    }
}

enum Initialization { Ring, Cursor, Ready }
enum CadenceStage { Diag, Full, Delta, Publish }
struct CadenceBatch {
    decisions: Vec<(ConnId, crate::cadence::Decision)>,
    // Captured before any native read, so equal-ID Leave/Join cannot reuse it.
    sessions: Vec<crate::model_cut::Session>,
    stage: CadenceStage,
    full: Option<crate::snapshot::Snapshot>,
    delta: Option<crate::snapshot::Snapshot>,
}
enum GatherAudience {
    Fresh { counted: Vec<ConnId>, joiners: Vec<ConnId> },
    Join(Vec<ConnId>),
}
/// Where a gather's time went: recorder replies seen, when the first came, and the longest wait for one.
#[derive(Default)]
struct GatherTiming { replies: u32, first_reply_ms: u64, last_reply_ms: u64, slowest_reply_ms: u64 }
impl GatherTiming {
    fn reply(&mut self, now: u64, started: u64) {
        if self.replies == 0 { self.first_reply_ms = now; }
        let since = if self.replies == 0 { started } else { self.last_reply_ms };
        self.slowest_reply_ms = self.slowest_reply_ms.max(now.saturating_sub(since));
        self.replies += 1; self.last_reply_ms = now;
    }
}
struct JoinGather {
    timing: GatherTiming,
    history: presentation::HistoryGather,
    audience: GatherAudience,
    held: Vec<String>,
    started_ms: Option<u64>,
    catchup_started_ms: Option<u64>,
    dump_turn: bool,
    locked: bool,
    abandoned: bool,
    // Sealed before replacing the participant ledger's world. Existing audience
    // cleanup on Leave/rebind still removes its recipient connection leases.
    epoch_participants_ready: Option<bool>,
}
impl JoinGather {
    fn progress_report(&self, now: u64) -> String {
        let audience = if matches!(self.audience, GatherAudience::Join(_)) { "join" } else { "fresh" };
        format!("presentation_history_audience={audience}|{}|presentation_history_age_ms={}|presentation_history_held={}",
            self.history.progress_report(), now.saturating_sub(self.started_ms.unwrap_or(now)), self.held.len())
    }
    fn failure_report(&self, world: u64, now: u64, reason: &str) -> String {
        // Report content is bounded before hex framing; native errors can contain
        // delimiters or non-ASCII text and must never create diagnostics fields.
        let reason: String = reason.chars().take(256).collect();
        hex_encode(&format!("world={world}|at_ms={now}|{}|reason={reason}", self.progress_report(now)))
    }
}
struct RecorderConfig {
    args: Vec<String>,
    push_target: Option<String>,
    scope_generation: u64,
    at_ms: u64,
}
struct EpochBarrier {
    epoch: u64,
    pages: usize,
    drained: bool,
}
enum StreamIo {
    Ring,
    PushOff { generation: u64 },
    PushStatus { generation: u64 },
    Routes { world: u64 },
    Cursor { initial: bool },
    Config(RecorderConfig, u64),
    Diag,
    Full,
    Delta(i64),
    Gather,
    Dump { generation: u64 },
    EpochDump,
    StateProbe { scope: crate::visible_state::Scope, ids: Vec<u64> },
    ParticipantWitness(crate::participants::Stamp),
    ParticipantExport(crate::participants::Export),
}
impl StreamIo {
    fn kind(&self) -> usize {
        match self {
            Self::Ring => 0, Self::Routes { .. } => 1, Self::Cursor { .. } => 2,
            Self::Config(_, _) | Self::PushOff { .. } | Self::PushStatus { .. } => 3, Self::Diag => 4, Self::Full => 5, Self::Delta(_) => 6,
            Self::Gather => 7, Self::Dump { .. } => 8, Self::EpochDump => 9,
            Self::StateProbe { .. } => 10, Self::ParticipantWitness(_) | Self::ParticipantExport(_) => 11,
        }
    }
}

pub struct Stream {
    pub options: StreamOptions,
    pub last_tick: i64,
    pub history_trimmed: HashMap<ConnId, u64>,
    pub epoch_dump_limit_hits: u64,
    pub history_failures: u64,
    history_last_failure_hex: String,
    history_last_progress_ms: u64,
    last_pump_ms: u64,
    /// Any gather/epoch publication protects this whole pump from packet policies.
    pub policy_protected: bool,
    pub ladder: Ladder,
    ring: crate::ring_control::Control,
    ring_source_report: String,
    ring_status_due: bool,
    pub report: Vec<String>,
    next_snapshot_ms: u64,
    next_presentation_ms: u64,
    last_presentation_ms: u64,
    cursor: i64,
    targets: HashSet<u64>,
    sim_lots: HashMap<u64, Option<u64>>,
    sim_positions: HashMap<u64, [f32; 3]>,
    /// Off-lot Sims currently within reach of an off-lot claimed Sim, and since when.
    world_near_since: HashMap<u64, u64>,
    /// Which Sims each driver animates, learned from every live and history line passed to clients.
    driver_owners: presentation::DriverOwners,
    transforms_coalesced: u64,
    joiners: HashSet<ConnId>,
    initial_joiners: HashSet<ConnId>,
    pushed: Vec<String>,
    last_world_time: Option<crate::world_time::WorldTime>,
    last_world_time_ms: u64,
    push_targets_sent: Option<String>,
    command_failures: Vec<RoutedEvent>,
    initialization: Initialization,
    route_ack_world: Option<u64>,
    route_retry_at_ms: u64,
    route_config_failures: u64,
    pending_io: Option<StreamIo>,
    cadence_batch: Option<CadenceBatch>,
    configs: VecDeque<RecorderConfig>,
    gathers: VecDeque<JoinGather>,
    presentation_due: bool,
    skip_cursor: bool,
    want_dump: bool,
    dump_pages: usize,
    dump_targets: String,
    drain: crate::stream_work::Drain,
    io_work: crate::stream_work::Io,
    broadcast_due: bool,
    epoch_barrier: Option<EpochBarrier>,
    world_reset_pending: bool,
    world_reset_generation: u64,
    world_cut_failed: bool,
    world_events: Vec<RoutedEvent>,
    world_event_bytes: usize,
    visible_state: crate::visible_state::Publisher,
    state_cadence_due: bool,
    state_probe_turn: bool,
    state_index_world: u64,
    state_index_ready: bool,
    pending_io_world: Option<u64>,
    stale_snapshot_replies: u64,
    state_lease_nonce: u64,
    state_leases: HashMap<(ConnId, u64), u64>,
    state_final_joins: HashSet<ConnId>,
    state_final_fresh: Vec<(HashSet<ConnId>, HashSet<u64>)>,
    state_final_global: bool,
    /// When each object's full live transform last went out to every joined connection.
    live_transforms: HashMap<u64, u64>,
    model_cuts: crate::model_cut::Ledger,
    participants: crate::participants::Ledger,
    participant_turn: bool,
    snapshot_participant_stamp: Option<crate::participants::Stamp>,
    participant_hold_failed: bool,
    participant_world_cancellations: u64,
    participant_world_cancel_hex: String,
    participant_fresh_due: bool,
    history_deliveries: u64,
    /// Longest gaps since the last history delivery: ring lines read, and live lines sent to clients.
    ring_in_last_ms: u64, ring_in_gap_ms: u64, live_out_last_ms: u64, live_out_gap_ms: u64,
}
impl Stream {
    pub fn new(options: StreamOptions) -> Self {
        Self { ladder: Ladder::new(options.ring_requested), ring: crate::ring_control::Control::new(options.ring_requested), ring_source_report: "ring_available=0".into(), ring_status_due:false, options, last_tick: 0, history_trimmed: HashMap::new(),
            epoch_dump_limit_hits: 0, history_failures: 0, history_last_failure_hex: "-".into(), history_last_progress_ms: 0, last_pump_ms: 0,
            policy_protected: false, report: Vec::new(), next_snapshot_ms: 0, next_presentation_ms: 0, last_presentation_ms: 0,
            cursor: -1, targets: HashSet::new(), sim_lots: HashMap::new(), sim_positions: HashMap::new(), world_near_since: HashMap::new(), driver_owners: HashMap::new(), transforms_coalesced: 0, joiners: HashSet::new(), initial_joiners: HashSet::new(), pushed: Vec::new(),
            last_world_time: None, last_world_time_ms: 0, push_targets_sent: None, command_failures: Vec::new(),
            initialization: Initialization::Ring, pending_io: None, cadence_batch: None, configs: VecDeque::new(),
            route_ack_world: None, route_retry_at_ms: 0, route_config_failures: 0,
            gathers: VecDeque::new(), presentation_due: false, skip_cursor: false, want_dump: false,
            dump_pages: 0, dump_targets: String::new(), broadcast_due: false, epoch_barrier: None, world_reset_pending:false, world_reset_generation:0,world_cut_failed:false, world_events:Vec::new(), world_event_bytes:0,
            drain: Default::default(), io_work: Default::default(),
            visible_state: Default::default(), state_cadence_due: false, state_probe_turn: true,
            state_index_world: 0, state_index_ready: true, pending_io_world: None, stale_snapshot_replies: 0,
            state_lease_nonce: 0, state_leases: HashMap::new(), state_final_joins: HashSet::new(), state_final_fresh: Vec::new(), state_final_global: false, live_transforms: HashMap::new(),
            model_cuts: Default::default(), participants: Default::default(), participant_turn: true,
            snapshot_participant_stamp: None, participant_hold_failed: false,
            participant_world_cancellations: 0, participant_world_cancel_hex: "-".into(), participant_fresh_due: false, history_deliveries: 0, ring_in_last_ms: 0, ring_in_gap_ms: 0, live_out_last_ms: 0, live_out_gap_ms: 0 }
    }
    pub fn joined(&mut self, id: ConnId) {
        self.model_cuts.invalidate(id);
        self.joiners.insert(id);
        self.initial_joiners.insert(id);
        self.state_leases.retain(|(conn, _), _| *conn != id);
    }
    pub fn policy_transaction_pending(&self) -> bool { self.history_pending() || self.epoch_barrier.is_some() || self.policy_protected || self.world_reset_pending }
    pub fn world_cut_failed(&self)->bool { self.world_cut_failed }
    pub fn ring_transport_failed(&self)->bool { self.ring.failure.is_some() }
    pub fn participant_hold_failed(&self) -> bool { self.participant_hold_failed }
    fn state_pair_blocked(&self, conn: ConnId, sim: u64) -> bool {
        self.model_cuts.unready(conn) || self.world_reset_pending || self.epoch_barrier.is_some() || self.state_final_global
            || self.joiners.contains(&conn) || self.state_final_joins.contains(&conn)
            || self.state_final_fresh.iter().any(|(recipients, ids)| recipients.contains(&conn) && ids.contains(&sim))
            || self.gathers.iter().any(|gather| !gather.abandoned && match &gather.audience {
                GatherAudience::Join(ids) => ids.contains(&conn),
                GatherAudience::Fresh { counted, joiners } => joiners.contains(&conn)
                    || (counted.contains(&conn) && gather.history.holds_object(sim)),
            })
    }
    pub fn join_history_pending(&self) -> usize {
        let mut ids = self.joiners.clone();
        ids.extend(&self.state_final_joins);
        for gather in self.gathers.iter().filter(|gather| !gather.abandoned) {
            match &gather.audience {
                GatherAudience::Join(joiners) | GatherAudience::Fresh { joiners, .. } => ids.extend(joiners),
            }
        }
        ids.len()
    }
    pub fn fresh_history_pending(&self) -> usize {
        self.gathers.iter().filter(|gather| !gather.abandoned && matches!(gather.audience, GatherAudience::Fresh { .. })).count()
            + self.state_final_fresh.len()
    }
    fn observe_state_world(&mut self, driver: &impl GameDriver) {
        let world = driver.presentation_world_generation();
        if world == self.state_index_world { return; }
        if self.ring.requested && (self.ring.may_push || self.ring.off_pending) && !self.ring.quarantine {
            self.ring.fail("world changed before presentation ring quiescence");
        }
        if driver.participant_lifecycle_enabled() {
            let readiness: Vec<_> = self.gathers.iter().map(|gather| self.gather_participants_ready(gather)).collect();
            for (gather, ready) in self.gathers.iter_mut().zip(readiness) {
                gather.epoch_participants_ready = Some(ready);
                match &gather.audience {
                    GatherAudience::Join(ids) | GatherAudience::Fresh { counted: ids, .. } => self.joiners.extend(ids),
                }
            }
            self.participant_world_cancel_hex = hex_encode(&format!("old_world={}|new_world={world}|reason=obsolete uninitialized participant baseline", self.state_index_world));
        }
        self.state_index_world = world;
        self.route_retry_at_ms = 0;
        self.state_index_ready = false;
        self.sim_lots.clear();
        self.last_tick = 0;
        self.cadence_batch = None;
        self.state_cadence_due = false;
        self.next_snapshot_ms = 0;
    }
    /// Ground distance from an off-lot Sim to the nearest off-lot claimed Sim.
    fn world_distance(&self, id: u64, claims: &[u64]) -> Option<f32> {
        if self.sim_lots.get(&id).copied().flatten() != Some(WORLD_LOT) { return None; }
        let at = self.sim_positions.get(&id)?;
        claims.iter().filter(|claim| self.sim_lots.get(claim).copied().flatten() == Some(WORLD_LOT))
            .filter_map(|claim| self.sim_positions.get(claim))
            .map(|claim| ((at[0] - claim[0]).powi(2) + (at[2] - claim[2]).powi(2)).sqrt())
            .min_by(f32::total_cmp)
    }
    /// Note which off-lot Sims are near an off-lot claimed Sim now; the dwell clock starts on first sight.
    fn observe_world_interest(&mut self, now: u64, claims: &[u64]) {
        let near: HashSet<u64> = self.sim_lots.keys().copied()
            .filter(|id| self.world_distance(*id, claims).is_some_and(|distance| distance <= WORLD_ENTER_M)).collect();
        self.world_near_since.retain(|id, _| near.contains(id));
        for id in near { self.world_near_since.entry(id).or_insert(now); }
    }
    /// The Sims the clients are shown: the claimed Sims, everyone on a claimed Sim's lot, and, for a claimed Sim
    /// on the streets, the off-lot Sims near it. The streets are one lot id for the whole world, so sharing it
    /// says nothing about being in view.
    fn interest_targets(&self, now: u64, claims: &[u64]) -> HashSet<u64> {
        let lots: HashSet<_> = claims.iter().filter_map(|id| self.sim_lots.get(id).copied().flatten())
            .filter(|lot| *lot != WORLD_LOT).collect();
        claims.iter().copied().chain(self.sim_lots.iter().filter(|(id, lot)| match lot {
            Some(lot) if *lot != WORLD_LOT => lots.contains(lot),
            Some(_) => self.world_distance(**id, claims).is_some_and(|distance|
                (self.targets.contains(*id) && distance <= WORLD_EXIT_M)
                || (distance <= WORLD_ENTER_M && self.world_near_since.get(*id).is_some_and(|since| now.saturating_sub(*since) >= WORLD_DWELL_MS))),
            None => false,
        }).map(|(id, _)| *id)).collect()
    }
    fn refresh_state_scope(&mut self, server: &Server, driver: &impl GameDriver) {
        self.observe_state_world(driver);
        let claims: HashSet<_> = driver.claimed_sims().into_iter().collect();
        let claim_list: Vec<_> = claims.iter().copied().collect();
        let interest = self.interest_targets(self.last_pump_ms, &claim_list);
        let mut candidates: Vec<_> = interest.into_iter().filter(|id| self.state_index_ready && !claims.contains(id)).collect();
        candidates.sort_unstable();
        candidates.truncate(crate::visible_state::MAX_CANDIDATES);
        let recipients: Vec<_> = server.joined_connections().into_iter().filter_map(|id|
            server.connection(id)?.player.map(|player| (id.0, player))).collect();
        self.model_cuts.observe(driver.presentation_world_generation(), &recipients);
        let mut sessions: Vec<_> = recipients.iter().filter_map(|(conn, _)| self.model_cuts.session(ConnId(*conn))).collect();
        sessions.sort_by_key(|session| session.conn);
        let mut drain_claims: Vec<_> = claims.iter().copied().collect();
        drain_claims.sort_unstable();
        let had_debt = self.drain.pending();
        if self.drain.observe(driver.presentation_world_generation(), driver.state_authority_generation(), &sessions, &drain_claims) && had_debt {
            self.want_dump = false;
            self.dump_pages = 0;
            self.presentation_due = true;
        }
        let participant_claims: Vec<_> = claims.iter().copied().collect();
        let fresh_claims = self.participants.observe(driver.participant_lifecycle_enabled(),
            driver.presentation_world_generation(), driver.state_authority_generation(), &participant_claims, &sessions);
        self.participant_fresh_due |= !fresh_claims.is_empty();
        if !fresh_claims.is_empty() {
            self.report.push(format!("participant_claims_fresh|count={}|claims={}|authority={}", fresh_claims.len(), claims.len(), driver.state_authority_generation()));
        }
        for sim in fresh_claims { self.targets.remove(&sim); }
        let mut eligible = Vec::new();
        for (conn, player) in &recipients {
            if driver.baseline_pending(ConnId(*conn)) { continue; }
            for sim in &candidates {
                if self.targets.contains(sim) && !self.state_pair_blocked(ConnId(*conn), *sim) { eligible.push((ConnId(*conn), *player, *sim)); }
            }
        }
        let keys: HashSet<_> = eligible.iter().map(|(conn, _, sim)| (*conn, *sim)).collect();
        self.state_leases.retain(|key, _| keys.contains(key));
        let mut leases = Vec::new();
        for (conn, player, sim) in eligible {
            let key = (conn, sim);
            let generation = if let Some(generation) = self.state_leases.get(&key) { *generation } else {
                let Some(generation) = self.state_lease_nonce.checked_add(1) else {
                    self.visible_state.errors = self.visible_state.errors.saturating_add(1);
                    continue; // never reuse an exhausted readiness identity
                };
                self.state_lease_nonce = generation;
                self.state_leases.insert(key, generation);
                generation
            };
            leases.push(crate::visible_state::Lease { conn: conn.0, player, sim, generation });
        }
        self.visible_state.observe(crate::visible_state::Scope { world: driver.presentation_world_generation(),
            authority: driver.state_authority_generation(), recipients, candidates, leases });
    }
    /// Internal producer marker, consumed by the actor before public encoding.
    pub fn is_visible_state_event(&self, event: &RoutedEvent) -> bool { event.event.kind == crate::visible_state::EVENT }
    pub fn state_event_current(&mut self, event: &RoutedEvent, server: &Server, driver: &impl GameDriver) -> bool {
        if event.event.kind != crate::visible_state::EVENT { return true; }
        self.refresh_state_scope(server, driver);
        self.ring.observe(self.drain.scope_generation());
        self.visible_state.recipient(&event.event.fields) == event.requester.map(|id| id.0)
            && event.requester.is_some()
    }
    pub fn state_event_delivered(&mut self, event: &RoutedEvent, delivered: bool, server: &Server, driver: &impl GameDriver) {
        if event.event.kind != crate::visible_state::EVENT { return; }
        self.refresh_state_scope(server, driver);
        self.ring.observe(self.drain.scope_generation());
        self.visible_state.complete(&event.event.fields, delivered);
    }
    pub fn state_report(&self) -> String {
        let mut report = format!("visible_state|candidates={}|probes={}|delivered={}|stale={}|errors={}|world_ready={}|world_generation={}|discarded_snapshots={}|{}|presentation_routes_requested={}|presentation_routes_ready={}|presentation_route_config_failures={}|{}|presentation_history_last_progress_ms={}|presentation_history_last_failure_hex={}|{}|participant_world_cancellations={}|participant_world_cancel_hex={}",
            self.visible_state.scope.candidates.len(), self.visible_state.probes, self.visible_state.delivered,
            self.visible_state.stale, self.visible_state.errors, u8::from(self.state_index_ready),
            self.state_index_world, self.stale_snapshot_replies, self.model_cuts.report(),
            u8::from(self.options.route_stream_requested),
            u8::from(self.options.route_stream_requested && self.routes_ready()), self.route_config_failures,
            self.gathers.front().map(|gather| gather.progress_report(self.last_pump_ms)).unwrap_or_else(|| "presentation_history_stage=idle".into()),
            self.history_last_progress_ms, self.history_last_failure_hex, self.participants.report(),
            self.participant_world_cancellations, self.participant_world_cancel_hex);
        report.push('|');
        report.push_str(&self.drain.report(self.last_pump_ms));
        report.push('|');
        report.push_str(&self.io_work.report(self.last_pump_ms));
        if self.ring.requested { report.push('|');report.push_str(&self.ring.report());report.push('|');report.push_str(&self.ring_source_report); }
        report
    }
    pub fn is_participant_event(&self, event: &RoutedEvent) -> bool {
        matches!(event.event.kind.as_str(), crate::participants::DESCRIPTION | crate::participants::SPAWN)
    }
    pub fn participant_public_kind(&self, event: &RoutedEvent) -> Option<&'static str> {
        match event.event.kind.as_str() {
            crate::participants::DESCRIPTION => Some("SimDescriptionData"),
            crate::participants::SPAWN => Some("SimSpawned"),
            _ => None,
        }
    }
    pub fn participant_event_current(&mut self, event: &RoutedEvent, server: &Server, driver: &impl GameDriver) -> bool {
        if !self.is_participant_event(event) { return true; }
        self.refresh_state_scope(server, driver);
        self.ring.observe(self.drain.scope_generation());
        self.participants.permits(event)
    }
    pub fn participant_event_delivered(&mut self, event: &RoutedEvent, success: bool, server: &Server, driver: &impl GameDriver) {
        self.refresh_state_scope(server, driver);
        self.ring.observe(self.drain.scope_generation());
        self.participants.complete(event, success, self.last_pump_ms);
    }
    fn gather_participants_ready(&self, gather: &JoinGather) -> bool {
        if let Some(ready) = gather.epoch_participants_ready { return ready; }
        match &gather.audience {
            GatherAudience::Join(ids) | GatherAudience::Fresh { counted: ids, .. } =>
                ids.iter().filter(|conn| self.model_cuts.session(**conn).is_some())
                    .all(|conn| self.participants.recipient_ready(*conn)),
        }
    }
    fn routes_ready(&self) -> bool {
        !self.options.route_stream_requested || self.route_ack_world == Some(self.state_index_world)
    }
    /// Private per-recipient model receipt; the actor strips it before encoding.
    pub fn is_model_event(&self, event: &RoutedEvent) -> bool { event.event.kind == crate::model_cut::EVENT }
    pub fn model_event_current(&mut self, event: &RoutedEvent, server: &mut Server, driver: &impl GameDriver) -> bool {
        if !self.is_model_event(event) { return true; }
        self.refresh_state_scope(server, driver);
        self.ring.observe(self.drain.scope_generation());
        if event.requester.is_some_and(|conn| self.model_cuts.permits(&event.event.fields, conn, event.event.tick)) { return true; }
        if let Some(cut) = self.model_cuts.reject(&event.event.fields) { Self::restore_failed_cut(cut, server); }
        false
    }
    fn restore_failed_cut(cut: crate::model_cut::Completion, server: &mut Server) {
        if cut.forced {
            if let Some(connection) = server.connection_mut(cut.conn) {
                if connection.cadence.resync_from_tick == Some(cut.tick) {
                    connection.cadence.resync_from_tick = cut.previous_resync;
                }
            }
        }
    }
    pub fn model_event_delivered(&mut self, event: &RoutedEvent, delivered: bool, server: &mut Server, driver: &impl GameDriver) {
        if !self.is_model_event(event) { return; }
        self.refresh_state_scope(server, driver);
        self.ring.observe(self.drain.scope_generation());
        let Some(conn) = event.requester else { return; };
        if let Some(cut) = self.model_cuts.complete(&event.event.fields, conn, event.event.tick, delivered) {
            if !delivered { Self::restore_failed_cut(cut, server); }
        }
    }
    pub fn history_pending(&self) -> bool {
        !self.joiners.is_empty() || self.gathers.iter().any(|gather| !gather.abandoned)
    }
    pub fn joining(&self) -> bool {
        !self.joiners.is_empty() || self.gathers.iter().any(|gather| !gather.abandoned && match &gather.audience {
            GatherAudience::Fresh { joiners, .. } => !joiners.is_empty(),
            GatherAudience::Join(ids) => !ids.is_empty(),
        })
    }
    /// Existing read-only analytics API, independent of recorder ownership.
    pub fn admin_reply(&self, request: &crate::admin::Request) -> Option<String> {
        crate::admin::local_reply(request)
    }
    /// Opt-in transport ownership prevents out-of-band recorder configuration.
    pub fn managed_admin_reply(&mut self, request: &crate::admin::Request) -> Option<String> {
        if self.ring.requested {
            if matches!(request.name.as_str(), "LOAD_SAVE" | "LOAD_SAVE_MENU") {
                return Some(crate::admin::error("world replacement requires a default-off transport reattach; configured ring lifetime is not a world quiescence proof"));
            }
            if request.name == "SACS_RECORDER" {
                return Some(match request.args.first().map(String::as_str) {
                    Some("status" | "push-status") => crate::admin::report(&format!("{}|{}",self.ring.report(),self.ring_source_report)),
                    Some("push-off") if request.args.len()==1 => {
                        self.ring.manual();
                        if let Some(reason)=&self.ring.failure { crate::admin::error(reason) }
                        else if self.ring.off_ready { crate::admin::report("presentation_push_off=ready") }
                        else { "{\"ok\":true,\"accepted\":false,\"reason\":\"presentation_push_off_pending\",\"report\":null}".into() }
                    }
                    // The motion watch samples objects and never configures the ring: a refused watch leaves a
                    // server-versus-client comparison with no server track.
                    Some(verb) if crate::ring_control::recorder_diagnostic(verb) => return None,
                    _ => crate::admin::error("recorder control is owned by presentation ring transport"),
                });
            }
        }
        None
    }
    pub fn command_failed(&mut self, id: ConnId, corr: u64, error: &str) {
        let mut event = self.event("ServerError", vec![hex_encode(error)], Some(id));
        event.event.corr = corr;
        self.command_failures.push(event);
    }
    pub fn left(&mut self, id: ConnId) {
        self.model_cuts.invalidate(id);
        self.state_leases.retain(|(conn, _), _| *conn != id);
        self.state_final_joins.remove(&id);
        for (recipients, _) in &mut self.state_final_fresh { recipients.remove(&id); }
        self.joiners.remove(&id);
        self.initial_joiners.remove(&id);
        self.history_trimmed.remove(&id);
        for gather in &mut self.gathers {
            match &mut gather.audience {
                GatherAudience::Fresh { counted, joiners } => {
                    counted.retain(|conn| *conn != id);
                    joiners.retain(|conn| *conn != id);
                }
                GatherAudience::Join(ids) => ids.retain(|conn| *conn != id),
            }
        }
    }
    pub fn rebound(&mut self, old: ConnId, new: ConnId) {
        self.model_cuts.invalidate(old);
        self.model_cuts.invalidate(new);
        self.state_leases.retain(|(conn, _), _| *conn != old && *conn != new);
        if self.state_final_joins.remove(&old) { self.state_final_joins.insert(new); }
        for (recipients, _) in &mut self.state_final_fresh {
            if recipients.remove(&old) { recipients.insert(new); }
        }
        if self.joiners.remove(&old) { self.joiners.insert(new); }
        if self.initial_joiners.remove(&old) { self.initial_joiners.insert(new); }
        if let Some(count) = self.history_trimmed.remove(&old) { self.history_trimmed.insert(new, count); }
        for gather in &mut self.gathers {
            match &mut gather.audience {
                GatherAudience::Fresh { counted, joiners } => {
                    for id in counted.iter_mut().chain(joiners) { if *id == old { *id = new; } }
                }
                GatherAudience::Join(counted) => for id in counted { if *id == old { *id = new; } },
            }
        }
    }
    pub fn diag_clock(&mut self, report: &str, now: u64) -> Option<RoutedEvent> {
        let time = crate::world_time::parse_diag(report)?;
        if !crate::world_time::should_reconcile(time, self.last_world_time, now.saturating_sub(self.last_world_time_ms)) { return None; }
        self.last_world_time = Some(time);
        self.last_world_time_ms = now;
        self.report.push(format!("world_time_reconcile|at_ms={now}"));
        Some(self.event("WorldTimeChanged", vec![crate::messages::enc_record(
            &[time.day.to_string(), time.hours.to_string(), time.minutes.to_string()])], None))
    }
    fn event(&self, kind: &str, fields: Vec<String>, requester: Option<ConnId>) -> RoutedEvent {
        RoutedEvent { event: Event { seq: 0, tick: self.last_tick.max(0) as u64, corr: 0, kind: kind.into(), fields },
            owner: None, requester }
    }
    fn cadence_decision(&self, id: ConnId, server: &Server) -> crate::cadence::Decision {
        let cadence = &server.connection(id).expect("joined").cadence;
        let mut decision = cadence.decision(self.last_tick, self.options.base_interval_ms, self.options.thresholds);
        // Preserve real backpressure, and issue at most one recovery cut.
        if self.model_cuts.unready(id) && decision.action != Action::Suppress && cadence.resync_from_tick.is_none() {
            decision.action = Action::ForceFull;
        }
        decision
    }
    pub fn pump(&mut self, now: u64, server: &mut Server, driver: &mut impl GameDriver) -> Result<Vec<RoutedEvent>, DriverError> {
        self.last_pump_ms = now;
        self.drain.begin_turn();
        self.state_final_joins.clear();
        self.state_final_fresh.clear();
        self.state_final_global = false;
        self.policy_protected = self.history_pending() || self.epoch_barrier.is_some();
        let mut events = std::mem::take(&mut self.command_failures);
        let mut result = self.pump_into(now, server, driver, &mut events);
        if self.participant_hold_failed {
            result = Err(DriverError("fresh presentation driver ownership capacity exceeded; actor stopped before partial baseline".into()));
        }
        if self.participants.capacity_failed() {
            self.participant_hold_failed = true;
            result = Err(DriverError("participant ledger capacity exceeded; actor stopped before partial initialization".into()));
        }
        if self.participants.pending() || !self.routes_ready() {
            let mut bytes = self.pushed.iter().map(String::len).sum::<usize>();
            let mut rows = self.pushed.len();
            for gather in &self.gathers {
                for line in &gather.held { bytes = bytes.saturating_add(line.len()); rows = rows.saturating_add(1); }
            }
            if bytes > 16 * 1024 * 1024 || rows > 65_536 {
                self.participant_hold_failed = true;
                result = Err(DriverError("participant initialization hold capacity exceeded; actor stopped before partial baseline".into()));
            }
        }
        match result {
            Ok(()) => Ok(events),
            Err(error) => {
                self.command_failures = events;
                Err(error)
            }
        }
    }
    pub fn terminal_events(&mut self, driver: &mut impl GameDriver, error: &DriverError) -> Vec<RoutedEvent> {
        let mut events = self.take_events(driver);
        events.extend(driver.terminal_events(error));
        events
    }
    pub fn take_events(&mut self, driver: &mut impl GameDriver) -> Vec<RoutedEvent> {
        let mut events = std::mem::take(&mut self.command_failures);
        events.extend(driver.take_pending_events());
        events
    }
    fn pump_into(&mut self, now: u64, server: &mut Server, driver: &mut impl GameDriver,
        events: &mut Vec<RoutedEvent>) -> Result<(), DriverError> {
        for event in driver.poll_routed_events()? {
            if event.event.kind==crate::routing::WORLD_RESET {
                if self.options.sync_policy.is_some() {
                    if self.world_reset_pending {self.world_cut_failed=true;return Err(DriverError("overlapping presentation world cuts".into()));}
                    self.world_reset_pending=true;
                    self.world_reset_generation=event.event.fields.first().and_then(|s|s.parse().ok()).unwrap_or(driver.presentation_world_generation());
                } else {events.push(event);}
            } else if self.world_reset_pending {
                // Hold new-world driver output until the old recorder/history drain.
                // Capacity is explicit and fails visibly, never partially publishes a world cut.
                let bytes=event.event.fields.iter().map(String::len).sum::<usize>().saturating_add(event.event.kind.len()+64);
                if self.world_events.len()>=1024 || self.world_event_bytes.saturating_add(bytes)>4*1024*1024 {
                    self.world_cut_failed=true;
                    self.world_events.clear();
                    self.world_event_bytes=0;
                    return Err(DriverError("world replacement event hold capacity exceeded; actor stopped before partial cut".into()));
                }
                self.world_event_bytes+=bytes;
                self.world_events.push(event);
            } else { events.push(event); }
        }
        // A world replacement needs the same ordered drain even with navigation disabled.
        let pending_epoch = driver.take_epoch_announcement().or(self.world_reset_pending.then_some(0));
        self.observe_state_world(driver);
        self.refresh_state_scope(server, driver);
        self.ring.observe(self.drain.scope_generation());
        if let Some(error)=&self.ring.failure { return Err(DriverError(error.clone())); }
        // This is pending recorder work, independent of the timer reservation
        // for model cadence. The shared slot and all higher-priority cuts remain
        // owned by next_io; a finite yield never rearms inside this same pump.
        if self.drain.resume(now) && self.routes_ready() && !self.ladder.pushing
            && self.epoch_barrier.is_none() && !self.world_reset_pending {
            self.want_dump = true;
        }
        if std::mem::take(&mut self.state_cadence_due) {
            self.visible_state.cadence();
            self.state_probe_turn = true;
        }
        if self.epoch_barrier.as_ref().map(|barrier| barrier.epoch) != pending_epoch {
            self.epoch_barrier = pending_epoch.map(|epoch| EpochBarrier { epoch, pages: 0, drained: false });
        }
        self.ring.epoch(pending_epoch);
        if let Some(barrier) = &mut self.epoch_barrier {
            // Push mode is not an examination/catchup proof.
            if !self.ring.requested { barrier.drained |= self.ladder.pushing; }
        }
        // Materialize the existing atomic Join audience before any page flush,
        // even when native baseline completion falls between cadence deadlines.
        // Retry joiners deliberately keep their existing one-second backoff.
        if driver.participant_lifecycle_enabled() && self.routes_ready()
            && !server.joined_connections().is_empty()
            && !self.world_reset_pending && self.epoch_barrier.is_none()
            && (self.participant_fresh_due || self.initial_joiners.iter().any(|id| !driver.baseline_pending(*id))) {
            self.plan_presentation(now, server, driver);
        }
        if server.joined_connections().is_empty() && self.epoch_barrier.is_none() && !self.ring.requested { return Ok(()); }
        let ids: Vec<_> = server.joined_connections().into_iter().filter(|id| !driver.baseline_pending(*id)).collect();
        if !ids.is_empty() && self.cadence_batch.is_none() && !self.world_reset_pending
            && now >= self.next_snapshot_ms && self.options.base_interval_ms > 0.0 {
            let decisions: Vec<_> = ids.iter().map(|id| self.cadence_decision(*id, server)).collect();
            self.next_snapshot_ms = now + crate::cadence::effective_interval(&decisions, self.options.base_interval_ms);
            let sessions = ids.iter().filter_map(|id| self.model_cuts.session(*id)).collect();
            self.cadence_batch = Some(CadenceBatch { sessions, decisions: ids.into_iter().zip(decisions).collect(),
                stage: CadenceStage::Diag, full: None, delta: None });
        }
        let idle=server.joined_connections().is_empty();
        self.consume_ring(now, driver, idle)?;
        if self.ring.requested && idle && self.epoch_barrier.is_none() {
            self.ring.idle_discarded=self.ring.idle_discarded.saturating_add(self.pushed.len() as u64);
            self.pushed.clear();
            if matches!(self.pending_io,Some(StreamIo::Gather)) {
                self.gathers.truncate(1);
                if let Some(gather)=self.gathers.front_mut(){gather.abandoned=true;gather.held.clear();}
            } else {self.gathers.clear();}
            // No audience owns replay, but the single ring reader must keep
            // consuming/advancing the native stream instead of backing it up.
            if matches!(self.initialization,Initialization::Ready) && !self.ring.manual_stop {
                if self.drain.targets("-") {self.drain.cut_scope();self.ring.observe(self.drain.scope_generation());}
                self.targets.clear();
                if self.ring.can_start() {
                    match self.ladder.evaluate(now) {
                        Some(LadderStep::UseDumps)=>self.ring.stop(),
                        Some(LadderStep::TryPush) if driver.ring_available()=>{
                            self.configs.push_back(RecorderConfig {args:vec!["push-on".into(),self.cursor.to_string(),"-".into()],
                                push_target:Some("-".into()),scope_generation:self.drain.scope_generation(),at_ms:now});
                        }
                        Some(LadderStep::TryPush)=>self.ladder.push_result(now,false),
                        None=>{}
                    }
                }
            }
        }
        if now >= self.next_presentation_ms {
            self.presentation_due = true;
            self.next_presentation_ms = now + self.options.presentation_poll_ms;
            if self.gathers.front().is_some_and(|gather| gather.history.request().is_none()
                && gather.history.result.max_cursor().is_some()) { self.want_dump = true; }
        }
        if self.epoch_barrier.is_none() && self.presentation_due && !server.joined_connections().is_empty()
            && matches!(self.initialization, Initialization::Ready) && self.routes_ready() && self.cadence_batch.is_none() {
            self.plan_presentation(now, server, driver);
        }
        self.abandon_gathers(now, events);
        self.flush_presentation(now, server, events);
        self.finish_frozen_gathers(now, server, events);
        if let Some(io) = self.pending_io.take() {
            let request_world = self.pending_io_world.take();
            match driver.poll_stream_request() {
                Ok(Some(reply)) => {
                    self.finish_io_reply((io, request_world, reply), now, server, driver, events)?;
                }
                Ok(None) => { self.pending_io = Some(io); self.pending_io_world = request_world; return Ok(()); }
                Err(error) => {
                    self.io_work.finish(now, true);
                    self.failed_io(io, events, &error.0, now);
                    return Err(error);
                }
            }
            // Give the driver's pending baseline a turn before another native
            // request is issued. No positive-duration bridge wait runs here.
            self.finish_epoch_barrier(now, driver, events);
            self.flush_presentation(now, server, events);
            return Ok(());
        }
        loop {
            if driver.stream_request_pending() { break; }
            self.finish_epoch_barrier(now, driver, events);
            if server.joined_connections().is_empty() && self.epoch_barrier.is_none() && !self.ring.off_pending && self.configs.is_empty() { break; }
            if self.epoch_barrier.is_none() && self.presentation_due && !server.joined_connections().is_empty()
                && matches!(self.initialization, Initialization::Ready) && self.routes_ready() && self.cadence_batch.is_none() {
                self.plan_presentation(now, server, driver);
            }
            let Some(io) = self.next_io(server, events, now) else {
                if self.epoch_barrier.is_none() && self.presentation_due && !server.joined_connections().is_empty()
                    && matches!(self.initialization, Initialization::Ready) && self.routes_ready() && self.cadence_batch.is_none() { continue; }
                break;
            };
            let request = match &io {
                StreamIo::Ring => StreamRequest::Recorder(vec!["ring-on".into()]),
                StreamIo::Routes { .. } => StreamRequest::Recorder(vec!["routes-on".into()]),
                StreamIo::Cursor { .. } => StreamRequest::Recorder(vec!["dump".into(), presentation::FROM_NOW_CURSOR.to_string()]),
                StreamIo::Config(config, _) => StreamRequest::Recorder(config.args.clone()),
                StreamIo::PushOff { .. } => StreamRequest::Recorder(vec!["push-off".into()]),
                StreamIo::PushStatus { .. } => StreamRequest::Recorder(vec!["push-status".into()]),
                StreamIo::Diag => StreamRequest::DiagState,
                StreamIo::StateProbe { ids, .. } => StreamRequest::ProbeSimState(ids.clone()),
                StreamIo::ParticipantWitness(_) => StreamRequest::Snapshot(-1),
                StreamIo::ParticipantExport(export) => StreamRequest::ExportSimDescription(export.sim.description_id.expect("witnessed description")),
                StreamIo::Full => StreamRequest::Snapshot(-1),
                StreamIo::Delta(since) => StreamRequest::Snapshot(*since),
                StreamIo::Gather => {
                    let gather = self.gathers.front_mut().expect("active gather");
                    gather.locked = true;
                    StreamRequest::Recorder(gather.history.request().expect("unfinished gather"))
                }
                StreamIo::Dump { .. } => StreamRequest::Recorder(vec!["dump".into(), self.cursor.to_string(), self.dump_targets.clone()]),
                StreamIo::EpochDump => {
                    let claims = driver.claimed_sims();
                    let mut targets: Vec<_> = self.interest_targets(self.last_pump_ms, &claims).into_iter().collect();
                    targets.sort_unstable();
                    let targets = if targets.is_empty() { "-".into() }
                        else { targets.iter().map(u64::to_string).collect::<Vec<_>>().join(",") };
                    StreamRequest::Recorder(vec!["dump".into(), self.cursor.to_string(), targets])
                }
            };
            if matches!(io, StreamIo::Full | StreamIo::Delta(_)) {
                self.snapshot_participant_stamp = Some(self.participants.current_stamp());
            }
            let request_world = matches!(io, StreamIo::Diag | StreamIo::Full | StreamIo::Delta(_)).then_some(self.state_index_world);
            self.io_work.start(io.kind(), now);
            match driver.start_stream_request(&request) {
                Ok(Some(reply)) => {
                    self.finish_io_reply((io, request_world, reply), now, server, driver, events)?;
                }
                Ok(None) => { self.pending_io = Some(io); self.pending_io_world = request_world; break; }
                Err(error) => { self.io_work.finish(now, true); self.failed_io(io, events, &error.0, now); return Err(error); }
            }
        }
        self.finish_epoch_barrier(now, driver, events);
        self.flush_presentation(now, server, events);
        Ok(())
    }
    fn finish_epoch_barrier(&mut self, now: u64, driver: &mut impl GameDriver, events: &mut Vec<RoutedEvent>) {
        if self.pending_io.is_some() || driver.stream_request_pending() { return; }
        // The old IO retains its owner until its reply is consumed above. Only
        // then may an uninitialized obsolete-world gather be retired. Its
        // unpublished baseline/held data is never presented as a partial cut.
        let before = self.gathers.len();
        self.gathers.retain(|gather| gather.epoch_participants_ready != Some(false));
        self.participant_world_cancellations += (before - self.gathers.len()) as u64;
        if !self.gathers.is_empty()
            || !self.epoch_barrier.as_ref().is_some_and(|barrier| barrier.drained) { return; }
        let barrier = self.epoch_barrier.take().expect("drained epoch");
        self.policy_protected = true;
        self.state_final_global = true;
        for batch in self.pushed.chunks(presentation::MAX_LINES) {
            events.push(self.event("SimAnimation", vec![enc_list(&batch.iter().map(|line| hex_encode(line)).collect::<Vec<_>>())], None));
        }
        self.pushed.clear();
        self.broadcast_due = false;
        if self.world_reset_pending {
            events.push(crate::routing::world_reset(self.world_reset_generation));
            self.world_reset_pending=false;
        }
        driver.announced(barrier.epoch);
        events.extend(driver.take_pending_events());
        events.append(&mut self.world_events);
        self.world_event_bytes=0;
        self.last_presentation_ms = now;
    }
    fn plan_presentation(&mut self, now: u64, server: &Server, driver: &impl GameDriver) {
        self.presentation_due = false;
        let claims = driver.claimed_sims();
        self.observe_world_interest(now, &claims);
        let target_set = self.interest_targets(now, &claims);
        // A driver none of whose Sims is streamed any more is forgotten: its lines stop arriving, so a later
        // reuse of the handle would go unseen. (Its other actors may be objects, a car seat for one.)
        self.driver_owners.retain(|_, sims| sims.iter().any(|sim| target_set.contains(sim)));
        let mut targets: Vec<_> = target_set.iter().copied().collect();
        targets.sort();
        if targets.is_empty() {
            self.drain.targets("");
            if self.ladder.pushing {
                self.configs.push_back(RecorderConfig { args: vec!["push-targets".into(), "-".into()], push_target: None, scope_generation:self.drain.scope_generation(), at_ms: now });
            } else { self.skip_cursor = true; }
            if !self.targets.is_empty() { self.report.push(format!("presentation_targets_empty|at_ms={now}|prior={}", self.targets.len())); }
            self.targets.clear();
            self.pushed.clear();
            self.want_dump = false;
            self.drain.targets("");
            return;
        }
        let target_field = if targets.is_empty() { "-".into() } else { targets.iter().map(u64::to_string).collect::<Vec<_>>().join(",") };
        if self.drain.targets(&target_field) { self.dump_pages = 0; }
        self.participant_fresh_due = false;
        match if self.ring.can_start() { self.ladder.evaluate(now) } else { None } {
            Some(LadderStep::UseDumps) => {
                if self.ring.requested { self.ring.stop(); }
                else { self.configs.push_back(RecorderConfig { args: vec!["push-off".into()], push_target: None, scope_generation:self.drain.scope_generation(), at_ms: now }); }
                self.push_targets_sent = None;
            }
            Some(LadderStep::TryPush) => {
                if driver.ring_available() {
                    self.configs.push_back(RecorderConfig { args: vec!["push-on".into(), self.cursor.to_string(), target_field.clone()],
                        push_target: Some(target_field.clone()), scope_generation:self.drain.scope_generation(), at_ms: now });
                } else {
                    self.report.push("ring_unavailable|safe_native_reader_not_attached".into());
                    self.ladder.push_result(now, false);
                }
            }
            None => {}
        }
        if self.ladder.pushing && self.push_targets_sent.as_ref() != Some(&target_field) {
            self.configs.push_back(RecorderConfig { args: vec!["push-targets".into(), target_field.clone()], push_target: None, scope_generation:self.drain.scope_generation(), at_ms: now });
            self.push_targets_sent = Some(target_field.clone());
        }
        let fresh: Vec<_> = targets.iter().filter(|id| !self.targets.contains(id)).copied().collect();
        let mut joiners: Vec<_> = self.joiners.iter().copied()
            .filter(|id| server.connection(*id).is_some() && !driver.baseline_pending(*id)).collect();
        joiners.sort();
        for id in &joiners { self.joiners.remove(id); self.initial_joiners.remove(id); }
        let known: Vec<_> = targets.iter().filter(|id| !fresh.contains(id)).copied().collect();
        if !fresh.is_empty() || (!joiners.is_empty() && !known.is_empty()) {
            let claimed: Vec<_> = driver.claimed_sims();
            self.report.push(format!("history_gather_planned|at_ms={now}|fresh={}|fresh_claimed={}|known={}|joiners={}|prior_targets={}",
                fresh.len(), fresh.iter().filter(|id| claimed.contains(id)).count(), known.len(), joiners.len(), self.targets.len()));
        }
        if !fresh.is_empty() {
            self.state_leases.retain(|(_, sim), _| !fresh.contains(sim));
            let mut history = presentation::HistoryGather::new(&fresh);
            // Only when no connection is still waiting for its own baseline has
            // every recipient seen the live transforms recorded below.
            self.live_transforms.retain(|_, at| now.saturating_sub(*at) <= STREAMING_WINDOW_MS);
            if joiners.is_empty() && self.joiners.is_empty() {
                history.skip_streaming(self.live_transforms.keys().copied());
            }
            self.gathers.push_back(JoinGather { history,
                epoch_participants_ready: None,
                audience: GatherAudience::Fresh { counted: server.joined_connections(), joiners: joiners.clone() },
                held: Vec::new(), started_ms: Some(now), catchup_started_ms: None, timing: GatherTiming::default(), dump_turn: false, locked: false, abandoned: false });
        }
        if !joiners.is_empty() && !known.is_empty() {
            self.state_leases.retain(|(conn, _), _| !joiners.contains(conn));
            for gather in &mut self.gathers {
                if let GatherAudience::Join(ids) = &mut gather.audience { ids.retain(|id| !joiners.contains(id)); }
            }
            self.gathers.push_back(JoinGather { history: presentation::HistoryGather::new(&known),
                epoch_participants_ready: None,
                audience: GatherAudience::Join(joiners), held: Vec::new(),
                started_ms: (!self.gathers.iter().any(|gather| matches!(gather.audience, GatherAudience::Fresh { .. }))).then_some(now),
                catchup_started_ms: None, timing: GatherTiming::default(), dump_turn: false, locked: false, abandoned: false });
        }
        self.targets = target_set;
        if !self.ladder.pushing {
            self.want_dump = true;
            if self.dump_pages == 0 { self.dump_targets = target_field; }
        } else { self.broadcast_due = true; }
    }
    fn next_io(&mut self, server: &mut Server, events: &mut Vec<RoutedEvent>, now: u64) -> Option<StreamIo> {
        self.ring.observe(self.drain.scope_generation());
        if self.ring.requested && matches!(self.initialization,Initialization::Ring) { return Some(StreamIo::Ring); }
        if self.ring.requested && !matches!(self.initialization,Initialization::Ring) && self.ring.off_pending {
            return (now>=self.ring.retry_at).then_some(StreamIo::PushOff { generation:self.ring.generation });
        }
        if !self.routes_ready() {
            // A cold native host starts with routes disabled. A warm attachment
            // owns the same explicit setup, before cursor/history or live dumps.
            if matches!(self.initialization, Initialization::Ring) { return Some(StreamIo::Ring); }
            return (now >= self.route_retry_at_ms).then_some(StreamIo::Routes { world: self.state_index_world });
        }
        if self.ring.requested && matches!(self.initialization,Initialization::Cursor) && self.epoch_barrier.is_none() {
            return Some(StreamIo::Cursor { initial:true });
        }
        while let Some(batch) = &mut self.cadence_batch {
            match batch.stage {
                CadenceStage::Diag => return Some(StreamIo::Diag),
                CadenceStage::Full => {
                    if !self.state_index_ready || batch.decisions.iter().any(|(_, decision)| decision.action == Action::ForceFull) { return Some(StreamIo::Full); }
                    batch.stage = CadenceStage::Delta;
                }
                CadenceStage::Delta => {
                    // The bootstrap full is the model AND index cut. Reading a
                    // newer delta but sending only the full would skip that
                    // delta's model changes when last_tick advances. Read them
                    // next cadence, starting at this full's published watermark.
                    if !self.state_index_ready && batch.full.is_some() {
                        batch.stage = CadenceStage::Publish;
                        continue;
                    }
                    if batch.decisions.iter().any(|(_, decision)| matches!(decision.action, Action::PublishDelta | Action::SlowAndPublish)) {
                        return Some(StreamIo::Delta(self.last_tick));
                    }
                    batch.stage = CadenceStage::Publish;
                }
                CadenceStage::Publish => self.publish_cadence(server, events),
            }
        }
        match self.initialization {
            Initialization::Ring => return Some(StreamIo::Ring),
            Initialization::Cursor if self.epoch_barrier.is_none() => return Some(StreamIo::Cursor { initial: true }),
            Initialization::Cursor | Initialization::Ready => {}
        }
        // An existing gather owns LastTargets through its final page. Finish its
        // old history/held lines before draining and announcing a new epoch.
        if self.epoch_barrier.is_some() {
            if let Some(gather) = self.gathers.front() {
                return if gather.history.request().is_some() { Some(StreamIo::Gather) }
                    else { self.want_dump.then_some(StreamIo::EpochDump) };
            }
            return self.epoch_barrier.as_ref().filter(|barrier| !barrier.drained).map(|_| StreamIo::EpochDump);
        }
        if self.ring_status_due && self.ring.requested && self.ring.may_push && !self.ring.off_pending {
            self.ring_status_due=false;
            return Some(StreamIo::PushStatus { generation:self.ring.generation });
        }
        if !self.gathers.front().is_some_and(|gather| gather.locked) {
            while let Some(config) = self.configs.pop_front() {
                if self.ring.requested && config.scope_generation!=self.ring.generation {
                    self.ring.stale_receipts=self.ring.stale_receipts.saturating_add(1);
                    self.push_targets_sent=None;
                    self.ladder.report.push(format!("push_config_dropped|at_ms={now}|queued_ms={}|verb={}|reason=scope_changed",config.at_ms,config.args[0]));
                    self.ladder.quiesced(now);
                    continue;
                }
                // A push-on names its targets. One queued before the targets changed would arm the
                // push for Sims nobody watches any more; drop it and let the retry carry the new set.
                if self.ring.requested && config.push_target.as_deref().is_some_and(|targets| targets!=self.drain.target_field()) {
                    self.push_targets_sent=None;
                    self.ladder.report.push(format!("push_config_dropped|at_ms={now}|queued_ms={}|verb={}|reason=targets_changed",config.at_ms,config.args[0]));
                    self.ladder.quiesced(now);
                    continue;
                }
                if self.ring.requested && !self.ring.can_start() {
                    // A push-on dropped here must leave a retry scheduled: without one the push
                    // stays off until some later scope cut happens to re-arm it.
                    self.push_targets_sent=None;
                    self.ladder.report.push(format!("push_config_dropped|at_ms={now}|queued_ms={}|verb={}|reason=off_pending={},quarantine={},manual={}",
                        config.at_ms,config.args[0],u8::from(self.ring.off_pending),u8::from(self.ring.quarantine),u8::from(self.ring.manual_stop)));
                    if config.push_target.is_some() { self.ladder.quiesced(now); }
                    continue;
                }
                // A target update queued while pushing is void once the push went off: sent then,
                // the game answers push=off and that would score as a refusal. The next push-on
                // carries the targets.
                if self.ring.requested && config.push_target.is_none() && !self.ring.may_push {
                    self.push_targets_sent=None;
                    self.ladder.report.push(format!("push_targets_void|at_ms={now}|queued_ms={}",config.at_ms));
                    continue;
                }
                if self.ring.requested { self.ring.start(); }
                if now.saturating_sub(config.at_ms)>=1000 {
                    self.ladder.report.push(format!("push_config_waited|at_ms={now}|queued_ms={}|verb={}",config.at_ms,config.args[0]));
                }
                let generation=config.scope_generation;
                return Some(StreamIo::Config(config,generation));
            }
        }
        if self.state_probe_turn || (!self.want_dump && self.gathers.is_empty()) {
            if let Some((scope, ids)) = self.visible_state.request() {
                self.state_probe_turn = false;
                return Some(StreamIo::StateProbe { scope, ids });
            }
        }
        if self.participant_turn || (!self.want_dump && self.gathers.front().is_none_or(|gather| !self.gather_participants_ready(gather))) {
            if let Some(export) = self.participants.export(now) {
                self.participant_turn = false;
                return Some(StreamIo::ParticipantExport(export));
            }
            if let Some(stamp) = self.participants.witness_stamp(now) {
                self.participant_turn = false;
                return Some(StreamIo::ParticipantWitness(stamp));
            }
        }
        let participants_ready = self.gathers.front().is_none_or(|gather| self.gather_participants_ready(gather));
        if let Some(gather) = self.gathers.front_mut() {
            if !participants_ready && !gather.locked { gather.started_ms = None; }
            else if participants_ready && gather.started_ms.is_none() { gather.started_ms = Some(now); }
        }
        if self.gathers.front().is_some_and(|gather| gather.dump_turn && gather.history.can_interleave_dump())
            && self.want_dump && (!self.ring.requested || (!self.ladder.pushing && !self.ring.may_push)) && self.drain.allowed(now) { return Some(StreamIo::Dump { generation: self.drain.generation() }); }
        // Capture has no client-side effects. Select/Boundary pages retain their
        // protected LastTargets ownership, but must be allowed to classify the
        // unrelated established drivers even while a participant export retries.
        if self.gathers.front().is_some_and(|gather| gather.history.request().is_some()) { return Some(StreamIo::Gather); }
        if self.skip_cursor { return Some(StreamIo::Cursor { initial: false }); }
        if self.want_dump && (!self.ring.requested || (!self.ladder.pushing && !self.ring.may_push)) && self.drain.allowed(now) { return Some(StreamIo::Dump { generation: self.drain.generation() }); }
        None
    }
    fn prepare_gather_reply(&mut self, io: &StreamIo, reply: &StreamReply, now: u64,
        server: &Server, driver: &mut impl GameDriver, events: &mut Vec<RoutedEvent>) -> Result<(), DriverError> {
        if let (StreamIo::PushOff { generation },StreamReply::Recorder(report))=(io,reply) {
            if *generation==self.ring.generation && crate::ring_control::push_ack(report,"off") {
                self.consume_ring(now,driver,server.joined_connections().is_empty())?;
            }
            return Ok(());
        }
        if matches!(io, StreamIo::Gather) && self.gathers.front().is_some_and(|gather| gather.epoch_participants_ready == Some(false)) { return Ok(()); }
        let StreamReply::Recorder(report) = reply else { return Ok(()); };
        if !matches!(io, StreamIo::Gather) || !report.split('\n').next().unwrap_or("").split(';')
            .any(|field| field.starts_with("snapshot=") || field.starts_with("cursor=")) { return Ok(()); }
        // A live bridge may collect pushed pages while completing this final snapshot reply.
        // Hold those lines before removing the joiner, even inside the ordinary pacing window.
        self.consume_ring(now,driver,server.joined_connections().is_empty())?;
        self.flush_presentation_inner(now, server, events, true);
        Ok(())
    }
    fn consume_ring(&mut self, now:u64, driver:&mut impl GameDriver, idle:bool)->Result<(),DriverError> {
        self.ring_source_report=driver.ring_report();
        if self.ring.requested && self.ring.quarantine {
            match driver.discard_pages() {
                Ok(count)=>self.ring.startup_discarded=self.ring.startup_discarded.saturating_add(count as u64),
                Err(error)=>{self.ring.fail(&error.0);return Err(error);}
            }
            self.ring_source_report=driver.ring_report();
            return Ok(());
        }
        let pages=match driver.presentation_pages(){Ok(pages)=>pages,Err(error)=>{if self.ring.requested{self.ring.fail(&error.0);}return Err(error);}};
        if self.ring.requested && (pages.len()>4096 || pages.iter().map(String::len).sum::<usize>()>8*1024*1024) {
            self.ring.fail("presentation ring batch capacity exceeded");
            return Err(DriverError("presentation ring batch capacity exceeded".into()));
        }
        if self.ring.requested {
            if let Some(fault)=pages.iter().find_map(|page| crate::ring_control::page_fault(page)) {
                self.ring.fail("native pushed page framing malformed");
                return Err(DriverError(format!("native pushed page framing malformed: {fault}")));
            }
        }
        for page in pages {
            let lines=self.ladder.pushed_page(now,&page,&mut self.cursor);
            if !lines.is_empty() {
                if self.ring_in_last_ms != 0 { self.ring_in_gap_ms = self.ring_in_gap_ms.max(now.saturating_sub(self.ring_in_last_ms)); }
                self.ring_in_last_ms = now;
            }
            if self.ring.requested && (idle || self.targets.is_empty()) {
                self.ring.idle_discarded=self.ring.idle_discarded.saturating_add(lines.len() as u64);
            } else {self.pushed.extend(lines);}
        }
        self.ring_source_report=driver.ring_report();
        // A pushed line goes out with the pump that read it. Waiting for the next presentation poll
        // held every line up to 250 ms and turned per-frame transforms (a car in motion) into 4 Hz hops.
        if self.ladder.pushing && !self.pushed.is_empty() { self.broadcast_due = true; }
        if self.ring.requested && (self.pushed.len()>65_536 || self.pushed.iter().map(String::len).sum::<usize>()>8*1024*1024) {
            self.ring.fail("presentation ring actor hold capacity exceeded");
            return Err(DriverError("presentation ring actor hold capacity exceeded".into()));
        }
        Ok(())
    }
    fn finish_io_reply(&mut self, completed: (StreamIo, Option<u64>, StreamReply), now: u64,
        server: &mut Server, driver: &mut impl GameDriver, events: &mut Vec<RoutedEvent>) -> Result<(), DriverError> {
        let (io, request_world, reply) = completed;
        self.observe_state_world(driver);
        self.refresh_state_scope(server, driver);
        self.ring.observe(self.drain.scope_generation());
        if request_world.is_some_and(|world| world != self.state_index_world) {
            self.io_work.finish(now, false);
            self.stale_snapshot_replies += 1;
        } else {
            let result = self.prepare_gather_reply(&io, &reply, now, server, driver, events)
                .and_then(|()| self.complete_io(io, reply, now, events));
            self.io_work.finish(now, result.is_err());
            result?;
        }
        self.finish_frozen_gathers(now, server, events);
        Ok(())
    }
    fn complete_io(&mut self, io: StreamIo, reply: StreamReply, now: u64,
        events: &mut Vec<RoutedEvent>) -> Result<(), DriverError> {
        if matches!(io, StreamIo::Dump { .. } | StreamIo::Gather | StreamIo::StateProbe { .. }) { self.participant_turn = true; }
        match (io, reply) {
            (StreamIo::ParticipantWitness(stamp), StreamReply::Snapshot(Some(snapshot))) => self.participants.witness(stamp, &snapshot.sims, now),
            (StreamIo::ParticipantWitness(_), _) => self.participants.failed_witness(now, "participant snapshot unavailable"),
            (StreamIo::ParticipantExport(export), StreamReply::Description(payload)) => {
                events.extend(self.participants.prepared(&export, &payload, now, self.last_tick.max(0) as u64));
            }
            (StreamIo::ParticipantExport(export), _) => self.participants.failed_export(&export, now, "participant export reply unavailable"),
            (StreamIo::Ring, StreamReply::Recorder(report)) => {
                if self.ring.requested {
                    let mut fields=report.split(';').filter_map(|p|p.trim().split_once('='))
                        .filter(|(k,_)|*k=="ring").map(|(_,v)|v);
                    if fields.next()!=Some("on") || fields.next().is_some() || crate::ring_control::reports_error(&report) {
                        self.ring.fail("ring-on acknowledgement malformed or refused");
                        return Err(DriverError("ring-on acknowledgement malformed or refused".into()));
                    }
                }
                self.initialization = Initialization::Cursor;
            }
            (StreamIo::Ring, _) if self.ring.requested => {
                self.ring.fail("ring-on reply type mismatch");return Err(DriverError("ring-on reply type mismatch".into()));
            }
            (StreamIo::PushOff { generation }, StreamReply::Recorder(report)) => {
                if generation==self.ring.generation && crate::ring_control::push_ack(&report,"off")
                    && self.ring.may_push && !self.ring.native_status(&report) { return Err(DriverError("native push continuity failure".into())); }
                if self.ring.acknowledge_off(generation,&report,now) {
                    self.ladder.report.push(format!("push_off|at_ms={now}|generation={generation}"));
                    self.ladder.quiesced(now);
                    self.push_targets_sent=None;
                }
            }
            (StreamIo::PushOff { .. }, _) => self.ring.failed_off(now),
            (StreamIo::PushStatus { generation }, StreamReply::Recorder(report)) => {
                if generation==self.ring.generation && (!crate::ring_control::push_ack(&report,"on") || !self.ring.native_status(&report)) {
                    self.ring.fail("native push status failed owned transport check");
                    // The first recorded reason names which check failed (overwrite counter, missing field).
                    return Err(DriverError(format!("native push status failed owned transport check: {} | {}",
                        self.ring.failure.as_deref().unwrap_or("-"), report.chars().take(400).collect::<String>())));
                }
            }
            (StreamIo::PushStatus { .. }, _) => {self.ring.fail("native push status reply malformed");return Err(DriverError("native push status reply malformed".into()));}
            (StreamIo::Routes { world }, reply) => {
                let acknowledged = match &reply {
                    StreamReply::Recorder(report) => {
                        let mut fields = report.split(';').filter(|field| field.starts_with("routes="));
                        fields.next() == Some("routes=on") && fields.next().is_none()
                    }
                    _ => false,
                };
                if !acknowledged {
                    self.routes_failed(world, now);
                    return Err(DriverError("route_stream_configuration_unacknowledged".into()));
                }
                // An old native reply cannot release the new world's setup gate.
                if world == self.state_index_world {
                    self.route_ack_world = Some(world);
                    self.route_retry_at_ms = 0;
                    self.report.push(format!("route_stream_ready|world={world}"));
                }
            }
            (StreamIo::Cursor { initial }, StreamReply::Recorder(report)) => {
                if self.ring.requested && !crate::ring_control::valid_page(&report) {
                    self.ring.fail("initial recorder cursor acknowledgement malformed");
                    return Err(DriverError("initial recorder cursor acknowledgement malformed".into()));
                }
                let seq = presentation::parse_dump(&report).0;
                self.cursor = self.cursor.max(seq.max(0));
                if initial { self.initialization = Initialization::Ready; self.ring.quarantine=false; } else { self.skip_cursor = false; }
            }
            (StreamIo::Cursor { .. }, _) if self.ring.requested => {
                self.ring.fail("recorder cursor reply type mismatch");return Err(DriverError("recorder cursor reply type mismatch".into()));
            }
            (StreamIo::Config(config, generation), StreamReply::Recorder(report)) => {
                if self.ring.requested {
                    // A scope cut or stop that overtook this push-on is this server's own
                    // barrier, not a native refusal: the acknowledged off re-arms the push
                    // at once. Only a refusal by the game backs the retry off.
                    let overtaken=self.ring.overtaken(generation);
                    if !self.ring.acknowledge_on(generation,&report) {
                        if overtaken { self.ladder.report.push(format!("push_overtaken|at_ms={}|generation={generation}",config.at_ms)); }
                        else {
                            self.ladder.report.push(format!("push_refused|at_ms={}|generation={generation}|report={}",config.at_ms,
                                report.chars().take(240).collect::<String>().replace('\n'," / ")));
                            self.ladder.push_result(config.at_ms,false);
                        }
                        self.push_targets_sent=None;
                        return Ok(());
                    }
                }
                if self.ring.requested && !self.ring.native_status(&report) { return Err(DriverError("native push continuity failure".into())); }
                if let Some(target) = config.push_target {
                    let accepted=if self.ring.requested { self.ring.acknowledge_on(generation,&report) }
                        else { report.contains("push=on") };
                    self.ladder.push_result(config.at_ms, accepted);
                    if self.ladder.pushing {
                        self.push_targets_sent = Some(target);
                        self.want_dump = false;
                        self.dump_pages = 0;
                        self.drain.clear();
                    }
                }
            }
            (StreamIo::Diag, StreamReply::DiagState(report)) => {
                self.ring_status_due=self.ring.requested && self.ring.may_push;
                if let Some(report) = report { if let Some(event) = self.diag_clock(&report, now) { events.push(event); } }
                self.cadence_batch.as_mut().expect("cadence request").stage = CadenceStage::Full;
            }
            (StreamIo::Full, StreamReply::Snapshot(snapshot)) => {
                if let (Some(stamp), Some(snapshot)) = (self.snapshot_participant_stamp.take(), snapshot.as_ref()) {
                    self.participants.witness(stamp, &snapshot.sims, now);
                }
                let batch = self.cadence_batch.as_mut().expect("cadence request");
                batch.full = snapshot; batch.stage = CadenceStage::Delta;
            }
            (StreamIo::Delta(_), StreamReply::Snapshot(snapshot)) => {
                if let (Some(stamp), Some(snapshot)) = (self.snapshot_participant_stamp.take(), snapshot.as_ref()) {
                    self.participants.witness(stamp, &snapshot.sims, now);
                }
                let batch = self.cadence_batch.as_mut().expect("cadence request");
                batch.delta = snapshot; batch.stage = CadenceStage::Publish;
            }
            (StreamIo::Gather, StreamReply::Recorder(report)) => {
                let participants_ready = self.gathers.front().is_some_and(|gather| self.gather_participants_ready(gather));
                let gather = self.gathers.front_mut().expect("gather request");
                if gather.epoch_participants_ready == Some(false) { return Ok(()); }
                if !gather.abandoned {
                    if let Err(error) = gather.history.accept(&report) {
                        self.failed_io(StreamIo::Gather, events, &error.0, now);
                        return Err(error);
                    }
                    self.history_last_progress_ms = now;
                    let started = gather.started_ms.unwrap_or(now);
                    gather.timing.reply(now, started);
                }
                if !gather.abandoned && gather.history.can_interleave_dump() && !self.ladder.pushing {
                    gather.dump_turn = true;
                    self.want_dump = true;
                }
                if !gather.abandoned && gather.history.request().is_none() && gather.history.result.max_cursor().is_some() {
                    // A dump/push prefix not yet read may still be covered by the frozen snapshot.
                    // Keep the audience held until the ordered global cursor catches up.
                    gather.catchup_started_ms.get_or_insert(now);
                    self.want_dump = true;
                } else if (gather.abandoned || gather.history.request().is_none()) && participants_ready {
                    self.hold_fresh_lines();
                    let gather = self.gathers.pop_front().expect("finished gather");
                    let fresh = matches!(gather.audience, GatherAudience::Fresh { .. });
                    for line in &gather.history.result.lines { presentation::learn_driver_owner(&mut self.driver_owners, line, false); }
                    self.deliver_gather(gather, events);
                    if fresh {
                        for waiting in &mut self.gathers {
                            if matches!(waiting.audience, GatherAudience::Fresh { .. }) { break; }
                            if waiting.started_ms.is_none() { waiting.started_ms = Some(now); }
                        }
                    }
                    self.broadcast_due = true;
                }
            }
            (StreamIo::Dump { generation }, StreamReply::Recorder(report)) => {
                self.state_probe_turn = true;
                let (seq, more, lines) = presentation::parse_dump(&report);
                if seq >= 0 { self.cursor = self.cursor.max(seq); }
                let continue_turn = self.drain.complete(generation, more, now, &lines);
                self.ladder.delivered(&lines);
                self.pushed.extend(lines);
                self.broadcast_due = true;
                if let Some(gather) = self.gathers.front_mut() { gather.dump_turn = false; }
                self.dump_pages = (self.dump_pages + 1) % presentation::MAX_DUMP_PAGES;
                if !continue_turn {
                    self.want_dump = false; self.dump_pages = 0; self.broadcast_due = true;
                    if generation != self.drain.generation() { self.presentation_due = true; }
                }
            }
            (StreamIo::EpochDump, StreamReply::Recorder(report)) => {
                let (seq, more, lines) = presentation::parse_dump(&report);
                if seq >= 0 { self.cursor = self.cursor.max(seq); }
                self.ladder.delivered(&lines);
                self.pushed.extend(lines);
                if !more { self.want_dump = false; }
                if matches!(self.initialization, Initialization::Cursor) { self.initialization = Initialization::Ready; }
                if let Some(barrier) = &mut self.epoch_barrier {
                    barrier.pages = (barrier.pages + 1).min(MAX_EPOCH_DUMP_PAGES);
                    barrier.drained = !more;
                    if more && barrier.pages == MAX_EPOCH_DUMP_PAGES {
                        self.epoch_dump_limit_hits += 1;
                        return Err(DriverError("navigation epoch dump page limit reached".into()));
                    }
                }
            }
            (StreamIo::StateProbe { scope, ids }, StreamReply::SimState(report)) => {
                for sample in self.visible_state.accept(&scope, &ids, &report)? {
                    events.push(self.event(crate::visible_state::EVENT, sample.fields(), Some(ConnId(sample.lease.conn))));
                }
            }
            _ => return Err(DriverError("stream reply does not match its request".into())),
        }
        Ok(())
    }
    fn publish_cadence(&mut self, server: &mut Server, events: &mut Vec<RoutedEvent>) {
        self.state_cadence_due = true;
        let world_full = !self.state_index_ready;
        let CadenceBatch { decisions, sessions, full, delta, .. } = self.cadence_batch.take().expect("cadence batch");
        let merged = match (&full, &delta) {
            (Some(full), Some(delta)) => match full.with_later_delta(delta) {
                Some(snapshot) => Some(snapshot),
                None => {
                    // Native reads from different worlds or a regressing clock
                    // cannot form a coherent cut. Force a fresh complete index.
                    self.state_index_ready = false;
                    self.sim_lots.clear();
                    self.next_snapshot_ms = 0;
                    for session in sessions { if self.model_cuts.current(session) { self.model_cuts.mark_unready(session.conn); } }
                    self.report.push("model_cut_rejected|reason=incoherent_full_delta".into());
                    return;
                }
            },
            _ => full.clone(),
        };
        let tick = full.as_ref().map_or(0, |snapshot| snapshot.tick).max(delta.as_ref().map_or(0, |snapshot| snapshot.tick));
        for (id, decision) in decisions {
            let Some(session) = sessions.iter().copied().find(|session| session.conn == id)
                .filter(|session| self.model_cuts.current(*session)) else { continue; };
            if decision.action == Action::Suppress {
                if full.is_some() || delta.as_ref().is_some_and(|snapshot| !snapshot.sims.is_empty() || !snapshot.objects.is_empty()) {
                    self.model_cuts.mark_unready(id);
                }
                continue;
            }
            let forced = world_full || decision.action == Action::ForceFull;
            let snapshot = if forced { merged.as_ref() } else { delta.as_ref() };
            if let Some(snapshot) = snapshot {
                if tick <= 0 || (snapshot.sims.is_empty() && snapshot.objects.is_empty() && !forced) { continue; }
                let Some(connection) = server.connection_mut(id) else { continue; };
                let previous = connection.cadence.resync_from_tick;
                let Some(fields) = self.model_cuts.prepare(session, forced, snapshot.tick, previous, snapshot.wire_body()) else { continue; };
                if forced { connection.cadence.force_full(snapshot.tick); }
                let mut event = self.event(crate::model_cut::EVENT, fields, Some(id));
                event.event.tick = snapshot.tick.max(0) as u64;
                events.push(event);
                self.report.push(format!("resync|player={}|action={:?}|tick={}", server.connection(id).and_then(|conn| conn.player).unwrap_or(0), decision.action, snapshot.tick));
            }
        }
        if tick > 0 { self.last_tick = tick; }
        if full.is_some() { self.sim_lots.clear(); self.sim_positions.clear(); }
        if full.is_some() { self.state_index_ready = true; }
        // A delta read follows the full read and can move an entity between lots.
        // Advancing last_tick past it while retaining only the full index would
        // leave stationary entities permanently scoped to their previous lot.
        for snapshot in [full.as_ref(), delta.as_ref()].into_iter().flatten() {
            self.sim_lots.extend(snapshot.sims.iter().map(|sim| (sim.object_id, sim.lot_id)));
            self.sim_positions.extend(snapshot.sims.iter().map(|sim| (sim.object_id, sim.position)));
        }
    }
    fn deliver_gather(&mut self, mut gather: JoinGather, events: &mut Vec<RoutedEvent>) {
        self.policy_protected = true;
        let audience_name = if matches!(gather.audience, GatherAudience::Join(_)) { "join" } else { "fresh" };
        self.history_deliveries += 1;
        self.report.push(format!("history_delivered|n={}|audience={audience_name}|abandoned={}|history_lines={}|held_lines={}|streaming_skipped={}|other_sims_not_held={}|transforms_coalesced={}|started_ms={}|first_reply_ms={}|replies={}|slowest_reply_ms={}|catchup_ms={}|delivered_ms={}|ring_in_last_ms={}|ring_in_gap_ms={}|live_out_last_ms={}|live_out_gap_ms={}|unsent={}",
            self.history_deliveries, u8::from(gather.abandoned), gather.history.result.lines.len(), gather.held.len(), gather.history.streaming_skipped, gather.history.other_sims_not_held, self.transforms_coalesced,
            gather.started_ms.unwrap_or(0), gather.timing.first_reply_ms, gather.timing.replies, gather.timing.slowest_reply_ms,
            gather.catchup_started_ms.unwrap_or(0), self.last_pump_ms,
            self.ring_in_last_ms, self.ring_in_gap_ms, self.live_out_last_ms, self.live_out_gap_ms, self.pushed.len()));
        self.ring_in_gap_ms = 0; self.live_out_gap_ms = 0;
        // Diagnostic capture of what a gather sends, for a live run: one file per delivery.
        if let Some(dir) = std::env::var_os("SIMSONLINE_HISTORY_CAPTURE_DIR") {
            let stamp = std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).map_or(0, |d| d.as_millis());
            let path = std::path::Path::new(&dir).join(format!("history-{:04}-{audience_name}-{stamp}.txt", self.history_deliveries));
            let body = format!("{}
--held--
{}
", gather.history.result.lines.join("
"), gather.held.join("
"));
            let _ = std::fs::write(path, body);
        }
        match &gather.audience {
            GatherAudience::Join(ids) => { self.state_final_joins.extend(ids); }
            GatherAudience::Fresh { counted, joiners } => {
                self.state_final_joins.extend(joiners);
                self.state_final_fresh.push((counted.iter().copied().collect(), gather.history.held_object_ids().collect()));
            }
        }
        self.state_leases.retain(|(conn, sim), _| !self.state_final_joins.contains(conn)
            && !self.state_final_fresh.iter().any(|(recipients, ids)| recipients.contains(conn) && ids.contains(sim)));
        match gather.audience {
            GatherAudience::Fresh { counted, .. } => {
                if gather.abandoned { gather.history.result = presentation::History::default(); }
                for id in counted { *self.history_trimmed.entry(id).or_default() += gather.history.result.history_trimmed; }
                gather.history.result.append_live(std::mem::take(&mut gather.held));
                self.pushed.extend(gather.history.result.lines);
            }
            GatherAudience::Join(ids) => {
                if gather.abandoned { gather.history.result = presentation::History::default(); }
                gather.history.result.append_live(std::mem::take(&mut gather.held));
                let lines = gather.history.result.lines;
                for id in ids {
                    *self.history_trimmed.entry(id).or_default() += if gather.abandoned { 0 } else { gather.history.result.history_trimmed };
                    for batch in lines.chunks(presentation::MAX_LINES) {
                        events.push(self.event("SimAnimation", vec![enc_list(&batch.iter().map(|line| hex_encode(line)).collect::<Vec<_>>())], Some(id)));
                    }
                }
            }
        }
    }
    fn finish_frozen_gathers(&mut self, now: u64, server: &Server, events: &mut Vec<RoutedEvent>) {
        if self.pending_io.is_some() { return; }
        while self.gathers.front().is_some_and(|gather| gather.history.request().is_none()
            && self.gather_participants_ready(gather)
            && gather.history.result.max_cursor().is_none_or(|cursor| gather.abandoned || self.cursor >= cursor)) {
            self.flush_presentation_inner(now, server, events, true);
            let gather = self.gathers.pop_front().expect("complete frozen gather");
            let fresh = matches!(gather.audience, GatherAudience::Fresh { .. });
            self.deliver_gather(gather, events);
            if fresh {
                for waiting in &mut self.gathers {
                    if matches!(waiting.audience, GatherAudience::Fresh { .. }) { break; }
                    if waiting.started_ms.is_none() { waiting.started_ms = Some(now); }
                }
            }
            self.broadcast_due = true;
        }
    }
    fn abandon_gathers(&mut self, now: u64, events: &mut Vec<RoutedEvent>) {
        if !self.gathers.is_empty() { self.policy_protected = true; }
        let mut deliveries = Vec::new();
        let mut retry = Vec::new();
        let mut failures = 0;
        let blocked: Vec<_> = self.gathers.iter().map(|gather| !self.gather_participants_ready(gather)).collect();
        for (gather, blocked) in self.gathers.iter_mut().zip(blocked) {
            if gather.epoch_participants_ready == Some(false) { continue; }
            if blocked {
                // Initialization is a separate acknowledged queue phase. It
                // must not spend the recorder/cursor deadline before it starts.
                gather.started_ms = None;
                gather.catchup_started_ms = None;
                continue;
            }
            let completed_frozen = gather.history.request().is_none() && gather.history.result.max_cursor().is_some();
            let Some(started) = (if completed_frozen { gather.catchup_started_ms } else { gather.started_ms }) else { continue; };
            if gather.abandoned || now.saturating_sub(started) < 30_000 { continue; }
            if matches!(gather.audience, GatherAudience::Fresh { .. }) && !completed_frozen { continue; }
            self.history_last_failure_hex = gather.failure_report(self.state_index_world, now,
                if completed_frozen { "frozen cursor catchup deadline" } else { "join history gather deadline" });
            if let GatherAudience::Fresh { counted, .. } = &gather.audience {
                self.report.push("join_history_abandoned|limit_ms=30000|reason=frozen_cursor_catchup|audience=fresh".into());
                retry.extend(counted.iter().copied());
                failures += 1;
                gather.abandoned = true;
                self.pushed.extend(std::mem::take(&mut gather.held));
                self.broadcast_due = true;
                continue;
            }
            let GatherAudience::Join(ids) = &mut gather.audience else { continue; };
            self.report.push(format!("join_history_abandoned|limit_ms=30000|held={}|joiners={}", gather.held.len(), ids.len()));
            retry.extend(ids.iter().copied());
            failures += 1;
            deliveries.push((std::mem::take(ids), std::mem::take(&mut gather.held)));
            gather.abandoned = true;
        }
        self.retry_history(retry, now, failures);
        for (ids, lines) in deliveries {
            for id in ids {
                for batch in lines.chunks(presentation::MAX_LINES) {
                    events.push(self.event("SimAnimation", vec![enc_list(&batch.iter().map(|line| hex_encode(line)).collect::<Vec<_>>())], Some(id)));
                }
            }
        }
        if self.pending_io.is_none() {
            while self.gathers.pop_front_if(|gather| gather.abandoned).is_some() {}
        }
    }
    fn retry_history(&mut self, ids: Vec<ConnId>, now: u64, failures: u64) {
        self.history_failures = self.history_failures.saturating_add(failures);
        if ids.is_empty() { return; }
        // Held-live fallback is not a completed participant baseline. Request full
        // known-target history again, without replaying claims or resetting counters.
        self.joiners.extend(ids);
        self.next_presentation_ms = self.next_presentation_ms.max(now.saturating_add(1000));
        self.presentation_due = false;
        self.report.push(format!("join_history_retry|after_ms=1000|pending={}", self.joiners.len()));
    }
    fn flush_presentation(&mut self, now: u64, server: &Server, events: &mut Vec<RoutedEvent>) {
        self.flush_presentation_inner(now, server, events, false);
    }
    fn hold_fresh_lines(&mut self) {
        if self.driver_owners.len() > MAX_KNOWN_DRIVERS { self.driver_owners.clear(); }
        for line in &self.pushed { presentation::learn_driver_owner(&mut self.driver_owners, line, true); }
        for gather in &mut self.gathers {
            if gather.abandoned || !matches!(gather.audience, GatherAudience::Fresh { .. }) { continue; }
            let pending = std::mem::take(&mut gather.held).into_iter().chain(std::mem::take(&mut self.pushed));
            for line in pending {
                if gather.history.classify_live_line_among(&line, &self.driver_owners) { gather.held.push(line); }
                else { self.pushed.push(line); }
            }
            self.participant_hold_failed |= gather.history.live_driver_overflow();
        }
    }
    fn flush_presentation_inner(&mut self, now: u64, server: &Server, events: &mut Vec<RoutedEvent>, force: bool) {
        if !self.routes_ready() { return; }
        if self.epoch_barrier.is_some() && self.gathers.is_empty() { return; }
        self.hold_fresh_lines();
        if self.pushed.is_empty() {
            self.broadcast_due = false;
            return;
        }
        let gathering = self.gathers.iter().any(|gather| !gather.abandoned);
        if !self.broadcast_due && !gathering { return; }
        if !force && self.ladder.pushing && now.saturating_sub(self.last_presentation_ms) < self.options.presentation_min_interval_ms { return; }
        self.transforms_coalesced += presentation::coalesce_transforms(&mut self.pushed) as u64;
        let mut held = HashSet::new();
        for gather in &mut self.gathers {
            if let GatherAudience::Join(ids) = &gather.audience {
                if gather.abandoned { continue; }
                gather.held.extend(self.pushed.iter().cloned());
                held.extend(ids.iter().copied());
            }
        }
        // Diagnostic: every live line sent to clients, with the wall clock, when a capture directory is set.
        if let Some(dir) = std::env::var_os("SIMSONLINE_HISTORY_CAPTURE_DIR") {
            use std::io::Write;
            let stamp = std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).map_or(0, |d| d.as_millis());
            if let Ok(mut file) = std::fs::OpenOptions::new().create(true).append(true).open(std::path::Path::new(&dir).join("live.txt")) {
                for line in &self.pushed { let _ = writeln!(file, "{stamp}|{now}|{line}"); }
            }
        }
        if held.is_empty() {
            // These lines go to every joined connection now. A fresh gather still in flight whose
            // recipients are exactly those connections learns the objects they place, as it learned
            // the ones streaming when it began.
            let every_recipient = self.joiners.is_empty();
            for line in &self.pushed {
                if let Some(id) = presentation::live_full_transform(line) {
                    if self.live_transforms.len() < MAX_STREAMING_OBJECTS || self.live_transforms.contains_key(&id) { self.live_transforms.insert(id, now); }
                    if every_recipient {
                        for gather in &mut self.gathers {
                            if !gather.abandoned && matches!(&gather.audience, GatherAudience::Fresh { joiners, .. } if joiners.is_empty()) {
                                gather.history.note_streaming(id);
                            }
                        }
                    }
                }
            }
        }
        for batch in self.pushed.chunks(presentation::MAX_LINES) {
            let fields = vec![enc_list(&batch.iter().map(|line| hex_encode(line)).collect::<Vec<_>>())];
            if held.is_empty() { events.push(self.event("SimAnimation", fields, None)); }
            else {
                for id in server.joined_connections().into_iter().filter(|id| !held.contains(id)) {
                    events.push(self.event("SimAnimation", fields.clone(), Some(id)));
                }
            }
        }
        self.pushed.clear();
        self.broadcast_due = false;
        if self.live_out_last_ms != 0 { self.live_out_gap_ms = self.live_out_gap_ms.max(now.saturating_sub(self.live_out_last_ms)); }
        self.live_out_last_ms = now;
        self.last_presentation_ms = now;
    }
    fn failed_io(&mut self, io: StreamIo, events: &mut Vec<RoutedEvent>, reason: &str, now: u64) {
        match io {
            StreamIo::Dump { generation } => {
                self.want_dump = false;
                self.dump_pages = 0;
                self.drain.failed(generation, now, self.options.presentation_poll_ms);
                self.resync_refused_dump(reason, now);
            }
            StreamIo::ParticipantWitness(_) => self.participants.failed_witness(now, reason),
            StreamIo::ParticipantExport(export) => self.participants.failed_export(&export, now, reason),
            StreamIo::Routes { world } => self.routes_failed(world, now),
            StreamIo::StateProbe { scope, ids } => self.visible_state.failed(&scope, &ids),
            StreamIo::Gather => {
                if self.gathers.front().is_some_and(|gather| gather.epoch_participants_ready == Some(false)) {
                    self.gathers.pop_front();
                    self.participant_world_cancellations += 1;
                    return;
                }
                if let Some(mut gather) = self.gathers.pop_front() {
                    self.history_last_failure_hex = gather.failure_report(self.state_index_world, now, reason);
                    let audience = if matches!(gather.audience, GatherAudience::Join(_)) { "join" } else { "fresh" };
                    self.report.push(format!("join_history_failed|held={}|audience={audience}|reason={reason}", gather.held.len()));
                    let retry = match &gather.audience {
                        GatherAudience::Fresh { counted, .. } | GatherAudience::Join(counted) => counted.clone(),
                    };
                    self.retry_history(retry, now, 1);
                    gather.abandoned = true;
                    self.deliver_gather(gather, events);
                    self.broadcast_due = true;
                    if audience == "fresh" {
                        for waiting in &mut self.gathers {
                            if matches!(waiting.audience, GatherAudience::Fresh { .. }) { break; }
                            if waiting.started_ms.is_none() { waiting.started_ms = Some(now); }
                        }
                    }
                }
            }
            StreamIo::Diag | StreamIo::Full | StreamIo::Delta(_) => self.cadence_batch = None,
            StreamIo::PushOff { .. } => self.ring.failed_off(now),
            StreamIo::PushStatus { .. } => self.ring.stop(),
            StreamIo::Config(config, _) if config.args.first().is_some_and(|arg| arg == "push-targets") => {
                self.push_targets_sent = None;
                if self.ring.requested { self.ring.stop(); }
            }
            StreamIo::Config(_, _) if self.ring.requested => {self.ring.stop();self.ladder.push_result(now,false);},
            // The game refuses a dump only while its push is on. A refusal while this server
            // believes the push is off means the two disagree: an acknowledged push-off
            // restores agreement and the ladder re-arms after it.
            StreamIo::EpochDump if self.ring.requested => self.resync_refused_dump(reason, now),
            _ => {}
        }
    }
    fn resync_refused_dump(&mut self, reason: &str, now: u64) {
        if !self.ring.requested || self.ring.off_pending { return; }
        self.ladder.report.push(format!("dump_refused_resync|at_ms={now}|generation={}|reason={}",
            self.ring.generation, reason.chars().take(60).collect::<String>()));
        self.ring.stop();
    }
    fn routes_failed(&mut self, world: u64, now: u64) {
        self.route_config_failures = self.route_config_failures.saturating_add(1);
        if world == self.state_index_world {
            self.route_retry_at_ms = now.saturating_add(self.options.presentation_poll_ms.max(50));
        }
        self.report.push(format!("route_stream_configuration_pending|world={world}|failures={}", self.route_config_failures));
    }
}

#[cfg(test)]
mod ring_ownership_tests {
    use super::*;
    struct Native { calls:Vec<String>, pages:Vec<String>, off_failures:usize, epoch:Option<u64> }
    impl Native { fn new()->Self{Self{calls:vec![],pages:vec![],off_failures:0,epoch:None}} }
    impl GameDriver for Native {
        fn adjudicate(&mut self,_:&crate::messages::Command)->Result<Vec<u8>,DriverError>{unreachable!()}
        fn poll_event_lines(&mut self)->Result<Vec<Vec<u8>>,DriverError>{Ok(vec![])}
        fn claimed_sims(&self)->Vec<u64>{vec![10]}
        fn ring_available(&self)->bool{true}
        fn presentation_pages(&mut self)->Result<Vec<String>,DriverError>{Ok(std::mem::take(&mut self.pages))}
        fn discard_pages(&mut self)->Result<usize,DriverError>{Ok(std::mem::take(&mut self.pages).len())}
        fn take_epoch_announcement(&mut self)->Option<u64>{self.epoch}
        fn announced(&mut self,_:u64){self.epoch=None;}
        fn recorder(&mut self,args:&[String])->Result<String,DriverError>{
            self.calls.push(args.join("|"));
            Ok(match args[0].as_str(){
                "ring-on"=>"ring=on;recorder=on".into(),
                "push-off"=>{if self.off_failures>0{self.off_failures-=1;return Err(DriverError("fixture begin failure".into()));}"push=off,cursor=5,sequence=5,behind=0".into()},
                "push-on"|"push-targets"|"push-status"=>"push=on,cursor=5,sequence=5,behind=0".into(),
                "snapshot"=>"drivers=-".into(),"snapshot-driver"=>"next=-1".into(),
                "dump"=>"seq=5;more=0".into(),other=>panic!("unexpected {other}")
            })
        }
    }
    fn ready()->(Stream,Server,Native){
        let mut server=Server::new(1);for id in [1,2]{server.connect(ConnId(id));server.join(ConnId(id),id as i32);}
        let mut stream=Stream::new(StreamOptions{ring_requested:true,base_interval_ms:0.0,..StreamOptions::default()});
        for id in [1,2]{stream.joined(ConnId(id));}
        let mut driver=Native::new();stream.pump(0,&mut server,&mut driver).unwrap();(stream,server,driver)
    }
    #[test]
    fn cursor_requires_exact_header_before_releasing_startup_quarantine(){
        for bad in ["refused", "seq=-1;more=0", "seq=1;seq=1;more=0", "more=0", "seq=1;more=refused"] {
            let mut stream=Stream::new(StreamOptions{ring_requested:true,..StreamOptions::default()});
            stream.initialization=Initialization::Cursor;
            assert!(stream.complete_io(StreamIo::Cursor{initial:true},StreamReply::Recorder(bad.into()),0,&mut vec![]).is_err());
            assert!(stream.ring.quarantine);assert!(matches!(stream.initialization,Initialization::Cursor));
        }
        // Native clamps FROM_NOW before its observer pass, which can create rows/more.
        let mut stream=Stream::new(StreamOptions{ring_requested:true,..StreamOptions::default()});
        stream.complete_io(StreamIo::Cursor{initial:true},StreamReply::Recorder("seq=5;more=1\n5|AW|7".into()),0,&mut vec![]).unwrap();
        assert!(!stream.ring.quarantine);assert_eq!(stream.cursor,5);
    }
    #[test]
    fn startup_off_error_retains_barrier_and_retry_deadline_before_any_dump(){
        let mut stream=Stream::new(StreamOptions{ring_requested:true,base_interval_ms:0.0,..StreamOptions::default()});
        let mut server=Server::new(1);server.connect(ConnId(1));server.join(ConnId(1),1);stream.joined(ConnId(1));
        let mut driver=Native::new();driver.off_failures=1;driver.pages.push("seq=3;more=0\n3|X|7".into());
        assert!(stream.pump(0,&mut server,&mut driver).is_err());
        assert_eq!(driver.calls,["ring-on","push-off"]);
        stream.pump(249,&mut server,&mut driver).unwrap();assert_eq!(driver.calls.len(),2);
        stream.pump(250,&mut server,&mut driver).unwrap();
        assert_eq!(&driver.calls[..3],&["ring-on","push-off","push-off"]);
        assert_eq!(stream.ring.startup_discarded,1);assert!(!stream.ring.quarantine);
    }
    #[test]
    fn epoch_stops_and_drains_final_release_before_legacy_epoch_dump(){
        let (mut stream,mut server,mut driver)=ready();
        assert!(stream.ladder.pushing);
        driver.pages.push("seq=6;more=0\n6|X|7".into());driver.epoch=Some(2);
        let before=driver.calls.len();
        let events=stream.pump(250,&mut server,&mut driver).unwrap();
        assert_eq!(driver.calls[before],"push-off");
        assert!(driver.calls[before+1..].iter().any(|call|call.starts_with("dump|6|")));
        assert_eq!(driver.epoch,None);
        let lines:Vec<_>=events.iter().filter(|e|e.event.kind=="SimAnimation").flat_map(|e|crate::messages::dec_list(&e.event.fields[0]).unwrap()).map(|s|crate::messages::hex_decode(&s).unwrap()).collect();
        assert!(lines.iter().any(|s|s=="6|X|7"));
    }
    #[test]
    fn a_pushed_page_reaches_clients_in_the_pump_that_read_it_not_at_the_next_poll(){
        let (mut stream,mut server,mut driver)=ready();
        assert!(stream.ladder.pushing);
        stream.pump(1,&mut server,&mut driver).unwrap();
        stream.gathers.clear();stream.broadcast_due=false;
        driver.pages.push("seq=6;more=0
6|X|7".into());
        // 10 ms is inside the 250 ms presentation poll: nothing but the page itself asks for a broadcast.
        let events=stream.pump(10,&mut server,&mut driver).unwrap();
        let lines:Vec<_>=events.iter().filter(|e|e.event.kind=="SimAnimation").flat_map(|e|crate::messages::dec_list(&e.event.fields[0]).unwrap()).map(|s|crate::messages::hex_decode(&s).unwrap()).collect();
        assert!(lines.iter().any(|s|s=="6|X|7"),"a pushed line waited for the poll: {lines:?}");
    }
    #[test]
    fn idle_audience_drains_cursor_without_recipientless_history_and_rearms_empty_scope(){
        let (mut stream,mut server,mut driver)=ready();
        for id in [1,2]{server.disconnect(ConnId(id));stream.left(ConnId(id));}
        driver.pages.push("seq=6;more=0\n6|X|7".into());
        assert!(stream.pump(250,&mut server,&mut driver).unwrap().iter().all(|event|event.event.kind!="SimAnimation"));
        stream.pump(251,&mut server,&mut driver).unwrap();
        assert!(stream.gathers.is_empty());assert!(stream.pushed.is_empty());assert_eq!(stream.cursor,6);
        assert!(driver.calls.iter().any(|call|call=="push-on|6|-"));
    }
    #[test]
    fn idle_silence_stops_native_before_any_rearm_or_returning_audience(){
        let (mut stream,mut server,mut driver)=ready();
        for id in [1,2]{server.disconnect(ConnId(id));stream.left(ConnId(id));}
        stream.pump(250,&mut server,&mut driver).unwrap();
        stream.pump(251,&mut server,&mut driver).unwrap();
        assert!(stream.ladder.pushing);
        let before=driver.calls.len();
        stream.pump(3251,&mut server,&mut driver).unwrap();
        assert_eq!(&driver.calls[before..],&["push-off"]);
        assert!(stream.ring.off_ready);assert!(!stream.ring.may_push);
        server.connect(ConnId(3));server.join(ConnId(3),1);stream.joined(ConnId(3));
        stream.pump(3500,&mut server,&mut driver).unwrap();
        assert!(driver.calls[before+1..].iter().all(|call|!call.starts_with("push-on|")),"fallback delay must survive audience return");
    }
    #[test]
    fn street_sims_are_of_interest_by_distance_and_dwell_not_by_sharing_the_world_lot(){
        let (mut stream,_server,_driver)=ready();
        stream.sim_lots.clear();stream.sim_positions.clear();stream.targets.clear();
        let place=|stream:&mut Stream,id:u64,lot:u64,x:f32|{stream.sim_lots.insert(id,Some(lot));stream.sim_positions.insert(id,[x,0.0,0.0]);};
        place(&mut stream,1,WORLD_LOT,0.0);   // claimed, riding on the street
        place(&mut stream,2,500,0.0);         // claimed, at home
        place(&mut stream,3,500,400.0);       // housemate: same lot, any distance
        place(&mut stream,4,WORLD_LOT,5.0);   // the taxi driver
        place(&mut stream,5,WORLD_LOT,450.0); // a pedestrian across town
        place(&mut stream,6,700,5.0);         // on another lot the taxi passes
        let claims=[1,2];
        stream.observe_world_interest(1_000,&claims);
        assert_eq!(stream.interest_targets(1_000,&claims),HashSet::from([1,2,3]),"a street Sim is not adopted on first sight");
        stream.observe_world_interest(1_000+WORLD_DWELL_MS,&claims);
        let adopted=stream.interest_targets(1_000+WORLD_DWELL_MS,&claims);
        assert_eq!(adopted,HashSet::from([1,2,3,4]),"near and dwelt: the driver; never the far pedestrian or another lot");
        stream.targets=adopted;
        place(&mut stream,4,WORLD_LOT,80.0);
        stream.observe_world_interest(5_000,&claims);
        assert!(stream.interest_targets(5_000,&claims).contains(&4),"a streamed Sim is kept inside the exit margin");
        place(&mut stream,4,WORLD_LOT,95.0);
        stream.observe_world_interest(6_000,&claims);
        assert!(!stream.interest_targets(6_000,&claims).contains(&4),"and dropped beyond it");
        place(&mut stream,1,500,0.0);         // the claimed Sim is home again: the street is out of view
        place(&mut stream,4,WORLD_LOT,5.0);
        stream.observe_world_interest(9_000,&claims);stream.observe_world_interest(12_000,&claims);
        assert_eq!(stream.interest_targets(12_000,&claims),HashSet::from([1,2,3]));
    }
    #[test]
    fn queued_old_target_configuration_is_not_reminted_after_scope_change(){
        let (mut stream,mut server,_driver)=ready();
        let old=stream.drain.scope_generation();
        stream.configs.push_back(RecorderConfig {args:vec!["push-on".into(),"5".into(),"old-target".into()],
            push_target:Some("old-target".into()),scope_generation:old,at_ms:1});
        stream.gathers.push_back(JoinGather {history:presentation::HistoryGather::new(&[10]),
            epoch_participants_ready:None,audience:GatherAudience::Join(vec![ConnId(2)]),
            held:vec![],started_ms:Some(1),catchup_started_ms:None,timing:GatherTiming::default(),dump_turn:false,locked:true,abandoned:false});
        assert!(matches!(stream.next_io(&mut server,&mut vec![],1),Some(StreamIo::Gather)));
        assert_eq!(stream.configs.len(),1,"locked gather must retain queued configuration");
        stream.drain.targets("new-target");stream.ring.observe(stream.drain.scope_generation());
        stream.gathers.clear(); // the held request has completed; only now can queued config be selected
        assert!(!matches!(stream.next_io(&mut server,&mut vec![],2),Some(StreamIo::Config(_, _))));
        assert!(stream.configs.is_empty());
        assert!(stream.ladder.report.iter().any(|line|line.contains("reason=targets_changed")));
    }
    #[test]
    fn a_changed_target_set_keeps_a_live_push_on_and_only_a_scope_cut_stops_it(){
        let (mut stream,_server,_driver)=ready();
        stream.ring.may_push=true;stream.ring.off_pending=false;
        let generation=stream.ring.generation;
        assert!(stream.drain.targets("10,11"));stream.ring.observe(stream.drain.scope_generation());
        assert!(stream.ring.may_push && !stream.ring.off_pending,"a retarget must not stop the push");
        assert_eq!(stream.ring.generation,generation);
        stream.drain.cut_scope();stream.ring.observe(stream.drain.scope_generation());
        assert!(!stream.ring.may_push || stream.ring.off_pending,"a scope cut still stops the push");
        assert_ne!(stream.ring.generation,generation);
    }
    #[test]
    fn a_target_update_queued_before_the_push_went_off_is_void_and_never_scored_as_a_refusal(){
        let (mut stream,mut server,_driver)=ready();
        stream.ring.may_push=false;stream.ring.off_pending=false;
        let delay=stream.ladder.rearm_delay_ms();
        stream.configs.push_back(RecorderConfig {args:vec!["push-targets".into(),"10".into()],push_target:None,
            scope_generation:stream.ring.generation,at_ms:1});
        assert!(!matches!(stream.next_io(&mut server,&mut vec![],2),Some(StreamIo::Config(_, _))));
        assert!(stream.configs.is_empty());assert!(!stream.ring.may_push);
        assert_eq!(stream.ladder.rearm_delay_ms(),delay);
        assert!(stream.ladder.report.iter().any(|line|line.starts_with("push_targets_void|")));
    }
    #[test]
    fn malformed_page_batch_fails_before_any_earlier_valid_page_is_published(){
        let (mut stream,mut server,mut driver)=ready();let before=stream.cursor;
        driver.pages=vec!["seq=6;more=0\n6|X|7".into(),"seq=7;seq=8;more=0\n7|AW|7".into()];
        assert!(stream.pump(250,&mut server,&mut driver).is_err());assert!(stream.ring_transport_failed());
        assert_eq!(stream.cursor,before);assert!(stream.pushed.is_empty());
    }
}
