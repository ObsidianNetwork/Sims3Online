#![forbid(unsafe_code)]
//! Actual actor/socket ordering over the existing native lane-6 memory format.
pub mod common;
use simlink_headless_server::{messages::{self,Command},ring_pages::{PageSource,RingPages,PAGE_LANE},
    serve::RunningServer,snapshot::Snapshot,stream::StreamOptions,transport::{StreamRequest,StreamReply},
    DriverError,FakeDriver,GameDriver};
use simlink_bridge::ring::{MemorySection,RingReader};
use std::{io::{BufRead,BufReader,Write},net::{TcpListener,TcpStream},sync::{Arc,Mutex,atomic::{AtomicBool,AtomicU64,Ordering}},time::{Duration,Instant}};
struct Shared {
    memory:MemorySection, clock:AtomicU64, source_seq:AtomicU64, calls:Mutex<Vec<String>>,
    hold:AtomicBool, release:AtomicBool, pending:AtomicBool, late_off:AtomicBool, world_calls:AtomicU64,
}
impl Shared {fn new()->Arc<Self>{Arc::new(Self{memory:MemorySection::new(1<<18,77),clock:AtomicU64::new(0),source_seq:AtomicU64::new(5),
    calls:Mutex::new(Vec::new()),hold:AtomicBool::new(false),release:AtomicBool::new(false),pending:AtomicBool::new(false),late_off:AtomicBool::new(false),world_calls:AtomicU64::new(0)})}}
struct Driver {fake:FakeDriver,shared:Arc<Shared>,pages:RingPages<MemorySection>,pending:Option<Vec<String>>}
impl Driver {
    fn answer(&mut self,args:&[String])->String {
        match args[0].as_str(){
            "ring-on"=>"recorder=on;ring=on".into(),
            "routes-on"=>"routes=on;recorder=on;ring=on".into(),
            "push-off"=>{
                if self.shared.late_off.swap(false,Ordering::SeqCst) {
                    self.shared.source_seq.store(2147483651,Ordering::SeqCst);
                    assert!(self.shared.memory.publish(PAGE_LANE,i32::MIN+3,0,"seq=2147483651;more=0\n2147483651|X|7"));
                }
                let seq=self.shared.source_seq.load(Ordering::SeqCst);
                format!("push=off,cursor={seq},sequence={seq},behind=0")
            },
            "push-on"|"push-targets"|"push-status"=>{let seq=self.shared.source_seq.load(Ordering::SeqCst);format!("push=on,cursor={seq},sequence={seq},behind=0")},
            "snapshot"=>"sims=10;drivers=7".into(),
            "snapshot-driver"=>"next=-1".into(),
            "dump"=>"seq=5;more=0".into(),
            other=>panic!("unexpected recorder request {other}"),
        }
    }
}
impl GameDriver for Driver {
    fn adjudicate(&mut self,c:&Command)->Result<Vec<u8>,DriverError>{self.fake.adjudicate(c)}
    fn poll_event_lines(&mut self)->Result<Vec<Vec<u8>>,DriverError>{Ok(vec![])}
    fn claimed_sims(&self)->Vec<u64>{vec![10]}
    fn ring_available(&self)->bool{true}
    fn presentation_pages(&mut self)->Result<Vec<String>,DriverError>{Ok(self.pages.take_pages())}
    fn discard_pages(&mut self)->Result<usize,DriverError>{Ok(self.pages.discard_pages())}
    fn ring_report(&self)->String{self.pages.report()}
    fn stream_request_pending(&self)->bool{self.pending.is_some()}
    fn admin_request(&mut self,request:&simlink_headless_server::admin::Request)->Result<String,DriverError>{
        assert!(!self.stream_request_pending(),"admin bypassed actual pending native IO");
        if request.name.starts_with("LOAD_SAVE"){self.shared.world_calls.fetch_add(1,Ordering::SeqCst);}
        Ok(simlink_headless_server::admin::report("native command reached"))
    }
    fn start_stream_request(&mut self,r:&StreamRequest)->Result<Option<StreamReply>,DriverError>{
        assert!(self.pending.is_none(),"replaced native IO owner");
        Ok(Some(match r{
            StreamRequest::Recorder(args)=>{
                self.shared.calls.lock().unwrap().push(args.join("|"));
                if args[0]=="snapshot-driver" && self.shared.hold.swap(false,Ordering::SeqCst){
                    self.pending=Some(args.clone());self.shared.pending.store(true,Ordering::SeqCst);return Ok(None);
                }
                StreamReply::Recorder(self.answer(args))
            }
            StreamRequest::Snapshot(_)=>StreamReply::Snapshot(Some(Snapshot{tick:1,world_name:"f".into(),save_name:"f".into(),speed:"Normal".into(),sims:vec![],objects:vec![]})),
            StreamRequest::DiagState=>StreamReply::DiagState(None),
            StreamRequest::ProbeSimState(_)=>StreamReply::SimState("sims=0".into()),
            StreamRequest::ExportSimDescription(_)=>panic!("capability disabled"),
        }))
    }
    fn poll_stream_request(&mut self)->Result<Option<StreamReply>,DriverError>{
        if !self.shared.release.load(Ordering::SeqCst){return Ok(None);}
        let Some(args)=self.pending.take() else{return Ok(None);};
        self.shared.pending.store(false,Ordering::SeqCst);
        Ok(Some(StreamReply::Recorder(self.answer(&args))))
    }
}
fn start(shared:&Arc<Shared>)->RunningServer{
    let d=Arc::clone(shared);let clock=Arc::clone(shared);
    RunningServer::start_configured_with_clock(TcpListener::bind("127.0.0.1:0").unwrap(),Some(TcpListener::bind("127.0.0.1:0").unwrap()),
        move||{let reader=RingReader::open(d.memory.clone(),Some(77)).unwrap();Ok(Driver{fake:FakeDriver::default(),shared:d,pages:RingPages::new(reader),pending:None})},8,
        StreamOptions{ring_requested:true,route_stream_requested:true,base_interval_ms:10_000.0,..StreamOptions::default()},move||clock.clock.fetch_add(10,Ordering::SeqCst)).unwrap()
}
fn wait(mut f:impl FnMut()->bool){let end=Instant::now()+Duration::from_secs(8);while !f(){assert!(Instant::now()<end,"ring transport deadline");std::thread::sleep(Duration::from_millis(5));}}
fn admin(s:&RunningServer,command:&str)->serde_json::Value{
    let mut socket=TcpStream::connect(s.admin_addr().unwrap()).unwrap();socket.set_read_timeout(Some(Duration::from_secs(3))).unwrap();
    writeln!(socket,"{command}").unwrap();let mut row=String::new();BufReader::new(socket).read_line(&mut row).unwrap();serde_json::from_str(&row).unwrap()
}
fn joined(s:&RunningServer,player:u64)->common::Peer{let mut p=common::Peer::connect(s);assert!(p.exchange(&format!("CMD|1|JoinSession|1|{player}|6a|~")).starts_with("ACK|1|JoinSession"));p}
fn lines_until(peer:&mut common::Peer,needle:&str)->Vec<String>{
    let end=Instant::now()+Duration::from_secs(8);let mut rows=vec![];
    loop{assert!(Instant::now()<end,"no live record {needle}: {rows:?}");let row=peer.next().unwrap();
        let fields=simlink_headless_server::wire_v1::split(row.as_bytes()).unwrap();
        if fields.first().map(String::as_str)==Some("EV") {let e=simlink_headless_server::decode_event(&fields).unwrap();if e.kind=="SimAnimation"{
            for line in messages::dec_list(&e.fields[0]).unwrap(){rows.push(messages::hex_decode(&line).unwrap());}
            if rows.iter().any(|row|row==needle){return rows;}
        }}
    }
}
#[test]
fn startup_quarantines_old_pages_and_managed_stop_waits_for_pending_io_and_preserves_both_sockets(){
    let shared=Shared::new();assert!(shared.memory.publish(PAGE_LANE,2,0,"seq=2;more=0\n2|AW|99"));
    let server=start(&shared);let mut c1=joined(&server,1);let mut c2=joined(&server,2);
    wait(||admin(&server,"FRONT_DOOR_STATE")["report"].as_str().unwrap().contains("presentation_join_history_pending=0"));
    wait(||shared.calls.lock().unwrap().iter().any(|s|s.starts_with("push-on|")));
    let calls=shared.calls.lock().unwrap().clone();assert_eq!(&calls[..3], &["ring-on","push-off","routes-on"]);
    assert!(admin(&server,"FRONT_DOOR_STATE")["report"].as_str().unwrap().contains("presentation_ring_startup_discarded=1"));
    let record="2147483650|AW|7";shared.source_seq.store(2147483650,Ordering::SeqCst);
    assert!(shared.memory.publish(2,1,0,"mirror must not forward"));
    assert!(shared.memory.publish(PAGE_LANE,i32::MIN+2,0,&format!("seq=2147483650;more=0\n{record}")));
    for c in [&mut c1,&mut c2]{let rows=lines_until(c,record);assert!(!rows.iter().any(|r|r=="2|AW|99"));assert_eq!(rows.iter().filter(|r|r.as_str()==record).count(),1);}
    for command in ["LOAD_SAVE 66","LOAD_SAVE_MENU 66","SACS_RECORDER ring-off","SACS_RECORDER push-on 0 -","SACS_RECORDER snapshot 10","SACS_RECORDER dump 0 10"]{
        let refused=admin(&server,command);assert_eq!(refused["ok"],false,"{command}");assert!(refused["error"].is_string(),"{command}");
    }
    assert_eq!(shared.world_calls.load(Ordering::SeqCst),0);
    // A genuine same-socket rejoin starts another history IO and holds its final reply.
    shared.hold.store(true,Ordering::SeqCst);
    assert!(c2.exchange("CMD|2|LeaveSession|2|2").starts_with("ACK|2|LeaveSession"));
    assert!(c2.exchange("CMD|3|JoinSession|3|2|6a|~").starts_with("ACK|3|JoinSession"));
    wait(||shared.pending.load(Ordering::SeqCst));
    let off_before=shared.calls.lock().unwrap().iter().filter(|s|s.as_str()=="push-off").count();
    shared.late_off.store(true,Ordering::SeqCst);
    let pending=admin(&server,"SACS_RECORDER push-off");assert_eq!(pending["ok"],true);assert_eq!(pending["accepted"],false);assert_eq!(pending["reason"],"presentation_push_off_pending");
    assert_eq!(shared.calls.lock().unwrap().iter().filter(|s|s.as_str()=="push-off").count(),off_before);
    let final_x="2147483651|X|7";
    shared.release.store(true,Ordering::SeqCst);
    wait(||admin(&server,"SACS_RECORDER push-off")["accepted"]==true);
    let ready=admin(&server,"SACS_RECORDER push-off");assert_eq!(ready["report"],"presentation_push_off=ready");
    for c in [&mut c1,&mut c2]{assert_eq!(lines_until(c,final_x).iter().filter(|r|r.as_str()==final_x).count(),1);}
    let starts=shared.calls.lock().unwrap().iter().filter(|s|s.starts_with("push-on|")).count();
    shared.clock.store(600_000,Ordering::SeqCst);std::thread::sleep(Duration::from_millis(40));
    assert_eq!(shared.calls.lock().unwrap().iter().filter(|s|s.starts_with("push-on|")).count(),starts,"manual stop must persist through rearm deadlines");
    assert_eq!(admin(&server,"LOAD_SAVE 66")["ok"],false);assert_eq!(shared.world_calls.load(Ordering::SeqCst),0,"transport-ready is not world/history quiescence");
    server.shutdown().unwrap();
}
