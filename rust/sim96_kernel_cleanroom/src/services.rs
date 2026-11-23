#![forbid(unsafe_code)]

use std::collections::BTreeMap;
use crate::{KernelError, LotId, ObjectGuid};

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum ServiceKind {
    NewspaperDelivery, MailCarrier, Maid, Repairman, PizzaDelivery, Burglar,
    Firefighter, GrimReaper, Police, Nanny, Butler, Groundskeeper, Stylist, Doctor,
    TowniePool, PetAdoptionPool, Other(u32),
}
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum RoleAuthority { Server, PresenterClient, HeadlessMirror }
impl RoleAuthority {
    pub fn may_spawn_world_service(self) -> bool { self == Self::Server }
}
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Vec3Fixed(pub u32, pub u32, pub u32);
#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord, Hash)]
pub struct ResourceKey(pub u32, pub u32, pub u64);
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct ObjectSpawnedFrame {
    pub guid: ObjectGuid,
    pub resource_key: ResourceKey,
    pub lot_id: LotId,
    pub pos: Vec3Fixed,
    pub tick_id: u64,
}
pub struct WorldServiceSpawnerGate {
    pub role: RoleAuthority,
    pub m_service_index: u32,
    pub spawn_frames: Vec<ObjectSpawnedFrame>,
}
impl WorldServiceSpawnerGate {
    pub fn new(role: RoleAuthority) -> Self {
        Self { role, m_service_index: 0, spawn_frames: Vec::new() }
    }
    /// Every subclass, including unknown future services, passes the same gate.
    pub fn simulate_round(&mut self, services: &[ServiceKind]) -> Vec<Result<(), KernelError>> {
        self.m_service_index = self.m_service_index.wrapping_add(1);
        services.iter().map(|_| if self.role.may_spawn_world_service() { Ok(()) }
            else { Err(KernelError::ClientWorldServiceSuppressed) }).collect()
    }
    pub fn on_server_spawn(&mut self, frame: ObjectSpawnedFrame) {
        if self.role.may_spawn_world_service() { self.spawn_frames.push(frame); }
    }
}
/// Stable server identities are independent of a client's local arena indices.
#[derive(Default)]
pub struct SpawnBindings { pub objects: BTreeMap<ObjectGuid, ObjectSpawnedFrame> }
impl SpawnBindings {
    pub fn bind(&mut self, frame: ObjectSpawnedFrame) -> bool {
        if self.objects.get(&frame.guid).is_some_and(|old| old.tick_id >= frame.tick_id) { return false; }
        self.objects.insert(frame.guid, frame);
        true
    }
}
/// Card 26 §C golden-corpus hook (canary 12, gate).
#[cfg(test)]
mod kdiff {
    use super::*;

    #[test]
    fn kdiff_canary_12() {
        let services = [ServiceKind::NewspaperDelivery, ServiceKind::MailCarrier];
        let mut client = WorldServiceSpawnerGate::new(RoleAuthority::PresenterClient);
        let accepted = client.simulate_round(&services).iter().filter(|r| r.is_ok()).count() as u64;
        println!("KDIFF|canary=12|case=client_gate_zero|got={:016x}", accepted);
        assert_eq!(accepted, 0);
        assert!(client.spawn_frames.is_empty());
        let mut server = WorldServiceSpawnerGate::new(RoleAuthority::Server);
        let ok = server.simulate_round(&services).iter().filter(|r| r.is_ok()).count() as u64;
        println!("KDIFF|canary=12|case=server_gate_passes|got={:016x}", ok);
        assert_eq!(ok, 2);
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn all_twenty_two_service_instances_are_gated_before_first_replay() {
        let services: Vec<_> = (0..22).map(ServiceKind::Other).collect();
        let frame = ObjectSpawnedFrame { guid: ObjectGuid(0xdead_beef),
            resource_key: ResourceKey(1, 0, 2), lot_id: LotId(3), pos: Vec3Fixed(1, 2, 3), tick_id: 7 };
        for role in [RoleAuthority::Server, RoleAuthority::PresenterClient, RoleAuthority::HeadlessMirror] {
            let mut gate = WorldServiceSpawnerGate::new(role);
            for _ in 0..1000 {
                let results = gate.simulate_round(&services);
                assert!(results.iter().all(|r| r.is_ok() == role.may_spawn_world_service()));
                gate.on_server_spawn(frame);
            }
            assert_eq!(gate.m_service_index, 1000);
            assert_eq!(gate.spawn_frames.len(), if role == RoleAuthority::Server { 1000 } else { 0 });
        }
        let mut client = SpawnBindings::default();
        assert!(client.bind(frame));
        assert!(!client.bind(frame));
        assert_eq!(client.objects[&frame.guid], frame);
    }
}
