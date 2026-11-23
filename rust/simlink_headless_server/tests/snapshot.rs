#![forbid(unsafe_code)]

use simlink_headless_server::snapshot::Snapshot;

fn u32le(bytes: &mut Vec<u8>, n: u32) { bytes.extend(n.to_le_bytes()); }
fn u64le(bytes: &mut Vec<u8>, n: u64) { bytes.extend(n.to_le_bytes()); }
fn text(bytes: &mut Vec<u8>, s: &str) { u32le(bytes, s.len() as u32); bytes.extend(s.as_bytes()); }

pub fn fixture(version: u32) -> Vec<u8> {
    let mut b = Vec::new();
    u32le(&mut b, 0x32505353); u32le(&mut b, version); u64le(&mut b, 77);
    text(&mut b, "World"); text(&mut b, "Save"); u32le(&mut b, 2);
    u32le(&mut b, 1); u64le(&mut b, u64::MAX); text(&mut b, "Sim");
    u64le(&mut b, 18);
    for f in [1.25f32, -2.0, 3.0] { b.extend(f.to_le_bytes()); }
    u64le(&mut b, 99); text(&mut b, "Interaction"); u32le(&mut b, 0xfffffffe);
    u64le(&mut b, 44); u32le(&mut b, 2); u64le(&mut b, 55); text(&mut b, "Posture");
    if version == 2 { u64le(&mut b, 66); }
    u32le(&mut b, 1); u64le(&mut b, 88); text(&mut b, "Object");
    for f in [4.0f32, 5.0, 6.0] { b.extend(f.to_le_bytes()); }
    u64le(&mut b, 0);
    b
}

#[test]
fn snapshot2_versions_fields_truncation_and_mod_delta() {
    for version in [1, 2] {
        let bytes = fixture(version);
        let snapshot = Snapshot::decode(&bytes).unwrap();
        assert_eq!(snapshot.tick, 77);
        assert_eq!(snapshot.speed, "Speed2");
        assert_eq!(snapshot.sims[0].object_id, u64::MAX);
        assert_eq!(snapshot.sims[0].owning_player_id, Some(-2));
        assert_eq!(snapshot.sims[0].description_id, (version == 2).then_some(66));
        assert_eq!(snapshot.objects[0].lot_id, None);
        for len in 0..bytes.len() { assert!(Snapshot::decode(&bytes[..len]).is_none(), "truncated {len}"); }
        let mut next = snapshot.clone();
        next.tick += 1;
        assert!(next.diff(&snapshot).sims.is_empty());
        next.sims[0].queue_count = Some(3);
        assert_eq!(next.diff(&snapshot).sims.len(), 1);
        assert!(next.diff(&snapshot).objects.is_empty());
        assert!(snapshot.wire_body().starts_with("(77;"));
    }
    let mut invalid = fixture(2);
    invalid[4..8].copy_from_slice(&3u32.to_le_bytes());
    assert!(Snapshot::decode(&invalid).is_none());
}
