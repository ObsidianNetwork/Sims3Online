#![forbid(unsafe_code)]

use std::{cell::RefCell,rc::Rc};
use sim96_kernel_cleanroom::{SimId,private_navigation::*,live_navigation::*,routing_astar::*};
use simlink_bridge::verbs::BridgeCommand;
use simlink_headless_server::{ConnId,DriverError,GameDriver};
use simlink_headless_server::bridge_driver::{BridgeEndpoint,BridgeGameDriver};
use simlink_headless_server::messages::{self as m,Command};

const ACTOR: u64 = 0xfedc_ba98_7654_3210;
fn unhex(s: &str) -> Vec<u8> {
    s.as_bytes().as_chunks::<2>().0.iter().map(|c| u8::from_str_radix(std::str::from_utf8(c).unwrap(),16).unwrap()).collect()
}
#[derive(Default)]
struct State {
    events: Vec<Vec<u8>>,
    results: Vec<Frame>,
    offers: Vec<RustNavigationEnvelope>,
    blob: Option<(String,usize,usize,Vec<u8>)>,
    snapshot: Option<Vec<u8>>,
    refuse_offer: bool,
    offer_reply: Option<&'static str>,
    result_reply: Option<&'static str>,
    process_exited: bool,
    checkpoint_pending: bool,
    disconnected: bool,
    /// Every `rust-route-on` / `rust-navigation-sync` recorder verb, in send order.
    order: Vec<String>,
    /// The args after the verb of each `rust-navigation-sync`: epoch, index, count, entries.
    syncs: Vec<Vec<String>>,
    sync_staged: usize,
    sync_active: usize,
    event_error: bool,
    lose_sync_final_ack: bool,
    refuse_switch: bool,
    dumps: std::collections::VecDeque<String>,
    ring_pages: Vec<String>,
    dump_calls: usize,
}
fn recorder_reply(corr: &str, body: String, mode: Option<&str>) -> Vec<String> {
    let body = match mode {
        Some("legacy") => "status;replayDrivers=0;sessions=1".into(),
        Some("wrong-request") => body.replace("request=3;","request=999;"),
        Some("wrong-blob") => body.replace("blob=nav-","blob=other-"),
        Some("wrong-epoch") => body.replace(";epoch=7;",";epoch=8;"),
        Some("wrong-generation") => body.replace(";generation=1;",";generation=2;"),
        Some("wrong-actor") => body.replace(&format!(";actor={ACTOR}"),";actor=1"),
        Some("wrong-plan-corr") => body.replace(";corr=1;",";corr=2;"),
        Some("wrong-operation") => body.replace("rust-navigation-","unknown-navigation-"),
        Some("wrong-frame") => format!("{body}00"),
        Some("rejected") => return vec![format!("AUTONOMY_PROBE|{corr}|refused|{}",m::hex_encode(&body).to_ascii_uppercase())],
        _ => body,
    };
    let corr = if mode == Some("wrong-corr") { "0" } else { corr };
    vec![format!("AUTONOMY_PROBE|{corr}|ok|{}",m::hex_encode(&body).to_ascii_uppercase())]
}
struct Endpoint(Rc<RefCell<State>>);
impl BridgeEndpoint for Endpoint {
    fn connected(&self) -> bool { !self.0.borrow().disconnected }
    fn begin_checkpoint_command(&mut self, command: &BridgeCommand, _: std::time::Duration) -> Result<Option<Vec<String>>,DriverError> {
        if self.0.borrow().checkpoint_pending && matches!(command,BridgeCommand::SaveGame { .. }) { Ok(None) }
        else { self.command(command).map(Some) }
    }
    fn poll_checkpoint_command(&mut self) -> Result<Option<Vec<String>>,DriverError> {
        if self.0.borrow().process_exited { Err(DriverError("process_exited".into())) } else { Ok(None) }
    }
    fn command(&mut self, command: &BridgeCommand) -> Result<Vec<String>,DriverError> {
        if self.0.borrow().process_exited { return Err(DriverError("process_exited".into())); }
        let encoded = simlink_bridge::verbs::encode(command).unwrap();
        assert!(encoded.len() <= 4080);
        let corr = encoded.split('|').nth(1).unwrap();
        let mut state = self.0.borrow_mut();
        match command {
            BridgeCommand::RequestSnapshot { .. } => {
                let mut bytes = Vec::new();
                for n in [0x3250_5353u32,2] { bytes.extend(n.to_le_bytes()); }
                bytes.extend(1u64.to_le_bytes());
                for n in [0u32,0,1,0,0] { bytes.extend(n.to_le_bytes()); }
                state.snapshot = Some(bytes);
                Ok(vec![format!("SNAPSHOT2|{corr}")])
            }
            BridgeCommand::Raw { name,args,.. } if name == "DIAG_BLOB" => {
                let index: usize = args[1].parse().unwrap();
                let count: usize = args[2].parse().unwrap();
                let chunk = unhex(&args[3]);
                assert!(chunk.len() <= 1900);
                if index == 0 { state.blob = Some((args[0].clone(),count,0,Vec::new())); }
                let (blob,expected,next,bytes) = state.blob.as_mut().unwrap();
                assert_eq!((blob.as_str(),*expected,*next),(args[0].as_str(),count,index));
                bytes.extend(chunk); *next += 1;
                Ok(vec![format!("DIAG_BLOB_REPORT|{corr}|ok|")])
            }
            BridgeCommand::SacsRecorder { args,.. } if args[0].ends_with("-on") => {
                state.order.push(args[0].clone());
                if state.refuse_switch { return Ok(vec![format!("ERROR|{corr}|refused")]); }
                Ok(vec![format!("AUTONOMY_PROBE|{corr}|ok|")])
            }
            BridgeCommand::SacsRecorder { args,.. } if args[0] == "dump" => {
                state.dump_calls += 1;
                let body = state.dumps.pop_front().unwrap_or_else(|| "seq=0;more=0".into());
                Ok(recorder_reply(corr,body,None))
            }
            BridgeCommand::SacsRecorder { args,.. } if args[0] == "push-targets" => {
                Ok(recorder_reply(corr,String::new(),None))
            }
            BridgeCommand::SacsRecorder { args,.. } if args[0] == "snapshot" => {
                Ok(recorder_reply(corr,"drivers=".into(),None))
            }
            BridgeCommand::SacsRecorder { args,.. } if args[0] == "rust-navigation-sync" => {
                // The mod's contract: index 0 resets staging, the last page commits atomically.
                state.order.push(args[0].clone());
                state.syncs.push(args[1..].to_vec());
                let (epoch,index,count,entries) = (&args[1],&args[2],&args[3],&args[4]);
                let page = if entries == "none" { 0 } else { entries.split(',').count() };
                if index == "0" { state.sync_staged = 0; }
                state.sync_staged += page;
                if index.parse::<usize>().unwrap() + 1 == count.parse::<usize>().unwrap() {
                    state.sync_active = state.sync_staged;
                }
                let body = format!("rust-navigation-sync=accepted;epoch={epoch};index={index};count={count};entries={page};active={}",
                    state.sync_active);
                if index.parse::<usize>().unwrap() + 1 == count.parse::<usize>().unwrap()
                    && std::mem::take(&mut state.lose_sync_final_ack) {
                    return Err(DriverError("lost final sync acknowledgment".into()));
                }
                Ok(recorder_reply(corr,body,None))
            }
            BridgeCommand::SacsRecorder { args,.. } if args[0] == "rust-navigation-result" => {
                let frame = Frame::decode(&unhex(&args[1])).unwrap();
                let body = format!("rust-navigation-result=accepted;operation={};request={};frame={}",
                    frame.message.operation(),frame.id,args[1]);
                state.results.push(frame);
                let mode = state.result_reply.take();
                Ok(recorder_reply(corr,body,mode))
            }
            BridgeCommand::SacsRecorder { args,.. } if args[0] == "rust-navigation-offer" => {
                if state.refuse_offer { return Ok(vec![format!("ERROR|{corr}|refused")]); }
                let (blob,count,next,bytes) = state.blob.take().unwrap();
                assert_eq!(count,next);
                assert_eq!(blob,args[1]);
                let envelope = RustNavigationEnvelope::decode(&bytes).unwrap();
                assert_eq!(envelope.epoch.to_string(),args[3]);
                assert_eq!(envelope.generation.to_string(),args[4]);
                assert_eq!(envelope.corr.to_string(),args[5]);
                assert_eq!(envelope.actor.to_string(),args[6]);
                state.offers.push(envelope);
                let body = format!("rust-navigation-offer=accepted;blob={};request={};epoch={};generation={};corr={};actor={}",
                    args[1],args[2],args[3],args[4],args[5],args[6]);
                Ok(recorder_reply(corr,body,state.offer_reply))
            }
            _ => Ok(vec![format!("OK|{corr}")]),
        }
    }
    fn events(&mut self) -> Result<Vec<Vec<u8>>,DriverError> {
        let mut state = self.0.borrow_mut();
        if std::mem::take(&mut state.event_error) { return Err(DriverError("event read failed".into())); }
        Ok(std::mem::take(&mut state.events))
    }
    fn take_snapshot(&mut self) -> Option<Vec<u8>> { self.0.borrow_mut().snapshot.take() }
}
fn command(corr: u64, verb: &str, args: &[&str]) -> Command {
    Command { corr,command_id:corr,player_id:-7,verb:verb.into(),args:args.iter().map(|s| (*s).into()).collect() }
}
fn enqueue(state: &Rc<RefCell<State>>, bytes: &[u8]) {
    // Fragment every record, including tiny control frames, at byte offsets.
    let id = Frame::decode(bytes).unwrap().id;
    for (index,chunk) in bytes.chunks(47).enumerate() {
        let hex: String = chunk.iter().map(|b| format!("{b:02X}")).collect();
        state.borrow_mut().events.push(format!("EVENT|PrivateNavigation|{id}|{}|{}|{hex}",
            index*47,bytes.len()).into_bytes());
    }
}
fn send(driver: &mut BridgeGameDriver<Endpoint>, state: &Rc<RefCell<State>>, id: u64, message: Message)
    -> Vec<simlink_headless_server::routing::RoutedEvent> {
    enqueue(state,&Frame { id,message }.encode().unwrap());
    driver.poll_routed_events().unwrap()
}
fn fixture() -> Message {
    // Authored square, not measured or private geometry.
    let nav = SimNav {
        header:NavHeader { format_version:1,flags:NAV_FLAG_PORTALS_BIDIRECTIONAL,lot_id:10,..NavHeader::default() },
        rooms:vec![NavRoom { room_id:0,cell_len:1,..NavRoom::default() }],
        cells:vec![NavCell { cell_id:0,room_id:0,vertex_len:4,centroid_cm:[50,0,50],..NavCell::default() }],
        vertices:vec![NavVertex { x_cm:0,y_cm:0,z_cm:0 },NavVertex { x_cm:0,y_cm:0,z_cm:100 },
            NavVertex { x_cm:100,y_cm:0,z_cm:100 },NavVertex { x_cm:100,y_cm:0,z_cm:0 }],
        ..SimNav::default()
    };
    Message::Install { simnav:nav.to_bytes(),bindings:Bindings { schema_version:1,epoch:7,generation:1,
        provenance:Provenance::Measured,cells:vec![CellBinding { cell_id:0,lot_id:10,room_id:100,level:0 }],
        portals:vec![],keys_held:vec![],obstacles:vec![],actors:vec![(ACTOR,SimId(2))] } }
}
fn request(corr: u64) -> Request {
    Request { epoch:7,generation:1,corr,actor_sim_id:ACTOR,kernel_sim:SimId(2),
        start_xyz_mm:[250,0,250],goal_xyz_mm:[750,0,750],start_identity:(10,100,0),goal_identity:(10,100,0),flags:0 }
}
fn installed() -> (BridgeGameDriver<Endpoint>,Rc<RefCell<State>>) {
    let state = Rc::new(RefCell::new(State::default()));
    let mut driver = BridgeGameDriver::new(Endpoint(Rc::clone(&state)));
    driver.configure_rust_switches(true,false,false);
    driver.adjudicate_for(ConnId(777),&command(1,"JoinSession",&["74657374","~"])).unwrap();
    assert!(driver.adjudicate(&command(2,"ClaimSim",&[&ACTOR.to_string()])).unwrap().starts_with(b"ACK|"));
    send(&mut driver,&state,1,Message::Invalidate { epoch:7,generation:0 });
    send(&mut driver,&state,2,fixture());
    assert!(matches!(state.borrow().results.last().unwrap().message,Message::Result { status:Status::Done,.. }));
    let (mut stream,mut server) = epoch_stream();
    stream.pump(0,&mut server,&mut driver).unwrap();
    (driver,state)
}

fn sync_driver(sync: bool) -> (BridgeGameDriver<Endpoint>,Rc<RefCell<State>>) {
    let state = Rc::new(RefCell::new(State::default()));
    let mut driver = BridgeGameDriver::new(Endpoint(Rc::clone(&state)));
    driver.configure_rust_switches(true,false,sync);
    (driver,state)
}
fn syncs(state: &Rc<RefCell<State>>) -> Vec<String> {
    state.borrow().syncs.iter().map(|args| args.join("|")).collect()
}
fn acked(driver: &mut BridgeGameDriver<Endpoint>, conn: u64, cmd: Command) {
    assert!(driver.adjudicate_for(ConnId(conn),&cmd).unwrap().starts_with(b"ACK|"));
}

fn animation_lines(events: &[simlink_headless_server::routing::RoutedEvent]) -> Vec<String> {
    events.iter().filter(|e| e.event.kind == "SimAnimation").flat_map(|e|
        m::dec_list(&e.event.fields[0]).unwrap().into_iter().map(|s| m::hex_decode(&s).unwrap())
    ).collect()
}
fn epoch_stream() -> (simlink_headless_server::stream::Stream,simlink_headless_server::Server) {
    let stream = simlink_headless_server::stream::Stream::new(
        simlink_headless_server::stream::StreamOptions { base_interval_ms:0.0,
            presentation_min_interval_ms:10000,..Default::default() });
    let mut server = simlink_headless_server::Server::new(9);
    server.connect(ConnId(3));
    server.join(ConnId(3),-7);
    (stream,server)
}

#[test]
fn epoch_delivery_idle_bump_is_acked_own_event_and_joiner_only_after_snapshot() {
    let (mut driver,state) = sync_driver(true);
    let (mut stream,mut server) = epoch_stream();
    let first = stream.pump(0,&mut server,&mut driver).unwrap();
    assert_eq!(animation_lines(&first),["0|Ne|1"]);
    assert_eq!(first.iter().find(|e| e.event.kind == "SimAnimation").unwrap().event.corr,0);
    assert!(animation_lines(&stream.pump(1,&mut server,&mut driver).unwrap()).is_empty());
    driver.admin_request(&simlink_headless_server::admin::parse_request("LOAD_SAVE 46697874757265").unwrap()).unwrap();
    state.borrow_mut().lose_sync_final_ack = true;
    assert!(stream.pump(2,&mut server,&mut driver).is_err());
    assert!(animation_lines(&driver.take_pending_events()).is_empty(),"no announcement on lost ack");
    let bumped = stream.pump(3,&mut server,&mut driver).unwrap();
    assert_eq!(animation_lines(&bumped),["0|Ne|2"]);
    assert_eq!(route_plans(&mut driver,"RUST_ROUTE_PLANS")["report"],"");
    assert!(animation_lines(&stream.pump(4,&mut server,&mut driver).unwrap()).is_empty());
    let mut join = command(1,"JoinSession",&["6a6f696e","~"]);
    join.player_id = 8;
    server.connect(ConnId(4));
    server.join(ConnId(4),8);
    acked(&mut driver,4,join);
    let events = driver.take_pending_events();
    let full = events.iter().position(|e| e.event.kind == "FullSnapshot").unwrap();
    assert_eq!(events[full-2..].iter().map(|event| event.event.kind.as_str()).collect::<Vec<_>>(),
        ["SessionJoined","GameSpeedChanged","FullSnapshot","LotLocalAuthorityModeChanged","SimAnimation"]);
    assert_eq!(animation_lines(&events[full+1..]),["0|Ne|2"]);
    let epoch = &events[full+2];
    assert_eq!((epoch.requester,epoch.event.corr),(Some(ConnId(4)),0));
    assert_eq!(simlink_headless_server::routing::recipients(&server,epoch),[ConnId(4)]);
    assert_eq!(m::dec_list(&epoch.event.fields[0]).unwrap(),["307c4e657c32"]);
}

#[test]
fn epoch_delivery_join_before_barrier_has_no_epoch_and_switch_refusal_has_none() {
    let (mut driver,state) = sync_driver(false);
    state.borrow_mut().refuse_switch = true;
    let (mut stream,mut server) = epoch_stream();
    assert!(stream.pump(0,&mut server,&mut driver).is_err());
    assert!(animation_lines(&driver.take_pending_events()).is_empty());
    state.borrow_mut().refuse_switch = false;
    acked(&mut driver,3,command(1,"JoinSession",&["74657374","~"]));
    assert!(animation_lines(&driver.take_pending_events()).is_empty());
    assert_eq!(animation_lines(&stream.pump(1,&mut server,&mut driver).unwrap()),["0|Ne|1"]);
}

#[test]
fn epoch_delivery_rust_route_off_never_announces_attach_join_or_load_save() {
    for sync in [false,true] {
        for road in [false,true] {
            let (mut driver,_) = sync_driver(sync);
            driver.configure_rust_switches(false,road,sync);
            let (mut stream,mut server) = epoch_stream();
            let mut events = stream.pump(0,&mut server,&mut driver).unwrap();
            acked(&mut driver,3,command(1,"JoinSession",&["74657374","~"]));
            events.extend(driver.take_pending_events());
            driver.admin_request(&simlink_headless_server::admin::parse_request("LOAD_SAVE 46697874757265").unwrap()).unwrap();
            events.extend(stream.pump(1,&mut server,&mut driver).unwrap());
            acked(&mut driver,4,command(2,"JoinSession",&["6a6f696e","~"]));
            events.extend(stream.pump(2,&mut server,&mut driver).unwrap());
            assert!(animation_lines(&events).iter().all(|s| s.split('|').nth(1) != Some("Ne")),"sync={sync},road={road}");
        }
    }
}

#[test]
fn epoch_delivery_plan_waits_for_announcement_and_sync_off_accepts_kernel_epoch() {
    for sync in [false,true] {
    let epoch = if sync { 1 } else { 7 };
    let (mut driver,state) = sync_driver(sync);
    acked(&mut driver,3,command(1,"JoinSession",&["74657374","~"]));
    acked(&mut driver,3,command(2,"ClaimSim",&[&ACTOR.to_string()]));
    send(&mut driver,&state,1,Message::Invalidate { epoch,generation:0 });
    let Message::Install { simnav,mut bindings } = fixture() else { unreachable!() };
    bindings.epoch = epoch;
    send(&mut driver,&state,2,Message::Install { simnav,bindings });
    let mut req = request(1);
    req.epoch = epoch;
    send(&mut driver,&state,3,Message::Plan { origin:Origin::Engine,deadline_tick:100,request:req });
    let waiting = send(&mut driver,&state,3,Message::CommitOffer);
    assert!(animation_lines(&waiting).is_empty(),"plan cannot precede its Ne");
    assert!(state.borrow().offers.is_empty(),"held delivery is not counted as failed or offered");
    let (mut stream,mut server) = epoch_stream();
    assert_eq!(animation_lines(&stream.pump(0,&mut server,&mut driver).unwrap()),["0|Ne|1"]);
    let next = animation_lines(&stream.pump(1,&mut server,&mut driver).unwrap());
    assert!(next[0].starts_with("0|Rt|1|"));
    assert!(next[1].starts_with("0|Vp|rt|1|"));
    assert_eq!(state.borrow().offers.len(),1);
    assert!(!driver.event_drop_state().contains("PrivateNavigationDelivery"));
    assert!(animation_lines(&stream.pump(2,&mut server,&mut driver).unwrap()).is_empty());
    }
}

#[test]
fn epoch_delivery_paging_requires_current_epoch_before_reading_frames() {
    let (mut driver,state) = installed();
    send(&mut driver,&state,3,Message::Plan { origin:Origin::Engine,deadline_tick:100,request:request(1) });
    send(&mut driver,&state,3,Message::CommitOffer);
    let first = route_plans(&mut driver,"RUST_ROUTE_PLANS 1 0 1");
    assert!(first["report"].as_str().unwrap().starts_with("rust-route-plans=page;epoch=1;corr=1;"));
    assert_eq!(route_plans(&mut driver,"RUST_ROUTE_PLANS 1 1")["error"],
        "rust-route-client=blocked;reason=epoch-required");
    assert_eq!(route_plans(&mut driver,"RUST_ROUTE_PLANS 1 1 16 2")["error"],
        "rust-route-client=blocked;reason=epoch-mismatch");
    assert_eq!(route_plans(&mut driver,"RUST_ROUTE_PLANS 1 1 16 1")["ok"],true);
}

struct RingDriver(BridgeGameDriver<Endpoint>);
impl GameDriver for RingDriver {
    fn adjudicate(&mut self, cmd: &Command) -> Result<Vec<u8>,DriverError> { self.0.adjudicate(cmd) }
    fn poll_event_lines(&mut self) -> Result<Vec<Vec<u8>>,DriverError> { self.0.poll_event_lines() }
    fn poll_routed_events(&mut self) -> Result<Vec<simlink_headless_server::routing::RoutedEvent>,DriverError> {
        self.0.poll_routed_events()
    }
    fn take_pending_events(&mut self) -> Vec<simlink_headless_server::routing::RoutedEvent> { self.0.take_pending_events() }
    fn take_epoch_announcement(&mut self) -> Option<u64> { self.0.take_epoch_announcement() }
    fn announced(&mut self, epoch: u64) { self.0.announced(epoch); }
    fn presentation_pages(&mut self) -> Result<Vec<String>,DriverError> {
        Ok(std::mem::take(&mut self.0.endpoint().0.borrow_mut().ring_pages))
    }
    fn recorder(&mut self, args: &[String]) -> Result<String,DriverError> { self.0.recorder(args) }
    fn claimed_sims(&self) -> Vec<u64> { self.0.claimed_sims() }
}

#[test]
fn epoch_delivery_route_off_has_no_pending_barrier_and_preserves_main_batch_bytes() {
    for sync in [false,true] {
        for road in [false,true] {
            let (mut driver,state) = sync_driver(sync);
            driver.configure_rust_switches(false,road,sync);
            acked(&mut driver,3,command(1,"JoinSession",&["74657374","~"]));
            acked(&mut driver,3,command(2,"ClaimSim",&[&ACTOR.to_string()]));
            assert_eq!(driver.take_epoch_announcement(),None);
            let mut driver = RingDriver(driver);
            let (mut stream,mut server) = epoch_stream();
            assert!(animation_lines(&stream.pump(0,&mut server,&mut driver).unwrap()).is_empty());
            stream.ladder.push_result(0,true);
            let dumps = state.borrow().dump_calls;
            let lines = ["1|A|55|buffered","2|A|55|unread","3|A|55|after-load","4|A|55|due"];
            for (index,now) in [1,2,3,10000].into_iter().enumerate() {
                if index == 2 {
                    driver.0.admin_request(&simlink_headless_server::admin::parse_request("LOAD_SAVE 46697874757265").unwrap()).unwrap();
                }
                state.borrow_mut().ring_pages.push(format!("seq={};more=0\n{}",index+1,lines[index]));
                let events = stream.pump(now,&mut server,&mut driver).unwrap();
                assert_eq!(driver.take_epoch_announcement(),None,"sync={sync},road={road}");
                assert_eq!(state.borrow().dump_calls,dumps,"route-off entered the epoch dump barrier");
                if index < 3 {
                    assert!(animation_lines(&events).is_empty(),"batch flushed before the main cadence");
                } else {
                    let animations: Vec<_> = events.iter().filter(|e| e.event.kind == "SimAnimation").collect();
                    assert_eq!(animations.len(),1);
                    let expected = m::Event { seq:0,tick:0,corr:0,kind:"SimAnimation".into(),
                        fields:vec![m::enc_list(&lines.map(m::hex_encode))] };
                    let encoded = simlink_headless_server::wire_v1::encode_event(&animations[0].event).unwrap();
                    assert_eq!(encoded,simlink_headless_server::wire_v1::encode_event(&expected).unwrap());
                    println!("route-off-batch|sync={sync}|road={road}|{encoded}");
                }
            }
        }
    }
}

#[test]
fn epoch_delivery_barrier_drains_unread_ring_and_all_dump_pages_before_ne_despite_cadence() {
    for pushing in [false,true] {
        let (mut driver,state) = sync_driver(true);
        acked(&mut driver,3,command(1,"JoinSession",&["74657374","~"]));
        acked(&mut driver,3,command(2,"ClaimSim",&[&ACTOR.to_string()]));
        let mut driver = RingDriver(driver);
        let (mut stream,mut server) = epoch_stream();
        assert_eq!(animation_lines(&stream.pump(0,&mut server,&mut driver).unwrap()),["0|Ne|1"]);
        stream.ladder.push_result(0,true);
        state.borrow_mut().ring_pages.push("seq=1;more=0\n1|A|55|old-buffered".into());
        assert!(animation_lines(&stream.pump(1,&mut server,&mut driver).unwrap()).is_empty());
        driver.0.admin_request(&simlink_headless_server::admin::parse_request("LOAD_SAVE 46697874757265").unwrap()).unwrap();
        state.borrow_mut().ring_pages.push("seq=2;more=0\n2|A|55|old-unread".into());
        let mut expected = vec!["1|A|55|old-buffered".to_string(),"2|A|55|old-unread".into()];
        stream.ladder.pushing = pushing;
        if !pushing {
            for n in 3..=20 {
                let line = format!("{n}|A|55|old-dump");
                state.borrow_mut().dumps.push_back(format!("seq={n};more={}\n{line}",u8::from(n < 20)));
                expected.push(line);
            }
        }
        expected.push("0|Ne|2".into());
        let events = stream.pump(2,&mut server,&mut driver).unwrap();
        assert_eq!(animation_lines(&events),expected,"pushing={pushing}");
        let ne = events.iter().rev().find(|e| e.event.kind == "SimAnimation").unwrap();
        assert_eq!(m::dec_list(&ne.event.fields[0]).unwrap().len(),1,"own event");
        assert_eq!(ne.event.corr,0);
        assert!(animation_lines(&stream.pump(3,&mut server,&mut driver).unwrap()).is_empty());
        assert!(state.borrow().dumps.is_empty());
    }
}

#[test]
fn epoch_delivery_bump_drops_unannounced_old_plan_and_wrong_epoch_fails_delivery() {
    for bump in [false,true] {
        let (mut driver,state) = sync_driver(true);
        acked(&mut driver,3,command(1,"JoinSession",&["74657374","~"]));
        acked(&mut driver,3,command(2,"ClaimSim",&[&ACTOR.to_string()]));
        let Message::Install { simnav,mut bindings } = fixture() else { unreachable!() };
        bindings.epoch = if bump { 1 } else { 7 };
        let epoch = bindings.epoch;
        send(&mut driver,&state,1,Message::Invalidate { epoch,generation:0 });
        send(&mut driver,&state,2,Message::Install { simnav,bindings });
        let mut req = request(1);
        req.epoch = epoch;
        send(&mut driver,&state,3,Message::Plan { origin:Origin::Engine,deadline_tick:100,request:req });
        let events = send(&mut driver,&state,3,Message::CommitOffer);
        assert!(animation_lines(&events).is_empty());
        if bump {
            driver.admin_request(&simlink_headless_server::admin::parse_request("LOAD_SAVE 46697874757265").unwrap()).unwrap();
        } else {
            assert!(driver.event_drop_state().contains("PrivateNavigationDelivery:1"));
        }
        let (mut stream,mut server) = epoch_stream();
        let events = stream.pump(0,&mut server,&mut driver).unwrap();
        assert_eq!(animation_lines(&events),["0|Ne|2"]);
        assert!(state.borrow().offers.is_empty());
        assert_eq!(route_plans(&mut driver,"RUST_ROUTE_PLANS")["report"],"");
        assert!(animation_lines(&stream.pump(1,&mut server,&mut driver).unwrap()).is_empty());
    }
}

#[test]
fn epoch_delivery_review_dump_limit_keeps_epoch_pending_and_recovers_without_reordering() {
    let limit = 4096 + 1;
    let (mut driver,state) = sync_driver(true);
    acked(&mut driver,3,command(1,"JoinSession",&["74657374","~"]));
    acked(&mut driver,3,command(2,"ClaimSim",&[&ACTOR.to_string()]));
    let mut driver = RingDriver(driver);
    let (mut stream,mut server) = epoch_stream();
    assert_eq!(animation_lines(&stream.pump(0,&mut server,&mut driver).unwrap()),["0|Ne|1"]);
    stream.ladder.push_result(0,true);
    state.borrow_mut().ring_pages.push("seq=1;more=0\n1|A|55|old-buffered".into());
    assert!(animation_lines(&stream.pump(1,&mut server,&mut driver).unwrap()).is_empty());
    driver.0.admin_request(&simlink_headless_server::admin::parse_request("LOAD_SAVE 46697874757265").unwrap()).unwrap();
    stream.ladder.pushing = false;
    // Finite fixture: the old unbounded loop returns Ok on page limit+1, so
    // the failing-first test detects the bug without hanging the test process.
    state.borrow_mut().dumps.extend(std::iter::repeat_n("seq=1;more=1".into(),limit));
    state.borrow_mut().dumps.push_back("seq=1;more=0".into());
    let before = state.borrow().dump_calls;
    let error = stream.pump(2,&mut server,&mut driver).unwrap_err();
    assert_eq!(error.0,"navigation epoch dump page limit reached");
    assert_eq!(state.borrow().dump_calls-before,limit);
    assert_eq!(driver.take_epoch_announcement(),Some(2));
    assert!(animation_lines(&stream.take_events(&mut driver)).is_empty(),"no Ne or premature flush");
    assert_eq!(state.borrow().dumps.len(),1);
    let events = stream.pump(3,&mut server,&mut driver).unwrap();
    assert_eq!(animation_lines(&events),["1|A|55|old-buffered","0|Ne|2"]);
    assert_eq!(driver.take_epoch_announcement(),None);
}

#[test]
fn epoch_delivery_bump_drains_held_plan_before_fresh_attachment() {
    let (mut driver,state) = sync_driver(true);
    acked(&mut driver,3,command(1,"JoinSession",&["74657374","~"]));
    acked(&mut driver,3,command(2,"ClaimSim",&[&ACTOR.to_string()]));
    let Message::Install { simnav,mut bindings } = fixture() else { unreachable!() };
    bindings.epoch = 1;
    send(&mut driver,&state,1,Message::Invalidate { epoch:1,generation:0 });
    send(&mut driver,&state,2,Message::Install { simnav:simnav.clone(),bindings:bindings.clone() });
    let mut req = request(1);
    req.epoch = 1;
    send(&mut driver,&state,3,Message::Plan { origin:Origin::Engine,deadline_tick:100,request:req });
    assert!(animation_lines(&send(&mut driver,&state,3,Message::CommitOffer)).is_empty());
    assert!(state.borrow().offers.is_empty(),"old plan is held before the first Ne");

    driver.admin_request(&simlink_headless_server::admin::parse_request("LOAD_SAVE 46697874757265").unwrap()).unwrap();
    let (mut stream,mut server) = epoch_stream();
    assert_eq!(animation_lines(&stream.pump(0,&mut server,&mut driver).unwrap()),["0|Ne|2"]);
    let drops = driver.event_drop_state();
    state.borrow_mut().results.clear();
    send(&mut driver,&state,1,Message::Invalidate { epoch:2,generation:1 });
    assert_eq!(driver.event_drop_state(),drops,"stale held plan closed the fresh attachment");
    assert!(matches!(state.borrow().results.last().map(|f| &f.message),
        Some(Message::Result { operation:4,status:Status::Done,epoch:2,generation:1,.. })));
    bindings.epoch = 2;
    bindings.generation = 2;
    send(&mut driver,&state,2,Message::Install { simnav,bindings });
    let mut req = request(1);
    req.epoch = 2;
    req.generation = 2;
    send(&mut driver,&state,3,Message::Plan { origin:Origin::Engine,deadline_tick:100,request:req });
    let events = send(&mut driver,&state,3,Message::CommitOffer);
    assert_eq!(animation_lines(&events).len(),2,"fresh plan must complete on the new attachment");
    let snapshot = state.borrow();
    assert_eq!(snapshot.offers.len(),1);
    assert_eq!((snapshot.offers[0].epoch,snapshot.offers[0].corr),(2,1));
    drop(snapshot);
    assert!(animation_lines(&stream.pump(1,&mut server,&mut driver).unwrap()).is_empty());
    assert!(syncs(&state).last().unwrap().starts_with("2|"),"no extra bump from a replayed old delivery");
    assert_eq!(driver.event_drop_state(),drops);
}

#[test]
fn card55_navigation_sync_is_off_unless_its_switch_is_on() {
    let (mut driver,state) = sync_driver(false);
    driver.poll_routed_events().unwrap();
    acked(&mut driver,3,command(1,"JoinSession",&["74657374","~"]));
    acked(&mut driver,3,command(2,"ClaimSim",&[&ACTOR.to_string()]));
    driver.poll_routed_events().unwrap();
    assert_eq!(state.borrow().order,["rust-route-on"]);
    assert!(syncs(&state).is_empty());
}

#[test]
fn card55_navigation_sync_follows_attach_join_claim_release_and_leave() {
    let (mut driver,state) = sync_driver(true);
    driver.poll_routed_events().unwrap();
    // First attach: epoch 1, after rust-route-on, before anything else.
    assert_eq!(state.borrow().order,["rust-route-on","rust-navigation-sync"]);
    assert_eq!(syncs(&state),["1|0|1|none"]);
    acked(&mut driver,3,command(1,"JoinSession",&["74657374","~"]));
    assert_eq!(syncs(&state).last().unwrap(),"1|0|1|none");
    // The public signed player id (-7) and the joining connection, not the kernel PlayerId.
    acked(&mut driver,3,command(2,"ClaimSim",&[&ACTOR.to_string()]));
    let claimed = format!("1|0|1|3:-7:{ACTOR}:2");
    assert_eq!(syncs(&state).last().unwrap(),&claimed);
    assert_eq!(state.borrow().sync_active,1);
    // An unrelated command changes nothing and pushes nothing.
    let before = syncs(&state).len();
    driver.poll_routed_events().unwrap();
    assert_eq!(syncs(&state).len(),before);
    acked(&mut driver,3,command(3,"ReleaseSim",&[&ACTOR.to_string()]));
    assert_eq!(syncs(&state).last().unwrap(),"1|0|1|none");
    assert_eq!(state.borrow().sync_active,0);
    acked(&mut driver,3,command(4,"ClaimSim",&[&ACTOR.to_string()]));
    assert_eq!(syncs(&state).last().unwrap(),&claimed);
    acked(&mut driver,3,command(5,"LeaveSession",&[]));
    assert_eq!(syncs(&state).last().unwrap(),"1|0|1|none");
    // A disconnect or supersede releases through the same seam.
    acked(&mut driver,4,command(6,"JoinSession",&["74657374","~"]));
    acked(&mut driver,4,command(7,"ClaimSim",&[&ACTOR.to_string()]));
    assert_eq!(syncs(&state).last().unwrap(),&format!("1|0|1|4:-7:{ACTOR}:2"));
    driver.release_player(-7);
    assert_eq!(syncs(&state).last().unwrap(),"1|0|1|none");
}

#[test]
fn card55_navigation_epoch_bumps_on_load_save_close_and_reconnect() {
    let (mut driver,state) = sync_driver(true);
    driver.poll_routed_events().unwrap();
    acked(&mut driver,3,command(1,"JoinSession",&["74657374","~"]));
    acked(&mut driver,3,command(2,"ClaimSim",&[&ACTOR.to_string()]));
    let entry = format!("3:-7:{ACTOR}:2");
    // An accepted LOAD_SAVE re-arms the switches; the sync follows route-on with epoch 2.
    driver.admin_request(&simlink_headless_server::admin::parse_request("LOAD_SAVE 46697874757265").unwrap()).unwrap();
    driver.poll_routed_events().unwrap();
    assert_eq!(syncs(&state).last().unwrap(),&format!("2|0|1|{entry}"));
    assert_eq!(&state.borrow().order[state.borrow().order.len() - 2..],["rust-route-on","rust-navigation-sync"]);
    // The private attachment closing (a malformed record) forces another bump.
    state.borrow_mut().events.push(b"EVENT|PrivateNavigation|malformed".to_vec());
    driver.poll_routed_events().unwrap();
    driver.poll_routed_events().unwrap();
    assert_eq!(syncs(&state).last().unwrap(),&format!("3|0|1|{entry}"));
    // A bridge reconnect bumps once, and nothing is pushed while it is down.
    let pushed = syncs(&state).len();
    state.borrow_mut().disconnected = true;
    driver.poll_routed_events().unwrap();
    driver.poll_routed_events().unwrap();
    assert_eq!(syncs(&state).len(),pushed);
    state.borrow_mut().disconnected = false;
    driver.poll_routed_events().unwrap();
    driver.poll_routed_events().unwrap();
    assert_eq!(syncs(&state)[pushed..],[format!("4|0|1|{entry}")]);
    // The epoch only ever moves forward and a quiet pump never re-pushes it.
    driver.poll_routed_events().unwrap();
    assert_eq!(syncs(&state).len(),pushed + 1);
}

#[test]
fn card55_refused_load_save_still_bumps_because_its_attachment_closed() {
    let (mut driver,state) = sync_driver(true);
    driver.poll_routed_events().unwrap();
    state.borrow_mut().events.clear();
    driver.admin_request(&simlink_headless_server::admin::parse_request("LOAD_SAVE 46697874757265").unwrap()).unwrap();
    driver.poll_routed_events().unwrap();
    assert!(syncs(&state).last().unwrap().starts_with("2|"));
}

#[test]
fn card55_navigation_sync_pages_a_table_over_the_carrier() {
    let (mut driver,state) = sync_driver(true);
    driver.poll_routed_events().unwrap();
    acked(&mut driver,3,command(1,"JoinSession",&["74657374","~"]));
    // Wide wire ids make each entry about 30 bytes, so 150 claims need two 3500-byte pages.
    let wires: Vec<u64> = (0..150u64).map(|n| u64::MAX - n).collect();
    for (n,wire) in wires.iter().enumerate() {
        assert!(driver.adjudicate(&command(2 + n as u64,"ClaimSim",&[&wire.to_string()])).unwrap().starts_with(b"ACK|"));
    }
    // `adjudicate` pushes nothing; the next connection-bound command pushes the whole table.
    let before = state.borrow().syncs.len();
    acked(&mut driver,3,command(999,"ClaimSim",&[&(u64::MAX - 150).to_string()]));
    let pages = state.borrow().syncs[before..].to_vec();
    assert!(pages.len() >= 2,"{} pages",pages.len());
    let mut seen = 0;
    for (index,args) in pages.iter().enumerate() {
        assert_eq!((args[0].as_str(),args[1].as_str(),args[2].as_str()),("1",index.to_string().as_str(),pages.len().to_string().as_str()));
        assert!(args[3].len() <= 3500);
        seen += args[3].split(',').count();
    }
    assert_eq!(seen,151);
    assert_eq!(state.borrow().sync_active,151);
}

#[test]
fn card55_event_poll_close_bumps_epoch_without_a_connected_flag_change() {
    let (mut driver,state) = sync_driver(true);
    driver.poll_routed_events().unwrap();
    send(&mut driver,&state,1,Message::Invalidate { epoch:1,generation:1 });
    state.borrow_mut().event_error = true;
    assert_eq!(driver.poll_routed_events().unwrap_err().0,"event read failed");
    driver.poll_routed_events().unwrap();
    assert_eq!(syncs(&state).last().unwrap(),"2|0|1|none");
    let before = syncs(&state).len();
    driver.poll_routed_events().unwrap();
    assert_eq!(syncs(&state).len(),before);
}

#[test]
fn card55_sync_restarts_at_zero_after_lost_final_ack_or_receiver_reset() {
    for reset in [false,true] {
        let (mut driver,state) = sync_driver(true);
        driver.poll_routed_events().unwrap();
        acked(&mut driver,3,command(1,"JoinSession",&["74657374","~"]));
        // Seed a committed nonempty table so reset changes the receiver's active count too.
        acked(&mut driver,3,command(2,"ClaimSim",&[&ACTOR.to_string()]));
        for n in 0..150u64 {
            assert!(driver.adjudicate(&command(3+n,"ClaimSim",&[&(u64::MAX-n).to_string()])).unwrap().starts_with(b"ACK|"));
        }
        state.borrow_mut().lose_sync_final_ack = true;
        acked(&mut driver,3,command(999,"ClaimSim",&[&(u64::MAX-150).to_string()]));
        assert_eq!(state.borrow().sync_active,152,"final page committed despite lost reply");
        if reset {
            state.borrow_mut().sync_active = 0;
            state.borrow_mut().sync_staged = 0;
        }
        let before = syncs(&state).len();
        driver.poll_routed_events().unwrap();
        let retry = state.borrow().syncs[before..].to_vec();
        assert!(retry.len() >= 2,"reset={reset}");
        assert_eq!(retry.first().unwrap()[1],"0");
        assert_eq!(retry.last().unwrap()[1],(retry.len()-1).to_string());
        assert_eq!(state.borrow().sync_active,152);
        let after = syncs(&state).len();
        driver.poll_routed_events().unwrap();
        assert_eq!(syncs(&state).len(),after,"retry finished, reset={reset}");
    }
}

#[test]
fn card55_example_1c_six_frames_restart_correlation_byte_for_byte() {
    const ACTOR_1C: u64 = 18031287978496800304;
    const LOT: u64 = 7787005354294925872;
    let (mut driver,state) = sync_driver(true);
    let mut join = command(1,"JoinSession",&["74657374","~"]);
    join.player_id = 1;
    acked(&mut driver,3,join);
    let mut claim = command(2,"ClaimSim",&[&ACTOR_1C.to_string()]);
    claim.player_id = 1;
    acked(&mut driver,3,claim);
    send(&mut driver,&state,1,Message::Invalidate { epoch:1,generation:1 });
    // Authored square covering the example's points, with the example's identities.
    let Message::Install { mut simnav,mut bindings } = fixture() else { unreachable!() };
    let mut nav = SimNav::load(&simnav).unwrap();
    nav.header.lot_id = LOT;
    nav.cells[0].centroid_cm = [200,0,200];
    nav.vertices[1].z_cm = 400;
    nav.vertices[2].x_cm = 400;
    nav.vertices[2].z_cm = 400;
    nav.vertices[3].x_cm = 400;
    simnav = nav.to_bytes();
    SimNav::load(&simnav).unwrap();
    bindings.epoch = 1;
    bindings.generation = 2;
    bindings.actors = vec![(ACTOR_1C,SimId(2))];
    bindings.cells[0].lot_id = LOT;
    bindings.cells[0].room_id = 0;
    send(&mut driver,&state,2,Message::Install { simnav:simnav.clone(),bindings:bindings.clone() });
    assert!(matches!(state.borrow().results.last().unwrap().message,Message::Result { status:Status::Done,.. }),
        "{:?}",state.borrow().results.last().unwrap().encode().unwrap());
    let req = |epoch,generation,corr| Request { epoch,generation,corr,actor_sim_id:ACTOR_1C,kernel_sim:SimId(2),
        start_xyz_mm:[1000,0,1000],goal_xyz_mm:[3000,0,1000],
        start_identity:(LOT,0,0),goal_identity:(LOT,0,0),flags:0 };
    send(&mut driver,&state,3,Message::Plan { origin:Origin::Player(3),deadline_tick:100,request:req(1,2,5) });
    assert!(matches!(state.borrow().results.last().unwrap().message,Message::Result { status:Status::Staged,.. }),
        "{:?}",state.borrow().results.last().unwrap().encode().unwrap());
    state.borrow_mut().event_error = true;
    assert!(driver.poll_routed_events().is_err());
    driver.poll_routed_events().unwrap();
    assert_eq!(syncs(&state).last().unwrap(),"2|0|1|3:1:18031287978496800304:2");
    // Reconnect binds the public connection again; close cleared the private session map.
    let mut rejoin = command(10,"JoinSession",&["74657374","~"]);
    rejoin.player_id = 1;
    acked(&mut driver,3,rejoin);
    let mut reclaim = command(11,"ClaimSim",&[&ACTOR_1C.to_string()]);
    reclaim.player_id = 1;
    acked(&mut driver,3,reclaim);
    state.borrow_mut().results.clear();
    // The six pinned frames in contract 3.1 Example 1C: Invalidate/Result twice, Plan/Result.
    for (id,generation,event,reply) in [
        (1,1,"EVENT|PrivateNavigation|1|0|32|1C00000001000400010000000000000002000000000000000100000000000000",
            "230000000100080001000000000000000403000100000000000000020000000000000000000000"),
        (2,2,"EVENT|PrivateNavigation|2|0|32|1C00000001000400020000000000000002000000000000000200000000000000",
            "230000000100080002000000000000000401000200000000000000020000000000000000000000"),
    ] {
        let frame = Frame { id,message:Message::Invalidate { epoch:2,generation } };
        let bytes = frame.encode().unwrap();
        assert_eq!(event,format!("EVENT|PrivateNavigation|{id}|0|{}|{}",bytes.len(),
            bytes.iter().map(|b| format!("{b:02X}")).collect::<String>()));
        state.borrow_mut().events.push(event.as_bytes().to_vec());
        driver.poll_routed_events().unwrap();
        assert_eq!(state.borrow().results.last().unwrap().encode().unwrap(),unhex(reply));
    }
    bindings.epoch = 2;
    bindings.generation = 3;
    send(&mut driver,&state,3,Message::Install { simnav,bindings });
    assert!(matches!(state.borrow().results.last().unwrap().message,
        Message::Result { operation:3,status:Status::Done,epoch:2,generation:3,.. }));
    let frame = Frame { id:4,message:Message::Plan { origin:Origin::Player(3),deadline_tick:100,request:req(2,3,1) } };
    let event = "EVENT|PrivateNavigation|4|0|133|81000000010005000400000000000000010300000000000000640000000000000002000000000000000300000000000000010000000000000030B612F1E1003CFA02000000E803000000000000E8030000B80B000000000000E8030000305AD0C61C00116C00000000000000000000305AD0C61C00116C0000000000000000000000000000";
    let bytes = frame.encode().unwrap();
    assert_eq!(event,format!("EVENT|PrivateNavigation|4|0|{}|{}",bytes.len(),
        bytes.iter().map(|b| format!("{b:02X}")).collect::<String>()));
    state.borrow_mut().events.push(event.as_bytes().to_vec());
    driver.poll_routed_events().unwrap();
    assert_eq!(state.borrow().results.last().unwrap().encode().unwrap(),
        unhex("230000000100080004000000000000000500000200000000000000030000000000000000000000"));
}

#[test]
fn card55_route_plans_admin_pages_one_retained_plan() {
    let (mut driver,state) = installed();
    send(&mut driver,&state,3,Message::Plan { origin:Origin::Engine,deadline_tick:100,request:request(1) });
    send(&mut driver,&state,3,Message::CommitOffer);
    let ask = |driver: &mut BridgeGameDriver<Endpoint>, line: &str| -> serde_json::Value {
        serde_json::from_str(&driver.admin_request(&simlink_headless_server::admin::parse_request(line).unwrap()).unwrap()).unwrap()
    };
    let whole = ask(&mut driver,"RUST_ROUTE_PLANS");
    let frames: Vec<_> = whole["report"].as_str().unwrap().lines().map(str::to_owned).collect();
    assert_eq!(frames.len(),2);
    let first = ask(&mut driver,"RUST_ROUTE_PLANS 1 0 1");
    assert_eq!(first["report"],format!("rust-route-plans=page;epoch=1;corr=1;actor={ACTOR};cursor=0;count=1;total=2;next=1;more=1\n{}",frames[0]));
    let rest = ask(&mut driver,"RUST_ROUTE_PLANS 1 1 16 1");
    assert_eq!(rest["report"],format!("rust-route-plans=page;epoch=1;corr=1;actor={ACTOR};cursor=1;count=1;total=2;next=2;more=0\n{}",frames[1]));
    let missing = ask(&mut driver,"RUST_ROUTE_PLANS 9");
    assert_eq!((&missing["ok"],missing["error"].as_str()),(&serde_json::Value::Bool(false),Some("rust-route-plans=missing")));
    let beyond = ask(&mut driver,"RUST_ROUTE_PLANS 1 3 16 1");
    assert_eq!(beyond["error"],"rust-route-plans=blocked;reason=cursor-out-of-range");
    for bad in ["RUST_ROUTE_PLANS 0","RUST_ROUTE_PLANS x","RUST_ROUTE_PLANS 1 -1","RUST_ROUTE_PLANS 1 0 x",
        "RUST_ROUTE_PLANS 1 0 1 1 1","RUST_ROUTE_PLANS 1 0 1 01","RUST_ROUTE_PLANS 1 0 1 +1",
        "RUST_ROUTE_PLANS 1 0 1 0","RUST_ROUTE_PLANS 1 0 1 18446744073709551616"] {
        assert_eq!(ask(&mut driver,bad)["ok"],false,"{bad}");
    }
}

fn route_plans(driver: &mut BridgeGameDriver<Endpoint>, line: &str) -> serde_json::Value {
    serde_json::from_str(&driver.admin_request(&simlink_headless_server::admin::parse_request(line).unwrap()).unwrap()).unwrap()
}

#[test]
fn card55_route_plans_epoch_bump_purges_old_corr_and_serves_its_replacement() {
    for cause in ["malformed", "event-error", "disconnect"] {
        let (mut driver,state) = sync_driver(true);
        driver.poll_routed_events().unwrap();
        let (mut stream,mut server) = epoch_stream();
        stream.pump(0,&mut server,&mut driver).unwrap();
        acked(&mut driver,777,command(1,"JoinSession",&["74657374","~"]));
        acked(&mut driver,777,command(2,"ClaimSim",&[&ACTOR.to_string()]));
        let Message::Install { simnav,mut bindings } = fixture() else { unreachable!() };
        bindings.epoch = 1;
        send(&mut driver,&state,1,Message::Invalidate { epoch:1,generation:0 });
        send(&mut driver,&state,2,Message::Install { simnav:simnav.clone(),bindings:bindings.clone() });
        let mut req = request(1);
        req.epoch = 1;
        send(&mut driver,&state,3,Message::Plan { origin:Origin::Engine,deadline_tick:100,request:req });
        send(&mut driver,&state,3,Message::CommitOffer);
        let old = route_plans(&mut driver,"RUST_ROUTE_PLANS 1");
        assert_eq!(old["ok"],true,"{cause}: {old}");
        assert_eq!(state.borrow().offers.len(),1);

        match cause {
            "malformed" => state.borrow_mut().events.push(b"EVENT|PrivateNavigation|malformed".to_vec()),
            "event-error" => state.borrow_mut().event_error = true,
            "disconnect" => state.borrow_mut().disconnected = true,
            _ => unreachable!(),
        }
        let closed = driver.poll_routed_events();
        if cause == "event-error" { assert_eq!(closed.unwrap_err().0,"event read failed"); }
        else { closed.unwrap(); }
        state.borrow_mut().disconnected = false;
        driver.poll_routed_events().unwrap();
        assert!(syncs(&state).last().unwrap().starts_with("2|"),"{cause}");
        let missing = route_plans(&mut driver,"RUST_ROUTE_PLANS 1");
        assert_eq!(missing["error"],"rust-route-plans=missing","{cause}: {missing}");
        assert_eq!(route_plans(&mut driver,"RUST_ROUTE_PLANS")["report"],"","{cause}");
        assert_eq!(route_plans(&mut driver,"RUST_ROUTE_PLANS 1 1 16 1")["error"],
            "rust-route-client=blocked;reason=epoch-mismatch","{cause}");
        stream.pump(1,&mut server,&mut driver).unwrap();

        // Reattach, reinstall, and commit a different plan with the restarted correlation.
        acked(&mut driver,777,command(10,"JoinSession",&["74657374","~"]));
        acked(&mut driver,777,command(11,"ClaimSim",&[&ACTOR.to_string()]));
        bindings.epoch = 2;
        bindings.generation = 2;
        send(&mut driver,&state,1,Message::Invalidate { epoch:2,generation:1 });
        let mut nav = SimNav::load(&simnav).unwrap();
        nav.cells[0].centroid_cm = [60,0,60];
        for vertex in &mut nav.vertices { vertex.x_cm += 10; vertex.z_cm += 10; }
        send(&mut driver,&state,2,Message::Install { simnav:nav.to_bytes(),bindings });
        assert!(matches!(state.borrow().results.last().unwrap().message,
            Message::Result { status:Status::Done,epoch:2,generation:2,.. }),"{cause}: {:?}",state.borrow().results.last().unwrap().encode().unwrap());
        let mut req = request(1);
        req.epoch = 2;
        req.generation = 2;
        req.goal_xyz_mm = [650,0,650];
        send(&mut driver,&state,3,Message::Plan { origin:Origin::Engine,deadline_tick:100,request:req });
        assert!(matches!(state.borrow().results.last().unwrap().message,
            Message::Result { status:Status::Staged,.. }),"{cause}: {:?}",state.borrow().results.last().unwrap().encode().unwrap());
        send(&mut driver,&state,3,Message::CommitOffer);
        let replacement = route_plans(&mut driver,"RUST_ROUTE_PLANS 1");
        assert_eq!(replacement["ok"],true,"{cause}: {replacement}");
        assert_ne!(replacement["report"],old["report"],"{cause}");
        let state = state.borrow();
        assert_eq!(state.offers.len(),2,"{cause}");
        assert_eq!((state.offers[1].epoch,state.offers[1].corr),(2,1),"{cause}");
    }
}

#[test]
fn card55_route_plans_load_save_still_clears_without_navigation_sync() {
    let (mut driver,state) = installed();
    driver.configure_rust_switches(true,false,false);
    send(&mut driver,&state,3,Message::Plan { origin:Origin::Engine,deadline_tick:100,request:request(1) });
    send(&mut driver,&state,3,Message::CommitOffer);
    assert_eq!(route_plans(&mut driver,"RUST_ROUTE_PLANS 1")["ok"],true);
    let loaded = route_plans(&mut driver,"LOAD_SAVE 46697874757265");
    assert_eq!(loaded["ok"],true);
    assert_eq!(route_plans(&mut driver,"RUST_ROUTE_PLANS 1")["error"],"rust-route-plans=missing");
    assert_eq!(route_plans(&mut driver,"RUST_ROUTE_PLANS")["report"],"");
    assert!(syncs(&state).is_empty());
}

#[test]
fn private_navigation_flush_error_keeps_collected_checkpoint_refusal() {
    let (mut driver,state) = installed();
    send(&mut driver,&state,3,Message::Plan { origin:Origin::Engine,deadline_tick:100,request:request(1) });
    state.borrow_mut().checkpoint_pending = true;
    assert!(driver.adjudicate_for(ConnId(777),&command(4,"SaveGame",&["50656e64696e67"])).unwrap().starts_with(b"ACK|"));
    {
        let mut state = state.borrow_mut();
        state.process_exited = true;
        state.events.push(b"EVENT|PrivateNavigation|malformed".to_vec());
    }
    assert_eq!(driver.poll_routed_events().unwrap_err().0,"process_exited");
    let events = driver.terminal_events(&DriverError("process_exited".into()));
    let refused: Vec<_> = events.iter().filter(|event| event.event.kind == "CommandRejected").collect();
    assert_eq!(refused.len(),1);
    assert_eq!(refused[0].event.corr,4);
    assert_eq!(refused[0].event.fields,["4",&m::hex_encode("process_exited")]);
}

#[test]
fn card55_e2_front_door_state_reports_private_counts_without_bridge_io() {
    use std::io::{BufRead, BufReader, Write};
    use std::net::{TcpListener, TcpStream};
    use simlink_headless_server::serve::{RunningServer, StreamOptions};
    let server = RunningServer::start_configured(TcpListener::bind("127.0.0.1:0").unwrap(),
        Some(TcpListener::bind("127.0.0.1:0").unwrap()), || {
            let (mut driver, state) = installed();
            send(&mut driver, &state, 3, Message::Plan {
                origin: Origin::Engine, deadline_tick: 100, request: request(1),
            });
            send(&mut driver, &state, 3, Message::CommitOffer);
            state.borrow_mut().process_exited = true;
            Ok(driver)
        }, 4, StreamOptions { base_interval_ms: 0.0, ..StreamOptions::default() }).unwrap();
    let socket = TcpStream::connect(server.admin_addr().unwrap()).unwrap();
    socket.set_read_timeout(Some(std::time::Duration::from_secs(10))).unwrap();
    let mut socket = BufReader::new(socket);
    for _ in 0..2 {
        writeln!(socket.get_mut(), "FRONT_DOOR_STATE").unwrap();
        let mut line = String::new();
        assert_ne!(socket.read_line(&mut line).unwrap(), 0);
        let reply: serde_json::Value = serde_json::from_str(&line).unwrap();
        let report = reply["report"].as_str().unwrap();
        for field in ["navigate_requests=1", "plans_served=1", "native_oracle_owned=0",
            "road_place_requests=0", "road_place_served=0", "road_load_failures=0"] {
            assert!(report.contains(field), "missing {field}: {report}");
        }
    }
    server.shutdown().unwrap();
}

#[test]
fn actual_actor_pairs_private_named_blob_and_sealed_presenter_events() {
    let (mut driver,state) = installed();
    send(&mut driver,&state,3,Message::Plan { origin:Origin::Player(777),deadline_tick:100,request:request(ACTOR) });
    assert!(matches!(state.borrow().results.last().unwrap().message,Message::Result { status:Status::Staged,.. }));
    let events = send(&mut driver,&state,3,Message::CommitOffer);
    let state = state.borrow();
    assert_eq!(state.offers.len(),1);
    let envelope = &state.offers[0];
    assert_eq!((envelope.actor,envelope.corr),(ACTOR,ACTOR));
    assert_eq!((envelope.start_mm,envelope.goal_mm),([250,0,250],[750,0,750]));
    assert_eq!(envelope.legs.iter().map(|l| l.kind).collect::<Vec<_>>(),[4]);
    let lines: Vec<_> = events.iter().filter(|e| e.event.kind == "SimAnimation").flat_map(|e| {
        assert_eq!(e.requester,None); // Existing broadcast routing reaches joined presenters.
        m::dec_list(&e.event.fields[0]).unwrap().into_iter().map(|s| m::hex_decode(&s).unwrap()).collect::<Vec<_>>()
    }).collect();
    assert_eq!(lines,[format!("0|Rt|{ACTOR}|{ACTOR}|1|{:016x}|0",envelope.hash),
        format!("0|Vp|rt|{ACTOR}|{ACTOR}|0|0|50|0|50")]);
    drop(state);
    let reply = driver.admin_request(&simlink_headless_server::admin::parse_request("RUST_ROUTE_PLANS").unwrap()).unwrap();
    let reply: serde_json::Value = serde_json::from_str(&reply).unwrap();
    let retained = reply["report"].as_str().unwrap();
    assert_eq!(retained.lines().collect::<Vec<_>>(),
        lines.iter().map(|line| line.strip_prefix("0|").unwrap()).collect::<Vec<_>>());
}

#[test]
fn actor_rejects_guessed_session_and_closed_owner_and_drops_duplicate_commit() {
    let (mut driver,state) = installed();
    send(&mut driver,&state,3,Message::Plan { origin:Origin::Player(778),deadline_tick:100,request:request(1) });
    assert!(matches!(state.borrow().results.last().unwrap().message,Message::Result { status:Status::Unauthorized,.. }));
    send(&mut driver,&state,4,Message::Plan { origin:Origin::Player(777),deadline_tick:100,request:request(2) });
    driver.release_player(-7);
    driver.poll_routed_events().unwrap();
    assert!(matches!(state.borrow().results.last().unwrap().message,Message::Result { status:Status::Cancelled,.. }));
    send(&mut driver,&state,4,Message::CommitOffer);
    assert!(state.borrow().offers.is_empty());
    send(&mut driver,&state,5,Message::Plan { origin:Origin::Player(777),deadline_tick:100,request:request(3) });
    assert!(matches!(state.borrow().results.last().unwrap().message,Message::Result { status:Status::Unauthorized,.. }));
}

#[test]
fn recorder_acknowledgment_requires_exact_offer_identity_before_presenter_success() {
    for mode in ["legacy","wrong-request","wrong-blob","wrong-epoch","wrong-generation",
        "wrong-actor","wrong-plan-corr","wrong-operation","wrong-corr","rejected","exact"] {
        let (mut driver,state) = installed();
        send(&mut driver,&state,3,Message::Plan { origin:Origin::Engine,deadline_tick:100,request:request(1) });
        state.borrow_mut().offer_reply = Some(mode);
        let events = send(&mut driver,&state,3,Message::CommitOffer);
        let animations = events.iter().filter(|e| e.event.kind == "SimAnimation").count();
        assert_eq!(animations > 0,mode == "exact","{mode}");
        assert!(driver.routing_state().unwrap().contains(&format!("plans_served={}|", u8::from(mode == "exact"))), "{mode}");
        assert_eq!(driver.event_drop_state().contains("PrivateNavigationDelivery:1"),mode != "exact","{mode}");
        // The endpoint decoded the actual blob in every case, even when its
        // acknowledgment was false: delivery alone must not publish Rt/Vp.
        assert_eq!(state.borrow().offers.len(),1);
    }
}

#[test]
fn recorder_acknowledgment_requires_exact_result_and_reconnect_restarts_ids() {
    for mode in ["legacy","wrong-request","wrong-operation","wrong-frame","wrong-corr","rejected","exact"] {
        let (mut driver,state) = installed();
        state.borrow_mut().result_reply = Some(mode);
        let events = send(&mut driver,&state,3,Message::Plan { origin:Origin::Engine,deadline_tick:100,request:request(1) });
        assert!(events.iter().all(|e| e.event.kind != "SimAnimation"));
        assert!(driver.routing_state().unwrap().contains(&format!("navigate_requests={}|plans_served=0|", u8::from(mode == "exact"))), "{mode}");
        assert_eq!(driver.event_drop_state().contains("PrivateNavigationDelivery:1"),mode != "exact","{mode}");
        let events = send(&mut driver,&state,3,Message::CommitOffer);
        assert_eq!(events.iter().any(|e| e.event.kind == "SimAnimation"),mode == "exact","{mode}");
        if mode == "exact" { continue; }
        // The failure closes this attachment. After its terminal is drained,
        // a fresh fence must not inherit the old request-ID high-water.
        state.borrow_mut().result_reply = None;
        state.borrow_mut().results.clear();
        send(&mut driver,&state,1,Message::Invalidate { epoch:8,generation:1 });
        let state = state.borrow();
        assert_eq!(state.results.len(),1,"{mode}");
        assert_eq!(state.results[0].id,1);
        assert!(matches!(state.results[0].message,Message::Result { operation:4,status:Status::Done,epoch:8,.. }));
    }
}

#[test]
fn bridge_delivery_refusal_never_emits_presenter_success() {
    let (mut driver,state) = installed();
    send(&mut driver,&state,3,Message::Plan { origin:Origin::Engine,deadline_tick:100,request:request(1) });
    state.borrow_mut().refuse_offer = true;
    let events = send(&mut driver,&state,3,Message::CommitOffer);
    assert!(events.iter().all(|e| e.event.kind != "SimAnimation"));
    assert!(state.borrow().offers.is_empty());
    assert!(driver.routing_state().unwrap().contains("navigate_requests=1|plans_served=0|"));
    assert!(driver.event_drop_state().contains("PrivateNavigationDelivery:1"));
    let reply = driver.admin_request(&simlink_headless_server::admin::parse_request("RUST_ROUTE_PLANS").unwrap()).unwrap();
    let reply: serde_json::Value = serde_json::from_str(&reply).unwrap();
    assert_eq!(reply["report"], "");
}

#[test]
fn card55_retains_only_the_last_sixteen_committed_plans_without_replay_prefixes() {
    let (mut driver, state) = installed();
    for corr in 1..=20 {
        send(&mut driver, &state, corr + 2, Message::Plan { origin: Origin::Engine, deadline_tick: 100, request: request(corr) });
        send(&mut driver, &state, corr + 2, Message::CommitOffer);
        send(&mut driver, &state, corr + 2, Message::CommitOffer);
    }
    let query = simlink_headless_server::admin::parse_request("RUST_ROUTE_PLANS").unwrap();
    let reply: serde_json::Value = serde_json::from_str(&driver.admin_request(&query).unwrap()).unwrap();
    let lines: Vec<_> = reply["report"].as_str().unwrap().lines().collect();
    assert_eq!(lines.len(), 32);
    assert!(lines[0].starts_with(&format!("Rt|5|{ACTOR}|")));
    assert!(lines[31].starts_with(&format!("Vp|rt|20|{ACTOR}|")));
    assert!(lines.iter().all(|line| !line.starts_with("0|")));
    let invalid = simlink_headless_server::admin::parse_request("RUST_ROUTE_PLANS unexpected").unwrap();
    let reply: serde_json::Value = serde_json::from_str(&driver.admin_request(&invalid).unwrap()).unwrap();
    assert_eq!(reply["ok"], false);
}

#[test]
fn card55_retained_plans_are_read_through_the_loopback_admin_listener() {
    use std::io::{BufRead, BufReader, Write};
    use std::net::{TcpListener, TcpStream};
    use simlink_headless_server::serve::{RunningServer, StreamOptions};
    let server = RunningServer::start_configured(TcpListener::bind("127.0.0.1:0").unwrap(),
        Some(TcpListener::bind("127.0.0.1:0").unwrap()), || {
            let (mut driver, state) = installed();
            send(&mut driver, &state, 3, Message::Plan { origin: Origin::Engine, deadline_tick: 100, request: request(1) });
            send(&mut driver, &state, 3, Message::CommitOffer);
            Ok(driver)
        }, 4, StreamOptions::default()).unwrap();
    let socket = TcpStream::connect(server.admin_addr().unwrap()).unwrap();
    socket.set_read_timeout(Some(std::time::Duration::from_secs(10))).unwrap();
    let mut socket = BufReader::new(socket);
    writeln!(socket.get_mut(), "RUST_ROUTE_PLANS").unwrap();
    let mut line = String::new();
    socket.read_line(&mut line).unwrap();
    let reply: serde_json::Value = serde_json::from_str(&line).unwrap();
    assert!(reply["report"].as_str().unwrap().starts_with(&format!("Rt|1|{ACTOR}|")));
    let socket = TcpStream::connect(server.local_addr()).unwrap();
    socket.set_read_timeout(Some(std::time::Duration::from_secs(10))).unwrap();
    let mut socket = BufReader::new(socket);
    writeln!(socket.get_mut(), "RUST_ROUTE_PLANS").unwrap();
    let mut line = String::new();
    socket.read_line(&mut line).unwrap();
    assert!(line.starts_with("ERR|"), "public listener disclosed retained geometry");
    server.shutdown().unwrap();
}

#[test]
fn authored_vector_reaches_actor_codec_and_oversized_chunk_fails_closed() {
    let data: serde_json::Value = serde_json::from_str(include_str!("../../../testdata/card55-private-attachment-v1.json")).unwrap();
    for row in data["vectors"].as_array().unwrap() {
        let state = Rc::new(RefCell::new(State::default()));
        let mut driver = BridgeGameDriver::new(Endpoint(Rc::clone(&state)));
        send(&mut driver,&state,1,Message::Invalidate { epoch:7,generation:0 });
        enqueue(&state,&unhex(row["wire_hex"].as_str().unwrap()));
        let events = driver.poll_routed_events().unwrap();
        assert!(events.is_empty(),"private record leaked into public events");
        let expected_drop = if row["name"] == "ready" { "PrivateNavigation:1" } else { "-" };
        assert_eq!(driver.event_drop_state(),expected_drop);
        let expected = match row["name"].as_str().unwrap() {
            "install_empty" => Some(Status::Invalid), // Supplied 9 differs from canonical IdMap 2.
            "invalidate" => Some(Status::Done),
            "plan" => Some(Status::Stale), // No generation 2 install in this attachment.
            "ready" | "bind" | "commit" | "cancel" | "result_staged" | "result_offer" => None,
            name => panic!("unclassified contract vector {name}"),
        };
        let state = state.borrow();
        assert_eq!(state.results.len(),1+usize::from(expected.is_some()));
        if let Some(expected) = expected {
            assert!(matches!(state.results[1].message,Message::Result { status,.. } if status == expected));
        }
        assert!(state.offers.is_empty());
    }
    let state = Rc::new(RefCell::new(State::default()));
    let mut driver = BridgeGameDriver::new(Endpoint(Rc::clone(&state)));
    send(&mut driver,&state,1,Message::Invalidate { epoch:7,generation:0 });
    state.borrow_mut().events.push(format!("EVENT|PrivateNavigation|4|0|{}|00",MAX_RECORD+1).into_bytes());
    driver.poll_routed_events().unwrap();
    assert!(driver.event_drop_state().contains("PrivateNavigation:1"));
}

#[test]
fn conflicting_install_mapping_refuses_without_replacing_the_active_generation() {
    let (mut driver,state) = installed();
    let mut replacement = fixture();
    if let Message::Install { bindings,.. } = &mut replacement {
        bindings.generation = 2;
        bindings.actors[0].1 = SimId(9);
    }
    send(&mut driver,&state,3,replacement);
    assert!(matches!(state.borrow().results.last().unwrap().message,Message::Result { status:Status::Invalid,.. }));
    send(&mut driver,&state,4,Message::Plan { origin:Origin::Engine,deadline_tick:100,request:request(1) });
    assert!(matches!(state.borrow().results.last().unwrap().message,Message::Result { status:Status::Staged,.. }));
    send(&mut driver,&state,4,Message::CommitOffer);
    assert_eq!(state.borrow().offers[0].generation,1);
}

#[test]
fn large_authored_plan_reassembles_all_bounded_chunks_before_presenter_delivery() {
    let (mut driver,state) = installed();
    let Message::Install { mut simnav,mut bindings } = fixture() else { unreachable!() };
    let mut nav = SimNav::load(&simnav).unwrap();
    let count = 1300u16;
    nav.rooms[0].cell_len = count;
    nav.cells.clear(); nav.vertices.clear(); bindings.cells.clear();
    for i in 0..count {
        let x = i32::from(i)*100;
        nav.cells.push(NavCell { cell_id:u32::from(i),room_id:0,vertex_first:i*4,
            vertex_len:4,centroid_cm:[x+50,0,50],..NavCell::default() });
        for (dx,z) in [(0,0),(0,100),(100,100),(100,0)] {
            nav.vertices.push(NavVertex { x_cm:x+dx,y_cm:0,z_cm:z });
        }
        bindings.cells.push(CellBinding { cell_id:u32::from(i),lot_id:10,room_id:100,level:0 });
        if i > 0 {
            nav.portals.push(NavPortal { portal_id:u32::from(i-1),cell_a:u32::from(i-1),
                cell_b:u32::from(i),jamb_a_cm:[x,20],jamb_b_cm:[x,80],width_cm:60,..NavPortal::default() });
            bindings.portals.push(PortalBinding { portal_id:u32::from(i-1),object_id:0,
                entry_slot:0,exit_slot:0,cross_lot:false });
        }
    }
    simnav = nav.to_bytes();
    bindings.generation = 2;
    send(&mut driver,&state,3,Message::Install { simnav,bindings });
    assert!(matches!(state.borrow().results.last().unwrap().message,Message::Result { status:Status::Done,.. }));
    let mut req = request(1);
    req.generation = 2;
    req.goal_xyz_mm = [(i32::from(count-1)*100+50)*10,0,500];
    send(&mut driver,&state,4,Message::Plan { origin:Origin::Player(777),deadline_tick:100,request:req });
    assert!(matches!(state.borrow().results.last().unwrap().message,Message::Result { status:Status::Staged,.. }));
    let events = send(&mut driver,&state,4,Message::CommitOffer);
    let state = state.borrow();
    assert_eq!(state.offers.len(),1);
    let envelope = &state.offers[0];
    assert_eq!(envelope.legs.len(),usize::from(count));
    assert_eq!(envelope.legs.last().unwrap().kind,4);
    assert!(envelope.encode().unwrap().len() > 64*1024);
    let points: usize = events.iter().filter(|e| e.event.kind == "SimAnimation")
        .map(|e| m::dec_list(&e.event.fields[0]).unwrap().len()).sum();
    assert_eq!(points,usize::from(count)+1); // Rt plus every ordered boundary leg.
}
