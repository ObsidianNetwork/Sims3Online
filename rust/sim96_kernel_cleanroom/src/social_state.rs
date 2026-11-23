#![forbid(unsafe_code)]
//! Motives, relationships (LTR / STC), and social state (spec card 09).
//! Motives are fixed-point `i32` scaled by `MOTIVE_FIXED_SCALE` (no
//! float drift). The relationship matrix is keyed by a canonical symmetric pair
//! (`min, max`) so `Get(a, b) == Get(b, a)`. `ltr_threshold_check` mirrors
//! `CalculateLTRState` (`LongTermRelationship.cs:1468`): `Acquaintance` needs the
//! `HasMet` bit; `Enemy` needs the `enemies` bit (liking alone never produces it).

use std::collections::BTreeMap;

use crate::SimId;

/// Motive bounds (0..=100) and the fixed-point scale (4 decimals).
pub const MOTIVE_MIN: i32 = 0;
pub const MOTIVE_MAX: i32 = 100;
pub const MOTIVE_FIXED_SCALE: i32 = 10_000;

/// Liking bounds (-100..=100).
pub const LIKING_MIN: i32 = -100;
pub const LIKING_MAX: i32 = 100;

/// Clean-room interaction bits used by the sealed referee model.
pub mod bits {
    pub const HAS_MET: u32 = 1 << 0;
    pub const DATING: u32 = 1 << 1;
    pub const ENEMIES: u32 = 1 << 2;
    pub const AFFAIR: u32 = 1 << 3;
    pub const MARRIED: u32 = 1 << 4;
}

/// 4-byte native fixed-point motive word.
#[repr(C)]
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct MotiveValueRaw(pub i32);

const _: () = assert!(std::mem::size_of::<MotiveValueRaw>() == 4);
const _: () = assert!(std::mem::offset_of!(MotiveValueRaw, 0) == 0);

/// A fixed-point motive level (raw, scale `MOTIVE_FIXED_SCALE`).
#[derive(Clone, Copy, Debug, PartialEq, Eq, Default)]
pub struct MotiveValue(pub i32);

impl MotiveValue {
    pub fn clamp(self) -> MotiveValue {
        MotiveValue(self.0.clamp(MOTIVE_MIN * MOTIVE_FIXED_SCALE, MOTIVE_MAX * MOTIVE_FIXED_SCALE))
    }
}

/// The seven core motives (card 09 §A).
#[derive(Clone, Copy, Debug, Default)]
pub struct MotiveStore {
    pub hunger: MotiveValue,
    pub energy: MotiveValue,
    pub bladder: MotiveValue,
    pub hygiene: MotiveValue,
    pub social: MotiveValue,
    pub fun: MotiveValue,
    pub room: MotiveValue,
    pub base_loss_raw_per_tick: [i32; 7],
    pub fractional_delta_16: [i32; 7],
    pub fractional_carry_16: [i64; 7],
}

/// A commodity kind the buff decay modifier targets.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum CommodityKind {
    Hunger,
    Energy,
    Bladder,
    Hygiene,
    Social,
    Fun,
    Room,
}

/// A buff's per-tick decay delta on one commodity (card 09 §A.2).
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct BuffCommodityDecayModifier {
    pub commodity: CommodityKind,
    pub delta_raw_per_tick: i32,
}

/// Apply one tick of motive decay. Deltas are summed per commodity before the
/// final clamp (card 09 §A.2: clamp after all stacking, not per modifier).
pub fn tick_motive_decay(m: &mut MotiveStore, buffs: &[BuffCommodityDecayModifier]) {
    let kinds = [CommodityKind::Hunger, CommodityKind::Energy, CommodityKind::Bladder,
        CommodityKind::Hygiene, CommodityKind::Social, CommodityKind::Fun, CommodityKind::Room];
    for (index, kind) in kinds.into_iter().enumerate() {
        let delta: i64 = buffs
            .iter()
            .filter(|b| b.commodity == kind)
            .map(|b| i64::from(b.delta_raw_per_tick))
            .sum();
        let fraction = i128::from(m.fractional_carry_16[index]) + i128::from(m.fractional_delta_16[index]);
        m.fractional_carry_16[index] = (fraction % 65_536) as i64;
        let total = i128::from(delta) - i128::from(m.base_loss_raw_per_tick[index]) + fraction / 65_536;
        let slot = match kind {
            CommodityKind::Hunger => &mut m.hunger, CommodityKind::Energy => &mut m.energy,
            CommodityKind::Bladder => &mut m.bladder, CommodityKind::Hygiene => &mut m.hygiene,
            CommodityKind::Social => &mut m.social, CommodityKind::Fun => &mut m.fun,
            CommodityKind::Room => &mut m.room,
        };
        slot.0 = (i128::from(slot.0) + total)
            .clamp(i128::from(MOTIVE_MIN * MOTIVE_FIXED_SCALE), i128::from(MOTIVE_MAX * MOTIVE_FIXED_SCALE)) as i32;
    }
}

/// A canonical, symmetric pair key: always `(min, max)` so both directions hash
/// and compare equal (card 09 §C.2, the fact).
#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord, Hash)]
pub struct RelationshipPair {
    pub lo: SimId,
    pub hi: SimId,
}

impl RelationshipPair {
    pub fn new(a: SimId, b: SimId) -> Self {
        if a <= b {
            Self { lo: a, hi: b }
        } else {
            Self { lo: b, hi: a }
        }
    }
}

/// The long-term relationship state (card 09 §C.1).
#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord, Hash)]
pub enum LongTermRelationshipType {
    Stranger,
    Acquaintance,
    Friend,
    GoodFriend,
    BestFriend,
    RomanticInterest,
    Enemy,
}

/// The short-term context type (card 09 §B).
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum ShortTermContextType {
    Friendly,
    Flirty,
    Awkward,
    Hostile,
    Neutral,
}

/// The full per-pair relationship state (the nine wire fields + pair key).
#[derive(Clone, Copy, Debug)]
pub struct RelationshipState {
    pub pair: RelationshipPair,
    pub liking: i32,
    pub stc_commodity: i32,
    pub stc_type: ShortTermContextType,
    pub stc_perceived: SimId,
    pub ltr: LongTermRelationshipType,
    pub interaction_bits: u32,
    pub ltr_start_ticks: u64,
    pub highest_liking: i32,
    pub when_last_greeted: u64,
    pub when_last_talked: u64,
}

/// The symmetric relationship matrix (card 09 §C).
#[derive(Default, Debug)]
pub struct RelationshipMatrix {
    pub pairs: BTreeMap<RelationshipPair, RelationshipState>,
}

impl RelationshipMatrix {
    pub fn get(&self, a: SimId, b: SimId) -> Option<&RelationshipState> {
        self.pairs.get(&RelationshipPair::new(a, b))
    }

    /// Set the nine mirror fields verbatim, with no `UpdateLTR` side effects
    /// (card 09 §C.3: the client sets what `CopyLtr` copies, no re-derivation).
    pub fn set_from_mirror(&mut self, st: RelationshipState) {
        let pair = RelationshipPair::new(st.pair.lo, st.pair.hi);
        self.pairs.insert(pair, RelationshipState { pair, ..st });
    }

    pub fn adjust_liking(&mut self, a: SimId, b: SimId, delta: i32) -> Option<i32> {
        let state = self.pairs.get_mut(&RelationshipPair::new(a, b))?;
        state.liking = state.liking.saturating_add(delta).clamp(LIKING_MIN, LIKING_MAX);
        state.highest_liking = state.highest_liking.max(state.liking);
        Some(state.liking)
    }

    /// Mirror of `CalculateLTRState` (`:1468`): a deterministic pure function of
    /// `(liking, bits)`. `Enemy` needs the `enemies` bit; `RomanticInterest`
    /// needs `dating`/`affair`; `Acquaintance` needs `HasMet`; the rest gate on
    /// liking ranges.
    pub fn ltr_threshold_check(
        &self,
        _pair: RelationshipPair,
        liking: i32,
        bits: u32,
    ) -> LongTermRelationshipType {
        if bits & bits::ENEMIES != 0 {
            return LongTermRelationshipType::Enemy;
        }
        if bits & bits::HAS_MET == 0 {
            return LongTermRelationshipType::Stranger;
        }
        if (bits & bits::DATING != 0 || bits & bits::AFFAIR != 0) && liking >= 60 {
            return LongTermRelationshipType::RomanticInterest;
        }
        match liking {
            ..=49 => LongTermRelationshipType::Acquaintance,
            50..=79 => LongTermRelationshipType::Friend,
            80..=94 => LongTermRelationshipType::GoodFriend,
            _ => LongTermRelationshipType::BestFriend,
        }
    }
}

/// Card 26 §C golden-corpus hook (canary 9).
#[cfg(test)]
mod kdiff {
    use super::*;

    #[test]
    fn kdiff_canary_09() {
        let mut m = RelationshipMatrix::default();
        let pair = RelationshipPair::new(SimId(1), SimId(2));
        // HasMet (bit 0) at liking 10 -> Acquaintance; no HasMet -> Stranger.
        let acq = matches!(m.ltr_threshold_check(pair, 10, 1),
            LongTermRelationshipType::Acquaintance) as u64;
        println!("KDIFF|canary=9|case=ltr_acquaintance|got={:016x}", acq);
        assert_eq!(acq, 1);
        assert!(matches!(m.ltr_threshold_check(pair, 10, 0), LongTermRelationshipType::Stranger));
        // Symmetric pair + the sealed [-100, 100] clamp.
        m.set_from_mirror(RelationshipState {
            pair, liking: 100, stc_commodity: 0, stc_type: ShortTermContextType::Friendly,
            stc_perceived: SimId(2), ltr: LongTermRelationshipType::Acquaintance,
            interaction_bits: 1, ltr_start_ticks: 0, highest_liking: 100,
            when_last_greeted: 0, when_last_talked: 0,
        });
        let clamped = m.adjust_liking(SimId(2), SimId(1), 5).unwrap_or(0);
        println!("KDIFF|canary=9|case=liking_clamp|got={:016x}", clamped.max(0) as u64);
        assert_eq!(clamped, LIKING_MAX);
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn sim(n: u32) -> SimId {
        SimId(n)
    }

    #[test]
    fn motive_saturates_low_and_high() {
        let mut m = MotiveStore::default();
        let buff = BuffCommodityDecayModifier { commodity: CommodityKind::Hunger, delta_raw_per_tick: -1000 };
        for _ in 0..1000 {
            tick_motive_decay(&mut m, &[buff]);
        }
        assert_eq!(m.hunger.0, 0);
        let mut h = MotiveStore { hygiene: MotiveValue(MOTIVE_MAX * MOTIVE_FIXED_SCALE), ..Default::default() };
        let rec = BuffCommodityDecayModifier { commodity: CommodityKind::Hygiene, delta_raw_per_tick: 10 };
        for _ in 0..10_000 {
            tick_motive_decay(&mut h, &[rec]);
        }
        assert_eq!(h.hygiene.0, MOTIVE_MAX * MOTIVE_FIXED_SCALE);
    }

    #[test]
    fn buff_stack_order_clamps_after_sum() {
        let mut m = MotiveStore { fun: MotiveValue(50 * MOTIVE_FIXED_SCALE), ..Default::default() };
        let buffs = [
            BuffCommodityDecayModifier { commodity: CommodityKind::Fun, delta_raw_per_tick: -100 },
            BuffCommodityDecayModifier { commodity: CommodityKind::Fun, delta_raw_per_tick: -50 },
            BuffCommodityDecayModifier { commodity: CommodityKind::Fun, delta_raw_per_tick: 30 },
        ];
        tick_motive_decay(&mut m, &buffs);
        assert_eq!(m.fun.0, 50 * MOTIVE_FIXED_SCALE - 120);
    }

    #[test]
    fn buff_missing_commodity_contributes_zero() {
        let mut m = MotiveStore { bladder: MotiveValue(40 * MOTIVE_FIXED_SCALE), ..Default::default() };
        let buffs = [BuffCommodityDecayModifier { commodity: CommodityKind::Fun, delta_raw_per_tick: -100 }];
        tick_motive_decay(&mut m, &buffs);
        assert_eq!(m.bladder.0, 40 * MOTIVE_FIXED_SCALE);
    }

    #[test]
    fn pair_symmetric_key() {
        assert_eq!(RelationshipPair::new(sim(2), sim(1)), RelationshipPair::new(sim(1), sim(2)));
        let mut m = RelationshipMatrix::default();
        let st = RelationshipState {
            pair: RelationshipPair::new(sim(1), sim(2)),
            liking: 30,
            stc_commodity: 0,
            stc_type: ShortTermContextType::Friendly,
            stc_perceived: sim(1),
            ltr: LongTermRelationshipType::Friend,
            interaction_bits: bits::HAS_MET,
            ltr_start_ticks: 0,
            highest_liking: 30,
            when_last_greeted: 0,
            when_last_talked: 0,
        };
        m.set_from_mirror(st);
        assert_eq!(m.get(sim(2), sim(1)).unwrap().liking, 30);
        assert!(std::ptr::eq(m.get(sim(1), sim(2)).unwrap(), m.get(sim(2), sim(1)).unwrap()));
        assert_eq!(m.adjust_liking(sim(2), sim(1), i32::MAX), Some(100));
        assert_eq!(m.adjust_liking(sim(1), sim(2), i32::MIN), Some(-100));
        assert_eq!(m.get(sim(1), sim(2)).unwrap().ltr, LongTermRelationshipType::Friend);
    }

    #[test]
    fn motive_fractional_carry_and_extreme_buff_stack_are_bounded() {
        let mut m = MotiveStore { hunger: MotiveValue(10), ..Default::default() };
        m.fractional_delta_16[0] = -32_768;
        tick_motive_decay(&mut m, &[]);
        assert_eq!(m.hunger.0, 10);
        tick_motive_decay(&mut m, &[]);
        assert_eq!(m.hunger.0, 9);
        assert_eq!(m.fractional_carry_16[0], 0);
        let modifiers = [
            BuffCommodityDecayModifier { commodity: CommodityKind::Hunger, delta_raw_per_tick: i32::MAX },
            BuffCommodityDecayModifier { commodity: CommodityKind::Hunger, delta_raw_per_tick: i32::MAX },
            BuffCommodityDecayModifier { commodity: CommodityKind::Hunger, delta_raw_per_tick: i32::MIN },
        ];
        tick_motive_decay(&mut m, &modifiers);
        assert_eq!(m.hunger.0, 100 * MOTIVE_FIXED_SCALE);
        m.base_loss_raw_per_tick[2] = 1;
        m.bladder = MotiveValue(2);
        tick_motive_decay(&mut m, &[]);
        assert_eq!(m.bladder.0, 1);
    }

    #[test]
    fn ltr_stranger_to_acquaintance_needs_hasmet() {
        let m = RelationshipMatrix::default();
        let pair = RelationshipPair::new(sim(1), sim(2));
        assert_eq!(m.ltr_threshold_check(pair, 20, 0), LongTermRelationshipType::Stranger);
        assert_eq!(m.ltr_threshold_check(pair, 20, bits::HAS_MET), LongTermRelationshipType::Acquaintance);
    }

    #[test]
    fn ltr_enemy_bit_wins_over_liking() {
        let m = RelationshipMatrix::default();
        let pair = RelationshipPair::new(sim(1), sim(2));
        assert_eq!(m.ltr_threshold_check(pair, -50, bits::ENEMIES), LongTermRelationshipType::Enemy);
    }

    #[test]
    fn ltr_romantic_interest_needs_dating_bit() {
        let m = RelationshipMatrix::default();
        let pair = RelationshipPair::new(sim(1), sim(2));
        assert_ne!(m.ltr_threshold_check(pair, 80, bits::HAS_MET), LongTermRelationshipType::RomanticInterest);
        assert_eq!(m.ltr_threshold_check(pair, 80, bits::HAS_MET | bits::DATING), LongTermRelationshipType::RomanticInterest);
    }

    #[test]
    fn liking_clamps_at_bounds() {
        assert_eq!(99i32.saturating_add(5).clamp(LIKING_MIN, LIKING_MAX), LIKING_MAX);
        assert_eq!((-99i32).saturating_sub(5).clamp(LIKING_MIN, LIKING_MAX), LIKING_MIN);
    }
}
