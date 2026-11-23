#![forbid(unsafe_code)]

use std::collections::BTreeMap;
use crate::SimId;
use crate::authority::ServerHouseholdAuthority;
use crate::wire::PlayerId;

pub const DIALOG_TIMEOUT_TICKS: u64 = 1_800;
pub const SPEED_REAPPLY_WINDOW_TICKS: u64 = 15;
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum DialogKind { SimpleMessage, StringInput, TwoButton, AcceptCancel, ThreeButton, ObjectPicker, Other(u32) }
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum DialogAnswer { Default, Selected(u32), Text(String), PickedObject(u64), None }
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct DialogRequest {
    pub corr: u64, pub id: u64, pub sim: SimId, pub owner_player: PlayerId,
    pub kind: DialogKind, pub opened_at_tick: u64,
}
#[derive(Default)]
pub struct RemoteModalDialogRouter {
    pub outbox: Vec<DialogRequest>,
    pub pending: BTreeMap<u64, DialogRequest>,
    pub answers: BTreeMap<u64, DialogAnswer>,
    pub timed_out: u64, pub suppressed: u64, pub remoted: u64, pub answered: u64,
    pub server_paused_by_dialog: bool,
    last_request: BTreeMap<PlayerId, u64>,
    last_answer: BTreeMap<PlayerId, u64>,
}
impl RemoteModalDialogRouter {
    pub fn request_for_claimed_sim(&mut self, r: DialogRequest, a: &ServerHouseholdAuthority) {
        if !a.is_selectable(r.sim) { self.suppressed += 1; return; }
        if self.last_request.get(&r.owner_player).is_some_and(|corr| r.corr <= *corr)
            || self.pending.contains_key(&r.id) || self.answers.contains_key(&r.id) { return; }
        self.last_request.insert(r.owner_player, r.corr);
        self.pending.insert(r.id, r);
        self.outbox.push(r);
        self.remoted += 1;
    }
    pub fn route_to_owner(&self, r: &DialogRequest) -> PlayerId { r.owner_player }
    /// Trusted internal delivery; public commands use answer_from_player.
    pub fn deliver_answer(&mut self, id: u64, answer: DialogAnswer) {
        if self.pending.remove(&id).is_some() {
            self.answers.insert(id, answer);
            self.answered += 1;
        }
    }
    pub fn answer_from_player(&mut self, player: PlayerId, corr: u64, id: u64, answer: DialogAnswer) -> bool {
        if self.last_answer.get(&player).is_some_and(|last| corr <= *last) { return true; }
        if !self.pending.get(&id).is_some_and(|r| r.owner_player == player) { return false; }
        self.last_answer.insert(player, corr);
        self.deliver_answer(id, answer);
        true
    }
    pub fn tick(&mut self, now: u64) -> Vec<u64> {
        let due: Vec<_> = self.pending.values()
            .filter(|r| now.saturating_sub(r.opened_at_tick) >= DIALOG_TIMEOUT_TICKS)
            .map(|r| r.id).collect();
        for id in &due {
            self.pending.remove(id);
            self.answers.insert(*id, DialogAnswer::Default);
            self.timed_out += 1;
        }
        due
    }
    pub fn invariant_clock_not_paused(&self) -> bool { !self.server_paused_by_dialog }
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn owner_gated_idempotent_answers_and_exact_timeout() {
        let mut a = ServerHouseholdAuthority::default();
        a.claimed_sims.insert(SimId(1));
        let mut router = RemoteModalDialogRouter::default();
        let r = DialogRequest { id: 1, corr: 1, sim: SimId(1), owner_player: PlayerId(1),
            kind: DialogKind::TwoButton, opened_at_tick: 1000 };
        router.request_for_claimed_sim(r, &a);
        router.request_for_claimed_sim(r, &a);
        assert_eq!(router.remoted, 1);
        assert!(!router.answer_from_player(PlayerId(2), 1, 1, DialogAnswer::Selected(1)));
        assert!(router.tick(2799).is_empty());
        assert_eq!(router.tick(2800), vec![1]);
        assert_eq!(router.answers[&1], DialogAnswer::Default);
        assert!(router.tick(2801).is_empty());
        router.request_for_claimed_sim(DialogRequest { id: 2, corr: 2, ..r }, &a);
        assert!(router.answer_from_player(PlayerId(1), 2, 2, DialogAnswer::Text("yes".into())));
        assert!(router.answer_from_player(PlayerId(1), 2, 2, DialogAnswer::None));
        assert_eq!(router.answered, 1);
        assert!(router.invariant_clock_not_paused());
    }
}
