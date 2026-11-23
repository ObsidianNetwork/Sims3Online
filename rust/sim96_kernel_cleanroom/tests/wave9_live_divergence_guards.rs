#![forbid(unsafe_code)]
//! Card 42/43 live-divergence guards: the four exact failure patterns from
//! `docs/engine/sim96_live_divergence_forensics.md`, modelled and locked as
//! kernel tests. Each guard asserts the contract the live TS3 run violated:
//! a rejection must be NAMED and SEEN on the wire, and state the server never
//! entered must not move. No runtime_bridge helper was needed - the kernel
//! already answers every one of these paths; what was missing were the locks.
use std::io::{BufRead, BufReader, Write};
use std::net::{Shutdown, TcpStream};
use std::thread;
use std::time::Duration;

use sim96_kernel_cleanroom::ObjectGuid;
use sim96_kernel_cleanroom::kernel_iface::{SlotIndex, SlotReservationStore};
use sim96_kernel_cleanroom::runtime_bridge::{AuthorityDaemon, AuthorityWorld};
use sim96_kernel_cleanroom::social_state::{
    BuffCommodityDecayModifier, CommodityKind, LongTermRelationshipType, RelationshipMatrix,
    RelationshipPair, RelationshipState, ShortTermContextType, MotiveStore, tick_motive_decay,
};
use sim96_kernel_cleanroom::wan_transport::RoleTag;
use sim96_kernel_cleanroom::wire::PlayerId;
use sim96_kernel_cleanroom::{KernelError, SimId};
use sim96_kernel_cleanroom::social_state::MotiveValue;

fn open(addr: std::net::SocketAddr) -> (TcpStream, BufReader<TcpStream>) {
    let stream = TcpStream::connect(addr).unwrap();
    stream.set_nodelay(true).unwrap();
    stream.set_read_timeout(Some(Duration::from_secs(10))).unwrap();
    let reader = BufReader::new(stream.try_clone().unwrap());
    (stream, reader)
}

fn exchange(stream: &mut TcpStream, reader: &mut BufReader<TcpStream>,
    lines: &str, fence: u64) -> Vec<String> {
    writeln!(stream, "{lines}PING|{fence}").unwrap();
    stream.flush().unwrap();
    let end = format!("PONG|{fence}");
    let mut batch = Vec::new();
    loop {
        let mut line = String::new();
        assert_ne!(reader.read_line(&mut line).unwrap(), 0, "daemon closed before {end}");
        let line = line.trim_end().to_owned();
        if line == end { break; }
        batch.push(line);
    }
    batch
}

fn named_answer_for(batch: &[String], corr: u64) -> usize {
    // A visible answer for a correlation id: accept, refusal, or the corr-carrying
    // authoritative delta a success reply actually is (`Hd|corr|...`).
    batch.iter()
        .filter(|s| s.starts_with(&format!("ACK|{corr}|")) || s.starts_with(&format!("ERR|{corr}|"))
            || s.starts_with(&format!("Hd|{corr}|")))
        .count()
}

/// Pattern 1 (forensics §4, card 33 §C): the probe's exact shape - a socket
/// that performs no handshake and sends `ClaimSim|77|4242` - MUST get a named
/// rejection, and every fresh correlation id on a joined session must receive
/// exactly one visible answer. A silent drop is a defect even when refusing is
/// correct; that is what the live controller lost and what this lock keeps.
///
/// Also pattern 2's wire half: a puppet's own `RouteAway` autonomy insert is
/// refused by name while the server's authoritative claim stands, and both
/// presenter streams keep reading the identical mirrored deltas and STATE hash.
#[test]
fn claim_and_autonomy_always_answer_by_name_on_the_wire() {
    let mut daemon = AuthorityDaemon::bind(([127, 0, 0, 1], 0).into()).unwrap();
    daemon.ticks = 900;
    daemon.max_connections = 3;
    let addr = daemon.local_addr().unwrap();
    let server = thread::spawn(move || daemon.serve_world(AuthorityWorld::new()));

    // (a) The forensics probe verbatim: no HELLO, claim sim 77 household 4242.
    let (mut raw, mut rawr) = open(addr);
    let dropped = exchange(&mut raw, &mut rawr, "CMD|77|ClaimSim|77|4242\n", 78);
    assert!(dropped.iter().any(|s| s == "ERR|0|bad_line|say_hello_first"),
        "unjoined claim must answer named, never silently: {dropped:?}");
    assert_eq!(dropped.iter().filter(|s| s.starts_with("ERR|77|")).count(), 0,
        "no claim-scoped reply may leak to an unjoined socket, but the fence still closed");

    // (b) Joined players: every fresh corr gets exactly one visible answer.
    let (mut b, mut br) = open(addr);
    exchange(&mut b, &mut br, "HELLO|1|1|1|17|15|client\n", 2);
    let own = exchange(&mut b, &mut br,
        "CMD|3|ClaimSim|5|100\nCMD|30|ClaimSim|6|100\n", 4);
    assert!(own.iter().any(|s| s.starts_with("ACK|3|SimClaimed|5|100")), "{own:?}");
    assert_eq!(named_answer_for(&own, 3), 1, "every fresh corr answers exactly once");
    assert_eq!(named_answer_for(&own, 30), 1);

    let (mut c, mut cr) = open(addr);
    exchange(&mut c, &mut cr, "HELLO|5|1|1|17|15|client2\n", 6);
    let steal = exchange(&mut c, &mut cr, "CMD|7|ClaimSim|5|100\n", 8);
    assert!(steal.iter().any(|s| s == "ERR|7|UnauthorizedClientInsertion|ClaimSim"),
        "claiming an already-owned Sim must be refused by name: {steal:?}");
    assert_eq!(named_answer_for(&steal, 7), 1, "the refused corr was not silently dropped");
    let foreign_funds = exchange(&mut c, &mut cr, "CMD|9|ModifyFunds|100|-25|coffee\n", 10);
    assert!(foreign_funds.iter().any(|s| s.starts_with("ERR|9|NotOwnerOfLot")),
        "a player without a household must be refused by name: {foreign_funds:?}");

    // (c) Pattern 2 wire half: the puppet's own autonomy insert is refused...
    // (corr 31: b's codec high-water is 30 after its claims - fresh ids must
    // strictly increase per player or the kernel drops them as stale, by design)
    let rogue = exchange(&mut b, &mut br, "CMD|31|EnqueueInteraction|5|RouteAway|client|0|100\n", 32);
    assert!(rogue.iter().any(|s| s == "ERR|31|UnauthorizedClientInsertion|RouteAway"),
        "puppet autonomy must be refused by name: {rogue:?}");
    // ...and the server-side interaction it conflicts with stands authoritative.
    let held = exchange(&mut b, &mut br, "CMD|33|EnqueueInteraction|5|SocializeWith|server|6|100\n", 34);
    assert!(held.iter().any(|s| s.starts_with("ACK|33|InteractionQueued")),
        "the server's own social insert stands after the refusal: {held:?}");

    // Both presenter streams keep mirroring the same bytes and the same hash.
    let funds = exchange(&mut b, &mut br, "CMD|35|ModifyFunds|100|-10|purchase\n", 36);
    assert_eq!(named_answer_for(&funds, 35), 1, "the mirrored delta's corr answers once");
    let hd = funds.iter().find(|s| s.starts_with("Hd|35|100|-10|")).cloned();
    assert!(hd.is_some(), "owning player's funds delta: {funds:?}");
    let mirror = exchange(&mut c, &mut cr, "", 37);
    assert!(mirror.iter().any(|s| Some(s) == hd.as_ref()),
        "the mirrored Hd delta reaches the other stream byte-identically: {mirror:?}");
    let sa = exchange(&mut b, &mut br, "STATE|38\n", 39);
    let sb = exchange(&mut c, &mut cr, "STATE|40\n", 41);
    let hash = |v: &[String]| v.iter().find(|s| s.starts_with("STATE|")).map(|s| s.split('|').nth(2).unwrap_or("").to_owned());
    assert_eq!(hash(&sa), hash(&sb), "presenter streams must read one authoritative hash");

    let _ = raw.shutdown(Shutdown::Both);
    let _ = b.shutdown(Shutdown::Both);
    let _ = c.shutdown(Shutdown::Both);
    let (report, world) = server.join().unwrap().unwrap();
    assert_eq!(report.dropped_ticks_during_join, 0);
    // The refusal of the puppet's autonomy left the kernel's authority exactly
    // where the server put it: one claimed Sim (with the joined presenter -
    // the never-greeted probe socket still consumed player id 1), one queue.
    assert_eq!(world.authority.owners.get(&SimId(5)), Some(&PlayerId(2)));
    let socials: Vec<_> = world.queue.entries().iter()
        .filter(|e| e.family.is_social()).collect();
    assert_eq!(socials.len(), 1, "server interaction preserved across the rejected insert");
    assert_eq!(socials[0].instance_actor, SimId(5));
}

/// Pattern 2 (bucket 1, 7 distinct issues): server holds `SocialInteractionB`
/// (a `SocializeWith`) on the cast; the puppet attempts local autonomy
/// `WashDishes` / `CleanUp` (unknown families) and `RouteAway` / `BedSleep`
/// (autonomy-only families). Every attempt is refused by name at the queue
/// gate, and the server's authoritative interaction, position and facing
/// stream provably never moved: queue contents, tick, RNG, travel segments,
/// last position and both state hashes are byte-identical across the barrage.
#[test]
fn puppet_local_autonomy_never_moves_the_authoritative_world() {
    let mut w = AuthorityWorld::new();
    let mut out = Vec::new();
    w.handle_line(PlayerId(1), RoleTag::Client, "HELLO|1|1|1|17|15|client", &mut out);
    w.handle_line(PlayerId(2), RoleTag::Client2, "HELLO|2|1|1|17|15|client2", &mut out);
    w.handle_line(PlayerId(1), RoleTag::Client, "CMD|3|ClaimSim|1|100", &mut out);
    w.handle_line(PlayerId(2), RoleTag::Client2, "CMD|4|ClaimSim|2|200", &mut out);
    // The server (the controller role, never a client) holds the social.
    w.handle_line(PlayerId(0), RoleTag::Server,
        "CMD|5|EnqueueInteraction|2|SocializeWith|server|1|200", &mut out);
    assert!(out.iter().any(|s| s.starts_with("ACK|5|InteractionQueued")), "{out:?}");
    for _ in 0..40 { w.tick(); }

    let before = (w.command_state_hash(), w.rng_state,
        w.clock.native_tick_cnt, w.segments_done, w.queue.len(),
        w.queue.entries().first().map(|e| (e.id, e.instance_actor, e.target_sim)),
        w.travel.as_ref().map(|v| format!("{:?}", v.last_pos)));

    // The puppet's rogue local-autonomy barrage, in the live names exactly.
    let attempts = [
        "CMD|20|EnqueueInteraction|1|WashDishes|client|0|100",   // unknown family -> named bad_line
        "CMD|21|EnqueueInteraction|1|CleanUp|client|0|100",      // unknown family -> named bad_line
        "CMD|22|EnqueueInteraction|1|RouteAway|client|0|100",    // autonomy-only ->
        "CMD|23|EnqueueInteraction|1|BedSleep|client|0|100",     // autonomy-only ->
        "CMD|24|SpawnService|Maid",                              // world service ->
    ];
    for line in attempts {
        out.clear();
        w.handle_line(PlayerId(1), RoleTag::Client, line, &mut out);
        assert_eq!(out.len(), 1, "every attempt answers exactly one named frame: {out:?}");
        assert!(out[0].starts_with("ERR|"), "must be a refusal, not an accept: {out:?}");
    }
    assert!(out[0].contains("ClientWorldServiceSuppressed"));
    out.clear();
    w.handle_line(PlayerId(1), RoleTag::Client, attempts[2], &mut out);
    assert_eq!(out[0], "ERR|22|UnauthorizedClientInsertion|RouteAway");
    out.clear();
    w.handle_line(PlayerId(1), RoleTag::Client, attempts[0], &mut out);
    assert_eq!(out[0], "ERR|20|bad_line|interact",
        "an unknown family is still refused by name, never silently");

    let after = (w.command_state_hash(), w.rng_state,
        w.clock.native_tick_cnt, w.segments_done, w.queue.len(),
        w.queue.entries().first().map(|e| (e.id, e.instance_actor, e.target_sim)),
        w.travel.as_ref().map(|v| format!("{:?}", v.last_pos)));
    assert_eq!(before, after,
        "the server's authoritative command state (queue/slots/funds/handles/edits), position and facing stream never moved (bucket-1 law, card 08 probe)");
    // The queue head is still the server's SocialInteractionB stand-in.
    let head = w.queue.entries().first().expect("server interaction still queued");
    assert_eq!((head.instance_actor, head.target_sim), (SimId(2), Some(SimId(1))));
}

/// Pattern 3 (bucket 1 of the props, 8 distinct issues): held/parented prop
/// lifecycle. Spawn a prop handle (the `FoodProp`-style auto-object of a
/// claim), parent it into an object slot, then run the server-side teardown
/// (claim-completion swap releases the reservations; `RetireHandle` retires
/// the generation). After teardown, stale derefs must answer the named
/// `StaleObjectHandle`, the slot must report no holder (puppets never keep an
/// orphan when the server says `(none)`), and a second parent attempt must be
/// refused while the slot is still held.
#[test]
fn prop_lifecycle_never_leaves_orphan_handles_or_slots() {
    let mut w = AuthorityWorld::new();
    let mut out = Vec::new();
    w.handle_line(PlayerId(1), RoleTag::Client, "HELLO|1|1|1|17|15|client", &mut out);
    w.handle_line(PlayerId(2), RoleTag::Client2, "HELLO|2|1|1|17|15|client2", &mut out);
    w.handle_line(PlayerId(1), RoleTag::Client, "CMD|3|ClaimSim|1|100", &mut out);
    // The claim's server-spawned prop: FoodProp/PhoneTable stand-in guid 9001
    // with its `Od|` presentation delta broadcast.
    assert!(out.iter().any(|s| s.starts_with("Od|9001|")), "prop spawn delta: {out:?}");
    let prop = ObjectGuid(9_001);
    let key = *w.handle_keys.last().expect("prop handle issued");

    // Parent it: `ChairDiningPatioPlastic`-style slot attach on the prop.
    out.clear();
    w.handle_line(PlayerId(2), RoleTag::Client2, "CMD|31|ClaimSim|2|200", &mut out);
    assert!(out.iter().any(|s| s.starts_with("ACK|31|SimClaimed")), "{out:?}");
    out.clear();
    w.handle_line(PlayerId(1), RoleTag::Client, "CMD|4|RouteSlotClaim|1|9001|2|0", &mut out);
    assert!(out[0].starts_with("ACK|4|RouteSlotClaim"), "{out:?}");
    assert_eq!(w.slots.holder(prop, SlotIndex(2)), Some(SimId(1)));
    // A second Sim parenting the same slot: named clash, not a silent no-op.
    out.clear();
    w.handle_line(PlayerId(2), RoleTag::Client2, "CMD|32|RouteSlotClaim|2|9001|2|0", &mut out);
    assert!(out[0].contains("SlotAlreadyReserved"), "{out:?}");

    // Deref while live works (the puppet may read a held prop the server holds).
    out.clear();
    w.handle_line(PlayerId(1), RoleTag::Client,
        &format!("CMD|6|DerefHandle|{}|{}", key.index, key.generation), &mut out);
    assert!(out[0].starts_with("ACK|6|Handle|9001"), "{out:?}");

    // Server-side destroy: `RetireHandle` is a server-role verb (the client
    // gate names the refusal), retiring the generation deterministically.
    out.clear();
    w.handle_line(PlayerId(0), RoleTag::Server,
        &format!("CMD|7|RetireHandle|{}|{}", key.index, key.generation), &mut out);
    assert!(out[0].starts_with("ACK|7|Retired"), "{out:?}");
    // A client-role retire attempt is refused by name at the authority gate,
    // never silently - puppets cannot destroy server objects.
    out.clear();
    w.handle_line(PlayerId(2), RoleTag::Client2,
        &format!("CMD|42|RetireHandle|{}|{}", key.index, key.generation), &mut out);
    assert_eq!(out[0], "ERR|42|UnauthorizedClientInsertion|RetireHandle", "{out:?}");

    // Server-side completion: the claim swap unparents (release at the seam).
    out.clear();
    w.handle_line(PlayerId(1), RoleTag::Client, "CMD|8|SwapClaim|1|9", &mut out);
    assert!(out[0].starts_with("ACK|8|SwapClaim"), "{out:?}");
    assert_eq!(w.slots.holder(prop, SlotIndex(2)), None,
        "no orphaned slot parentage survives the server teardown");

    // After destroy, stale access is NAMED forever (generation semantics):
    // deterministic cleanup frames, never a silence a puppet could fill in.
    for corr in 9..12 {
        out.clear();
        w.handle_line(PlayerId(1), RoleTag::Client,
            &format!("CMD|{corr}|DerefHandle|{}|{}", key.index, key.generation), &mut out);
        assert_eq!(out[0], format!("ERR|{corr}|StaleObjectHandle"),
            "retired prop handle must answer the named stale error");
    }
    // The arena API says the same thing directly.
    assert_eq!(w.handles.get(key), Err(KernelError::StaleObjectHandle));
}

/// Pattern 4 (bucket 3, 3 distinct issues): the `Disgusted` moodlet. Buff and
/// motive state are authoritative on the server: presenters mirror the SAME
/// transmitted (buff, tick-delta) sequence and must land on identical state -
/// same values, same expiry-side behaviour, order-independent stacking - while
/// any presenter that computes for itself (a local buff) diverges, which is
/// exactly the bug class the fork must suppress at the choke point.
#[test]
fn moodlet_mirrors_converge_and_local_computation_diverges() {
    // Server: Jennifer's hygiene motive under the server-owned `Disgusted` buff.
    let base = MotiveStore {
        hunger: MotiveValue(500_000), energy: MotiveValue(500_000),
        bladder: MotiveValue(500_000), hygiene: MotiveValue(500_000),
        social: MotiveValue(500_000), fun: MotiveValue(500_000),
        room: MotiveValue(500_000),
        base_loss_raw_per_tick: [10; 7],
        ..Default::default()
    };
    let disgusted = [
        BuffCommodityDecayModifier { commodity: CommodityKind::Hygiene, delta_raw_per_tick: -60 },
        BuffCommodityDecayModifier { commodity: CommodityKind::Social, delta_raw_per_tick: -40 },
    ];
    let mut server = base;
    let base_hygiene = base.hygiene.0;
    let base_social = base.social.0;
    let mut presenter_a = base;
    let mut presenter_b = base;
    for _ in 0..200 {
        tick_motive_decay(&mut server, &disgusted);
        // The presenters never invent buffs: they replay the transmitted list.
        tick_motive_decay(&mut presenter_a, &disgusted);
        // Stacked in a different transmission order: card 09 sums per
        // commodity before clamping, so order must not matter.
        let flipped = [disgusted[1], disgusted[0]];
        tick_motive_decay(&mut presenter_b, &flipped);
    }
    assert_eq!(presenter_a.hygiene, presenter_b.hygiene);
    assert_eq!(presenter_a.social, presenter_b.social);
    assert_eq!((presenter_a.hunger, presenter_a.energy, presenter_a.room),
        (presenter_b.hunger, presenter_b.energy, presenter_b.room));
    assert_eq!(presenter_a.hygiene, server.hygiene,
        "mirrors of the authoritative stream agree with the server exactly");
    assert!(presenter_a.hygiene.0 < base_hygiene
        && presenter_a.social.0 < base_social,
        "Disgusted degrades the mirrored motives, it does not inflate them");
    assert_eq!(presenter_a.hygiene, presenter_b.hygiene);

    // A presenter that applies a LOCAL moodlet the server never sent diverges:
    // this is the `Disgusted`-at-10.00-with-server-(none) failure, and the
    // kernel contract says the fork must suppress it, never mirror it away.
    let mut rogue = base;
    let local_extra = [disgusted[0], BuffCommodityDecayModifier {
        commodity: CommodityKind::Hygiene, delta_raw_per_tick: -100 }];
    for _ in 0..200 {
        tick_motive_decay(&mut rogue, &local_extra);
    }
    assert_ne!(rogue.hygiene, server.hygiene,
        "local computation must diverge - that is the whole point of the mirror");
    // Clamp floor is shared: deep expiry cannot underflow asymmetrically.
    let mut deep = server;
    for _ in 0..100_000 {
        tick_motive_decay(&mut deep, &disgusted);
        tick_motive_decay(&mut presenter_a, &disgusted);
    }
    assert_eq!(deep.hygiene, presenter_a.hygiene, "clamped floors must match too");

    // The relationship side of the same mirror law: both directions of one
    // transmitted pair normalize to the identical canonical row.
    let state = RelationshipState {
        pair: RelationshipPair::new(SimId(2), SimId(1)), liking: 10, stc_commodity: 0,
        stc_type: ShortTermContextType::Friendly, stc_perceived: SimId(1),
        ltr: LongTermRelationshipType::Acquaintance, interaction_bits: 1,
        ltr_start_ticks: 5, highest_liking: 10, when_last_greeted: 5, when_last_talked: 5,
    };
    let mut m1 = RelationshipMatrix::default();
    let mut m2 = RelationshipMatrix::default();
    m1.set_from_mirror(state);
    m2.set_from_mirror(RelationshipState { pair: RelationshipPair::new(SimId(1), SimId(2)), ..state });
    assert_eq!(m1.get(SimId(1), SimId(2)).map(|r| r.liking), m2.get(SimId(1), SimId(2)).map(|r| r.liking));
    assert_eq!(
        m1.get(SimId(1), SimId(2)).map(|r| (r.pair, r.ltr, r.interaction_bits, r.highest_liking)),
        m2.get(SimId(2), SimId(1)).map(|r| (r.pair, r.ltr, r.interaction_bits, r.highest_liking)),
        "canonical (min,max) rows must agree for both presenters");
}
