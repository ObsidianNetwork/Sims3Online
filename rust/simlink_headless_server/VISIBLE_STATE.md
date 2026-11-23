# Visible NPC state publisher

The resident stream mirrors the existing native `PROBE_SIM_STATE` boundary for
unclaimed Sims on lots occupied by claimed Sims. It emits the existing public
`SimStateChanged` event; clients already apply that event through `APPLY_SIM_STATE`.
There is no new native verb or client gameplay execution.

Every fifth completed snapshot cadence schedules at most 48 sorted candidates,
in batches of at most eight unique nonzero object IDs. Reads use the same bounded,
nonblocking native request slot as snapshot and presentation IO. A probe gets one
turn, then live dumps can progress before another probe. It does not mutate the
recorder's target-selection context.

Native reports use case-insensitive bridge hex and strict UTF-8. Each sample must
contain exactly six finite motives in Hunger, Energy, Bladder, Social, Hygiene,
Fun order and a bounded exact set of buff GUID/origin pairs. Origins retain all
64 bits. Malformed mixed reports publish no partial sample. A sample changes when
any motive differs by more than 0.5 or the buff pair set differs.

Each pending read captures world and monotonic authority generations plus exact
client/Sim readiness leases. A Join blocks all state for its joining client;
a Fresh gather blocks only its explicit and expanded primary-object scope for
its counted clients. New candidates or unrelated clients retain existing jobs
and caches. Final history and held-live publication keep their affected leases
blocked through the actor's queue attempt. World/index and navigation cuts remain
global protections. Read-only probes share bounded turns with ongoing history
and live dumps without changing recorder target selection.

Prepared state has a private monotonic delivery token and one validated recipient.
The actor restricts that internal delivery to the recipient before converting it
to the unchanged three-field public `SimStateChanged`. The token never enters the
public protocol. After queueing, the original private receipt commits only that
client/Sim cache. A failed or skipped client retries unchanged state independently
of other clients. Equal public values cannot reuse an old token after history,
claim or connection ABA. Claimed Sims remain excluded.

A world replacement also invalidates the shared lot index and staged snapshot
cadence. Outstanding snapshot replies carry their request's world generation and
cannot restore an older index after the cut. No state candidate is eligible until
an authoritative new-world full snapshot replaces that index, even if ordinary
cadence would request a delta using the old world's larger tick. An active ordered
world cut completes before the new cadence starts; ordinary navigation epochs keep
their existing cadence behavior. The new-world bootstrap full is published at its own tick; later model changes are read in the next cadence starting at that published watermark, before those entities can become state candidates. A delta following an ordinary full read updates that full
index before its tick advances. These index guards are shared by
future state publishers rather than reconstructed separately for each feature.

Ordinary mixed full/delta cadence also publishes a coherent model cut. A forced
recipient receives complete Full records overridden by complete later Delta
records of the same primary ID, with the later tick; other recipients retain the
original Delta. Conflicting world/save identity, duplicate IDs or regressing
Full/Delta ticks are rejected rather than advancing a partial cut.

Initial model delivery remains owned by the existing native Join/baseline path;
the model ledger adds no startup full snapshot or changed cadence by itself.
Each recipient has a private model session captured before native snapshot IO.
Same-socket Leave/Join and world replacement invalidate it even if public IDs are
unchanged. Private receipts bind the session, cut generation, tick, force flag and
payload hash, and never enter public framing. Only successful queueing of a
current forced model restores that recipient's state readiness. A suppressed or
dropped model blocks its state leases until a later complete model queues; it
does not block other clients. Recovery preserves actual cadence suppression and
allows only one full request in flight. Rejected/skipped current full cuts restore
their exact previous resync marker so an unsent model cannot wait forever for an
impossible acknowledgement. Old receipts cannot clear a newer recovery cut.

`FRONT_DOOR_STATE` includes a `visible_state` row with `candidates`, `probes`,
`delivered`, `stale`, `errors`, `world_ready`, `world_generation` and
`discarded_snapshots`. `delivered` counts successful client/Sim queue deliveries,
not native application or rendered UI confirmation. Totals retain the original
`presentation_history_pending` boolean and add `presentation_join_history_pending`
(distinct queued, active or final-pump joining recipients) and
`presentation_fresh_history_pending` (active or final-pump Fresh gathers). Warm
transport readiness may wait for Join completion without requiring the entire
town to stop producing Fresh history; policy effect checks still require their
original whole-history quiescence gate.
The `model_state` report suffix contains recipient `unready`, receipt `pending`,
successfully `queued` and `rejected` counts; these are transport ordering facts,
not proof of native application.

The natural before-install symptom was a visible unclaimed NPC whose server Social
was 100 and both clients' Social was 0, with matching native identity. That evidence
is distinct from a rabbit-hole NPC whose loaded values already coincided. The
publisher's native/two-client correction gate remains open: the reviewed candidate
passed 345 clean-room tests and Clippy, compiled on Windows, and warm-attached with
all three native process identities preserved. Native probes and client deliveries
then advanced while completed history continued progressing. Both real receivers
logged the exact state payload, but an identity-matched native comparison still
showed unchanged client motives. Successful queue delivery is therefore not native
application proof. A previously missing buff expired on the server before the
after sample and supplies no correction credit. The shared native apply/setter
boundary is under investigation; no motive or buff correction PASS is claimed.
Claimed FullPuppet motives, claimed buffs, details,
presence, vehicle transforms and native animation phase fidelity remain separate
work. This server still requires the native TS3 simulator.

History readiness is a separate monotonic lease for each client/Sim pair. Starting
or expanding affected history invalidates that pair's pending read and delivered
cache even when all IDs later match again. The next eligible sample follows held
buff records and resends unchanged native state that older history could overwrite.
Unrelated established Sims continue syncing while other Fresh gathers progress.

Fresh history also derives a bounded primary-object placement hold set from its
completed Boundary pages before interleaving driver reads. This includes props
actually established by that baseline, but excludes incidental parent/reference
IDs. Their live placement and lifecycle records follow the buffered baseline.
This prevents a rewind; a long gather can still delay a moving parent. Continuous
vehicle delivery, audio/VFX baseline overlap and native phase continuity remain
separate measured gates.
