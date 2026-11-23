#![forbid(unsafe_code)]

use std::collections::BTreeMap;
use crate::{KernelError, LotId, SimId};
use crate::kernel_iface::SlotIndex;
use crate::services::Vec3Fixed;

pub const MAX_CONTINUOUS_STEP_CM: u32 = 60;
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum VehicleTravelState { WalkToCurb, FadeAndMountVehicle, RoadSplineSegmentStream, DismountAndFadeIn, ArriveAtLot }
#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord, Hash)]
pub struct VehicleGuid(pub u64);
#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord, Hash)]
pub struct SeatSlot(pub u32);
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct RoadSplineSegment {
    pub tick_id: u64, pub vehicle: VehicleGuid, pub pos: Vec3Fixed,
    pub facing_xz: (i16, i16), pub seat_slot: SeatSlot,
}
#[derive(Clone, Debug)]
pub struct VehicleTravel {
    pub sim: SimId, pub vehicle: VehicleGuid, pub seat: SeatSlot,
    pub state: VehicleTravelState, pub dest_lot: LotId, pub curb_slot: SlotIndex,
    pub spline_stream: Vec<RoadSplineSegment>, pub snaps_detected: u32, pub last_pos: Vec3Fixed,
}
impl VehicleTravel {
    pub fn holds_position_mirror(&self) -> bool {
        matches!(self.state, VehicleTravelState::FadeAndMountVehicle
            | VehicleTravelState::RoadSplineSegmentStream | VehicleTravelState::DismountAndFadeIn)
    }
    pub fn apply_segment(&mut self, seg: RoadSplineSegment) -> Result<(), KernelError> {
        if seg.vehicle != self.vehicle { return Err(KernelError::VehicleGuidUnknown); }
        if seg.seat_slot != self.seat { return Err(KernelError::SeatAlreadyOccupied); }
        if self.state != VehicleTravelState::RoadSplineSegmentStream {
            return Err(KernelError::VehicleGuidUnknown);
        }
        if self.spline_stream.last().is_some_and(|last| seg.tick_id <= last.tick_id) {
            return Err(KernelError::SplineSegmentOutOfOrder);
        }
        let a = self.last_pos;
        let b = seg.pos;
        let distance2: u128 = [(a.0, b.0), (a.1, b.1), (a.2, b.2)].into_iter()
            .map(|(x,y)| { let d = u128::from(x.abs_diff(y)); d*d }).sum();
        if distance2 > u128::from(MAX_CONTINUOUS_STEP_CM).pow(2) {
            self.snaps_detected += 1;
            let cm = distance2.isqrt().min(u128::from(u32::MAX)) as u32;
            return Err(KernelError::DiscontinuousJump(cm));
        }
        self.last_pos = seg.pos;
        self.spline_stream.push(seg);
        Ok(())
    }
    pub fn try_transition_to(&mut self, next: VehicleTravelState) -> Result<(), KernelError> {
        use VehicleTravelState::*;
        if !matches!((self.state, next), (WalkToCurb, FadeAndMountVehicle)
            | (FadeAndMountVehicle, RoadSplineSegmentStream)
            | (RoadSplineSegmentStream, DismountAndFadeIn)
            | (DismountAndFadeIn, ArriveAtLot)) { return Err(KernelError::VehicleGuidUnknown); }
        self.state = next;
        Ok(())
    }
    pub fn transition_to(&mut self, next: VehicleTravelState) {
        // Invalid transitions leave the rider mounted; callers needing an error use try_transition_to.
        let _ = self.try_transition_to(next);
    }
    pub fn bridge_line(&self, tick: u64) -> String {
        use VehicleTravelState::*;
        match self.state {
            FadeAndMountVehicle | DismountAndFadeIn => format!("Vh|{}|{}|{}|{}|{}",
                self.sim.0, self.vehicle.0, self.seat.0,
                if self.state == FadeAndMountVehicle { "hide" } else { "show" }, tick),
            WalkToCurb => format!("Vs|{}|{}|{}", self.vehicle.0, tick, self.dest_lot.0),
            RoadSplineSegmentStream => {
                let facing = self.spline_stream.last().map_or((0, 0), |s| s.facing_xz);
                format!("Vp|{}|{}|{}|{}|{}|{}|{}", self.vehicle.0, tick,
                    self.last_pos.0, self.last_pos.1, self.last_pos.2, facing.0, facing.1)
            }
            ArriveAtLot => format!("Ve|{}|{}|{}|{}|{}", self.vehicle.0, tick,
                self.last_pos.0, self.last_pos.1, self.last_pos.2),
        }
    }
}
/// One vehicle road-segment frame as `bridge_line` writes it (card 13):
/// `Vp|<vehicle>|<tick>|<x>|<y>|<z>|<facing_x>|<facing_z>`.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct VehicleVpFrame {
    pub vehicle: VehicleGuid, pub tick: u64, pub pos: Vec3Fixed, pub facing_xz: (i16, i16),
}

/// The kernel's vehicle-frame reader. A route point (field 1 `rt`, card 54
/// §C.2) is not a vehicle frame: it reads as `None`, so it changes no vehicle
/// state and is not counted (row `vp_rt_ignored_by_vehicle_readers`); so does
/// any line that is not an 8-field numeric `Vp` frame.
pub fn parse_vehicle_vp(line: &str) -> Option<VehicleVpFrame> {
    let f: Vec<&str> = line.split('|').collect();
    if f.len() != 8 || f[0] != "Vp" || f[1] == "rt" {
        return None;
    }
    Some(VehicleVpFrame {
        vehicle: VehicleGuid(f[1].parse().ok()?),
        tick: f[2].parse().ok()?,
        pos: Vec3Fixed(f[3].parse().ok()?, f[4].parse().ok()?, f[5].parse().ok()?),
        facing_xz: (f[6].parse().ok()?, f[7].parse().ok()?),
    })
}

#[derive(Clone, Debug)]
pub struct VehicleOccupancy {
    pub vehicle: VehicleGuid, pub driver: Option<SimId>,
    pub passengers: BTreeMap<SeatSlot, SimId>, pub route_handle: Option<u32>,
    pub lot_id: LotId, pub state: VehicleTravelState,
}
impl VehicleOccupancy {
    pub fn mount(&mut self, seat: SeatSlot, sim: SimId, route: u32) -> Result<(), KernelError> {
        if (seat.0 == 1 && self.driver.is_some()) || self.passengers.contains_key(&seat)
            || self.driver == Some(sim) || self.passengers.values().any(|s| *s == sim) {
            return Err(KernelError::SeatAlreadyOccupied);
        }
        if seat.0 == 1 { self.driver = Some(sim); self.route_handle = Some(route); }
        else { self.passengers.insert(seat, sim); }
        Ok(())
    }
    pub fn park(&mut self, lot: LotId) {
        self.driver = None; self.passengers.clear(); self.route_handle = None;
        self.lot_id = lot; self.state = VehicleTravelState::ArriveAtLot;
    }
}
/// Card 26 §C golden-corpus hook (canary 13, snap detector).
#[cfg(test)]
mod kdiff {
    use super::*;
    use crate::services::Vec3Fixed;

    #[test]
    fn kdiff_canary_13() {
        let mut vt = VehicleTravel {
            sim: SimId(1), vehicle: VehicleGuid(1), seat: SeatSlot(0),
            state: VehicleTravelState::WalkToCurb, dest_lot: LotId(1),
            curb_slot: SlotIndex(0), spline_stream: Vec::new(),
            snaps_detected: 0, last_pos: Vec3Fixed(0, 0, 0),
        };
        vt.try_transition_to(VehicleTravelState::FadeAndMountVehicle).unwrap();
        vt.try_transition_to(VehicleTravelState::RoadSplineSegmentStream).unwrap();
        // A 2 m jump trips the continuous-step check at exactly its cm count.
        let jump = vt.apply_segment(RoadSplineSegment {
            tick_id: 1, vehicle: VehicleGuid(1), pos: Vec3Fixed(200, 0, 0),
            facing_xz: (0, 0), seat_slot: SeatSlot(0),
        });
        let cm = match jump {
            Err(KernelError::DiscontinuousJump(cm)) => cm as u64,
            _ => u64::MAX,
        };
        println!("KDIFF|canary=13|case=discontinuous_jump_cm|got={:016x}", cm);
        assert_eq!(cm, 200);
        assert_eq!(vt.snaps_detected, 1);
        // A refused segment never moved the mirror: the next legal step is
        // measured from the last *committed* position (30 cm from origin).
        vt.apply_segment(RoadSplineSegment {
            tick_id: 2, vehicle: VehicleGuid(1), pos: Vec3Fixed(30, 0, 0),
            facing_xz: (0, 0), seat_slot: SeatSlot(0),
        }).unwrap();
        assert_eq!(vt.snaps_detected, 1);
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn long_ride_rejects_wrong_identity_order_and_snaps_without_moving() {
        let mut v = VehicleTravel { sim: SimId(1), vehicle: VehicleGuid(2), seat: SeatSlot(2),
            state: VehicleTravelState::WalkToCurb, dest_lot: LotId(3), curb_slot: SlotIndex(0),
            spline_stream: Vec::new(), snaps_detected: 0, last_pos: Vec3Fixed(0, 0, 0) };
        assert!(v.try_transition_to(VehicleTravelState::ArriveAtLot).is_err());
        v.try_transition_to(VehicleTravelState::FadeAndMountVehicle).unwrap();
        assert!(v.bridge_line(0).starts_with("Vh|"));
        v.try_transition_to(VehicleTravelState::RoadSplineSegmentStream).unwrap();
        for tick in 1..=290 {
            v.apply_segment(RoadSplineSegment { tick_id: tick, vehicle: v.vehicle,
                pos: Vec3Fixed((tick * 16_400 / 290) as u32, 0, 0), facing_xz: (1,0), seat_slot: v.seat }).unwrap();
        }
        assert_eq!(v.snaps_detected, 0);
        let last = *v.spline_stream.last().unwrap();
        assert_eq!(v.apply_segment(last), Err(KernelError::SplineSegmentOutOfOrder));
        let jump = RoadSplineSegment { tick_id: 291, pos: Vec3Fixed(16_600, 0, 0), ..last };
        assert_eq!(v.apply_segment(jump), Err(KernelError::DiscontinuousJump(200)));
        assert_eq!(v.last_pos.0, 16_400);
        v.try_transition_to(VehicleTravelState::DismountAndFadeIn).unwrap();
        assert!(v.bridge_line(292).contains("|show|"));
        v.try_transition_to(VehicleTravelState::ArriveAtLot).unwrap();
        assert!(!v.holds_position_mirror());
    }
    #[test]
    fn seats_are_atomic_and_arrival_releases_every_rider() {
        let mut o = VehicleOccupancy { vehicle: VehicleGuid(1), driver: None,
            passengers: BTreeMap::new(), route_handle: None, lot_id: LotId(1),
            state: VehicleTravelState::WalkToCurb };
        o.mount(SeatSlot(1), SimId(1), 9).unwrap();
        assert_eq!(o.mount(SeatSlot(1), SimId(2), 9), Err(KernelError::SeatAlreadyOccupied));
        o.mount(SeatSlot(2), SimId(2), 9).unwrap();
        o.park(LotId(2));
        assert!(o.driver.is_none() && o.passengers.is_empty() && o.route_handle.is_none());
    }
}
