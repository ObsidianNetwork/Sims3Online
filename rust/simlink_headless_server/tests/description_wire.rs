#![forbid(unsafe_code)]
//! Exact raw-byte public framing. C# consumer interoperability is a separate gate.
use simlink_headless_server::{messages as m, serve, wire_v1 as w};

fn event(arrays: &[Vec<u8>; 3]) -> m::Event {
    m::Event { seq: 1, tick: 2, corr: 0, kind: "SimDescriptionData".into(),
        fields: std::iter::once((u64::MAX - 1).to_string()).chain(arrays.iter().map(|b| m::hex_encode_bytes(b))).collect() }
}

#[test]
fn raw_byte_helpers_do_not_apply_utf8_or_accept_native_uppercase() {
    assert_eq!(m::hex_encode_bytes(&[0, 127, 128, 255, 195, 40]), "007f80ffc328");
    assert_eq!(m::hex_decode_bytes("007f80ffc328").unwrap(), [0, 127, 128, 255, 195, 40]);
    assert_eq!(m::hex_decode_bytes("").unwrap(), Vec::<u8>::new());
    assert!(m::hex_decode("c328").is_err(), "the public text scalar still requires UTF8");
    for bad in ["FF", "f", "[0,255]", "gg"] { assert!(m::hex_decode_bytes(bad).is_err()); }
}

#[test]
fn description_frames_use_three_raw_bulks_and_preserve_exact_bytes() {
    for arrays in [
        [vec![0, 127, 128, 255], vec![], vec![0, 255]],
        [vec![0], vec![], vec![]],
        [vec![0xb1, 0x23], vec![], vec![]],
        [(0..524_288).map(|i| [0, 127, 128, 255][i % 4]).collect(), vec![], vec![]],
    ] {
        let mut next = 7;
        let lines = serve::encode_event_lines(&event(&arrays), &mut next).unwrap();
        assert_eq!(next, 10);
        assert_eq!(lines.len(), 4);
        assert!(serve::event_names_bulk(&lines[0]), "replay must not orphan a raw introducer");
        assert!(lines.iter().all(|line| line.len() <= m::PUBLIC_LINE_MAX_BYTES));
        let header = w::decode_event(&w::split(lines[0].as_bytes()).unwrap()).unwrap();
        assert_eq!((header.seq, header.tick, header.corr), (1, 2, 0));
        assert_eq!(header.kind, "SimDescriptionData");
        assert_eq!(header.fields, [(u64::MAX - 1).to_string(), "b7".into(), "b8".into(), "b9".into()]);
        let mut receiver = w::BulkReceiver::new();
        for (index, line) in lines[1..].iter().enumerate() {
            let chunk = w::decode_bulk(&w::split(line.as_bytes()).unwrap()).unwrap();
            assert_eq!((chunk.bulk_id, chunk.index, chunk.count), (7 + index as u64, 0, 1));
            assert_eq!(receiver.submit(&chunk).unwrap(), arrays[index]);
        }
    }
}

#[test]
fn invalid_description_and_exhausted_bulk_ids_are_transactional() {
    for fields in [vec!["1", "[0,255]", "", ""], vec!["1", "ff", ""], vec!["1", "FF", "", ""]] {
        let mut next = 7;
        let mut value = event(&[vec![], vec![], vec![]]);
        value.fields = fields.into_iter().map(str::to_owned).collect();
        assert!(serve::encode_event_lines(&value, &mut next).is_err());
        assert_eq!(next, 7);
    }
    let mut next = 7;
    assert!(serve::encode_event_lines(&event(&[vec![0; 524_289], vec![], vec![]]), &mut next).is_err());
    assert_eq!(next, 7);
    let mut next = u64::MAX - 1;
    assert!(serve::encode_event_lines(&event(&[vec![], vec![], vec![]]), &mut next).is_err());
    assert_eq!(next, u64::MAX - 1);
}

#[test]
fn explicit_description_request_uses_the_same_raw_byte_scalar() {
    use simlink_headless_server::{bridge_driver::{BridgeEndpoint, BridgeGameDriver}, ConnId, DriverError, GameDriver};
    struct Endpoint(Option<simlink_bridge::verbs::SimDescriptionPayload>);
    impl BridgeEndpoint for Endpoint {
        fn command(&mut self, command: &simlink_bridge::verbs::BridgeCommand) -> Result<Vec<String>, DriverError> {
            let simlink_bridge::verbs::BridgeCommand::ExportSimDescription { corr, .. } = command else {
                panic!("unexpected command: {command:?}");
            };
            // This endpoint already owns the assembled payload. The driver captures
            // it only after the correlated native completion, just as LiveBridge does.
            let payload = self.0.as_ref().unwrap();
            let bytes: Vec<_> = payload.property_stream.iter().chain(&payload.res_key_table)
                .chain(&payload.object_id_table).copied().collect();
            let report = format!("result=exported;slot=0;bytes={};props={};keys={};objects={};crc={:08X}",
                bytes.len(), payload.property_stream.len(), payload.res_key_table.len(),
                payload.object_id_table.len(), simlink_bridge::crc32::crc32(&bytes));
            Ok(vec![format!("AUTONOMY_PROBE|{}|ok|{}", corr.to_n(), simlink_bridge::text::encode_text(&report))])
        }
        fn events(&mut self) -> Result<Vec<Vec<u8>>, DriverError> { Ok(Vec::new()) }
        fn take_description(&mut self) -> Option<simlink_bridge::verbs::SimDescriptionPayload> { self.0.take() }
    }
    let mut driver = BridgeGameDriver::new(Endpoint(Some(simlink_bridge::verbs::SimDescriptionPayload {
        desc: u64::MAX - 1, property_stream: vec![0, 127, 128, 255], res_key_table: vec![], object_id_table: vec![0xb1, 0x23] })));
    let command = m::Command { corr: 9, command_id: 9, player_id: 1, verb: "RequestSimDescription".into(), args: vec![(u64::MAX - 1).to_string()] };
    assert!(driver.adjudicate_for(ConnId(1), &command).unwrap().starts_with(b"ACK|9|RequestSimDescription"));
    let events = driver.take_pending_events();
    let description = events.iter().find(|event| event.event.kind == "SimDescriptionData").unwrap();
    assert_eq!(description.requester, Some(ConnId(1)));
    assert_eq!(description.event.fields, [(u64::MAX - 1).to_string(), "007f80ff".into(), "".into(), "b123".into()]);
    let mut next = 1;
    let lines = serve::encode_event_lines(&description.event, &mut next).unwrap();
    assert_eq!(lines.len(), 4);
    assert!(serve::event_names_bulk(&lines[0]));
}
