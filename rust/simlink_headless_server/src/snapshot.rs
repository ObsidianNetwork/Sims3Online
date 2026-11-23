#![forbid(unsafe_code)]
//! Snapshot2BinaryReader parity at the bulk boundary. No gameplay runs here.

use serde::Serialize;
use crate::messages::{enc_list, enc_record, hex_encode};

#[derive(Clone, Debug, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Sim {
    pub object_id: u64,
    pub name: String,
    pub household_id: u64,
    pub position: [f32; 3],
    pub interaction_id: Option<u64>,
    pub owning_player_id: Option<i32>,
    pub interaction_key: Option<String>,
    pub lot_id: Option<u64>,
    pub queue_count: Option<i32>,
    pub posture_container: Option<u64>,
    pub posture_type: Option<String>,
    pub description_id: Option<u64>,
}
#[derive(Clone, Debug, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Object {
    pub object_id: u64,
    pub script_class: String,
    pub position: [f32; 3],
    pub lot_id: Option<u64>,
}
#[derive(Clone, Debug, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Snapshot {
    pub tick: i64,
    pub world_name: String,
    pub save_name: String,
    pub speed: String,
    pub sims: Vec<Sim>,
    pub objects: Vec<Object>,
}
struct Reader<'a>(&'a [u8]);
impl Reader<'_> {
    fn bytes(&mut self, n: usize) -> Option<&[u8]> {
        let (head, tail) = self.0.split_at_checked(n)?;
        self.0 = tail;
        Some(head)
    }
    fn u32(&mut self) -> Option<u32> { Some(u32::from_le_bytes(self.bytes(4)?.try_into().ok()?)) }
    fn u64(&mut self) -> Option<u64> { Some(u64::from_le_bytes(self.bytes(8)?.try_into().ok()?)) }
    fn string(&mut self) -> Option<String> {
        let len = self.u32()? as usize;
        // Encoding.UTF8.GetString in C# replaces invalid UTF-8 rather than rejecting it.
        Some(String::from_utf8_lossy(self.bytes(len)?).into_owned())
    }
    fn nullable_string(&mut self) -> Option<Option<String>> {
        let s = self.string()?;
        Some((!s.is_empty()).then_some(s))
    }
    fn id(&mut self) -> Option<Option<u64>> { let id = self.u64()?; Some((id != 0).then_some(id)) }
    fn int(&mut self) -> Option<Option<i32>> { let n = self.u32()?; Some((n != u32::MAX).then_some(n as i32)) }
    fn position(&mut self) -> Option<[f32; 3]> {
        Some([f32::from_bits(self.u32()?), f32::from_bits(self.u32()?), f32::from_bits(self.u32()?)])
    }
}
impl Snapshot {
    pub fn from_bridge(snapshot: simlink_bridge::reply::Snapshot) -> Self {
        Self { tick: snapshot.tick, world_name: snapshot.world, save_name: snapshot.save, speed: snapshot.speed.name().into(),
            sims: snapshot.sims.into_iter().map(|s| Sim { object_id: s.object_id, name: s.name, household_id: s.household,
                position: [s.position.x, s.position.y, s.position.z], interaction_id: s.interaction, owning_player_id: s.owner,
                interaction_key: s.interaction_key, lot_id: s.lot, queue_count: s.queue_count, posture_container: None,
                posture_type: None, description_id: s.desc }).collect(),
            objects: snapshot.objects.into_iter().map(|o| Object { object_id: o.object_id, script_class: o.script_class,
                position: [o.position.x, o.position.y, o.position.z], lot_id: o.lot }).collect() }
    }
    pub fn decode(bytes: &[u8]) -> Option<Self> {
        let mut r = Reader(bytes);
        if r.u32()? != 0x32505353 { return None; }
        let version = r.u32()?;
        if !(1..=2).contains(&version) { return None; }
        let tick = r.u64()? as i64;
        let world_name = r.string()?;
        let save_name = r.string()?;
        let speed = match r.u32()? { 1 => "Normal", 2 => "Speed2", 3 => "Speed3", 4 => "Ultra", _ => "Pause" }.into();
        let count = r.u32()? as i32;
        let mut sims = Vec::new();
        for _ in 0..count {
            let object_id = r.u64()?;
            let name = r.string()?;
            let household_id = r.u64()?;
            let position = r.position()?;
            let interaction_id = r.id()?;
            let interaction_key = r.nullable_string()?;
            let owning_player_id = r.int()?;
            let lot_id = r.id()?;
            let queue_count = r.int()?;
            let posture_container = if r.0.len() >= 8 { r.id()? } else { None };
            let posture_type = if r.0.len() >= 4 { r.nullable_string()? } else { None };
            let description_id = if version >= 2 { r.id()? } else { None };
            sims.push(Sim { object_id, name, household_id, position, interaction_id, owning_player_id,
                interaction_key, lot_id, queue_count, posture_container, posture_type, description_id });
        }
        let count = r.u32()? as i32;
        let mut objects = Vec::new();
        for _ in 0..count {
            objects.push(Object { object_id: r.u64()?, script_class: r.string()?, position: r.position()?, lot_id: r.id()? });
        }
        Some(Self { tick, world_name, save_name, speed, sims, objects })
    }
    /// Whole-record comparison only. Runtime deltas use the mod's sinceTick producer,
    /// never this comparison as an acknowledged-state replacement.
    pub fn diff(&self, baseline: &Self) -> Self {
        Self { sims: self.sims.iter().filter(|s| !baseline.sims.contains(s)).cloned().collect(),
            objects: self.objects.iter().filter(|o| !baseline.objects.contains(o)).cloned().collect(), ..self.clone() }
    }
    pub fn wire_body(&self) -> String {
        enc_record(&[
            self.tick.to_string(), hex_encode(&self.world_name), hex_encode(&self.save_name), self.speed.clone(),
            enc_list(&self.sims.iter().map(Sim::wire).collect::<Vec<_>>()),
            enc_list(&self.objects.iter().map(Object::wire).collect::<Vec<_>>()),
        ])
    }
}
fn optional<T: ToString>(value: Option<T>) -> String { value.map_or_else(|| "~".into(), |v| v.to_string()) }
fn position(p: [f32; 3]) -> String { enc_record(&p.map(|v| v.to_string())) }
impl Sim {
    fn wire(&self) -> String {
        enc_record(&[self.object_id.to_string(), hex_encode(&self.name), self.household_id.to_string(),
            position(self.position), optional(self.interaction_id), optional(self.owning_player_id),
            optional(self.interaction_key.as_deref().map(hex_encode)), optional(self.lot_id), optional(self.queue_count),
            optional(self.posture_container), optional(self.posture_type.as_deref().map(hex_encode)), optional(self.description_id)])
    }
}
impl Object {
    fn wire(&self) -> String {
        enc_record(&[self.object_id.to_string(), hex_encode(&self.script_class), position(self.position), optional(self.lot_id)])
    }
}
