# Resident sync policy

Opt in with `--sync-policy FILE`. Without this flag the original packet path and
wire output remain unchanged. Supported behavior includes **per-player, per-record-family packet partitioning**
and the typed animation/audio payload aliases below. Recipients, authority,
movement and routing remain core-owned.

```text
simlink_headless_server --check-sync-policy examples/sync-policy.rhai
simlink_headless_server --pid N --serve --sync-policy /absolute/developer-policy.rhai
```

The checker validates the required function and its boolean output on typed
representative inputs, including full unsigned ID text. It exits before bind or
native attachment, returning JSON with `ok`, `sha256`, and `interface`; errors exit
nonzero with `ok=false`. Normal server construction also validates before invoking
the game-driver factory. Existing native-server bootstrap/claim requirements still
apply. Install once using the coordinated warm-restart tool, then edit only the
policy file; further script edits need no server/controller/game restart or build.

## Script interface

```rhai
fn selected(record) {
    record.player_id == "2" && record.family == "motion"
}
fn split_before(record) { selected(record) }
```

Replacing this with `fn split_before(record) { false }` removes the helper and
restores original grouping. Helpers are ordinary script functions; adding,
removing or changing them recompiles the script, not Rust. A true result starts a
new packet before the selected record, provided a nonempty packet precedes it.
No empty packet is emitted. `examples/` contains both runnable policies.

For another ordinary development edit, split audio for player1 and animation
requests for player2 by replacing the same file with:

```rhai
fn for_player(record, player, family) {
    record.player_id == player && record.family == family
}
fn split_before(record) {
    for_player(record, "1", "audio") ||
        (record.player_id == "2" && record.kind == "R")
}
```

Save the file by atomic replacement, then query `SYNC_POLICY_STATE` for the exact
active source SHA and actual per-player/family split counts. Removing a helper
means replacing its callers too; a stale call is rejected while the last good
rule keeps serving. A true rule changes grouping only when at least two records
share an eligible original event; sparse one-record events cannot split further.

The immutable record has `player_id`, `kind`, `family`, `joined_clients`, `tick`.
IDs are text, never converted through floating point or signed script integers.
The current public protocol still uses i32 player IDs; the typed script seam also
validates u64::MAX text without widening that protocol. Families are:

| Family | Recorder kinds |
| --- | --- |
| motion | Pz, Pf |
| animation | A, AS, S, P, Ps, Pn, Ph, E, R, X, AW |
| audio | Au |
| world | other valid recorder kinds |

**Snapshot context:** tick and joined_clients are from the last published worker
decision-table context, not an exact callback on each outgoing record. The worker
evaluates per-player/kind boolean decisions; one validated table is atomically
selected at a pump boundary for every recipient. Newly observed kinds/clients use
unchanged grouping until represented by a validated table. At most64 clients and
64 kinds are represented. The rule cannot inspect raw payloads or issue commands.

File reads, SHA hashing, compilation and evaluation stay on one bounded worker.
The actor uses nonblocking channels of capacity 1 and never waits for a script.
Source is capped at 65,536 UTF-8 bytes; each call has 5,000 operations, 16 call levels,
expression depth 32, 32 functions, 64 variables, strings 256, arrays 16 and maps 8.
There are at most 4,096 evaluations/table. No filesystem, network, clock, native,
module/import or authority API is registered in the raw Rhai engine. These are
developer resource limits, not an adversarial-code sandbox guarantee.

Bad syntax, missing function, wrong output and budget failures retain the current
validated code/table. Later context failure rolls back to the previous policy;
if that also fails, the actual validated builtin false policy is selected and its
SHA reported. A failed source is quarantined until file content changes. Edit by
atomic file replacement to avoid intentionally publishing intermediate editor
contents. Source read errors preserve the active policy and report an error SHA.

## Delivery invariants

Only eligible joined public recipients are processed. Admin/unjoined peers are
excluded. Targeted histories, protected gather/epoch pumps and synthetic sequence-zero
records bypass partitioning; typed aliases apply consistently to their payloads.
Full snapshots are unchanged. Every source record appears once in order; only
the explicitly supported value/name field may change;
tick/correlation are retained. Packet sequence allocation and replay-ring append
happen after splitting. Neither recipient selection nor command admission is
scriptable.

Packet input framing is bounded before partitioning: at most 200 records, existing
public frame limit, each raw line at most 16 KiB, valid UTF-8/hex, numeric source
sequence, bounded alphanumeric kind and no control characters. Malformed or
oversized events bypass unchanged and are counted. This validates safe packet
partitioning, not every recorder payload's engine semantics. Core/client validation
still owns those semantics. Output has at most 200 packets and preserves
source-record count and order (except for supported value/name replacements). Adding new engine/protocol abilities, new native hooks or a
different script seam requires reviewed core code and a build.

## Actual effects and proof

Loopback read-only `SYNC_POLICY_STATE` uses the standard admin JSON envelope with
a `report` string. Rows:

```text
policy|enabled=1|active_sha256=HEX|revision=N|tick=N|context_tick=N|activations=N|rejections=N|rollbacks=N|rejected_sha256=HEX|last_error_hex=HEX
totals|applied_events=N|input_lines=N|output_packets=N|splits=N|invalid_events=N|protected_events=N
player|2|applied_events=N|input_lines=N|output_packets=N|splits=N
effect|player=2|family=motion|input_lines=N|splits=N
```

Counts describe actual eligible packet processing and inserted boundaries, not
observer output. Source errors use the error text's SHA when content is unavailable.
The normal-serve TCP regression exercises helper addition/removal, player2-only
grouping, exact line order/bytes, contiguous EV sequences, targeted baseline bypass,
failed edits retaining delivery, late runtime rollback and a failing previous
policy falling back to the actual builtin. CLI, default-off and malformed/max-size
coverage are separate tests. Tests are run at an immutable commit with the rig
tool; offline tests do not establish native/live acceptance.

## Verified campaign (2026-11-13)

Production source `12814adc` built successfully on Windows with two jobs and a
separate release target. The actual executable's interface/output checker passed.
Immutable test tip `803d1759` passed all 302 Linux clean-room tests with default
features disabled; this excludes PC-only external repository fixtures. Strict
Clippy (`all = deny`) passed at `74baf975`, whose production source is identical.
These results do not claim PC repository integration or independent native ABI
acceptance from the Linux suite.

The normal warm restart installed the opt-in policy while preserving all three
native games and their creation times. The live verifier then added/removed a
helper, changed actual player2 packet grouping (splits 4 to 6 while player1 stayed
at zero), rejected syntax/output/budget errors with exact rejected source hashes,
and rolled a later context fault back to the previous policy. All six server,
controller and game identities and the executable hash stayed fixed during edits.
The initial helper policy was restored (SHA256
`8af6fab98f0c1e72efb2d9bb39e7857707bbdd385ca78a952826cfbe5e8f370d`).

The local editable file is `.local/sync-policy/live.rhai`; the retained proof is
`.omo/run/headless-n54/sync-policy/`. One stale client controller receipt was
recovered before installation onto its unchanged native game; the initial refused
attach and every failed offline fixture attempt remain retained. Mobile-phone
negative/positive/start-stop verification passed after installation. Separate native/gameplay evidence then passed: both-client short animation run
`20261113-214451`, 120-second steady watches on each client with zero client-only
motion events and zero sampled position error, real player1/player2 GoHere inputs
through Rust (13.548 m and 14.497 m; client error at most 0.01 m), and four clear
full-body captures. Replay failures remained zero on both clients, with no duplicate
drivers. All five original input hashes and all five PhoneFixed checkpoint hashes
matched their preserved baseline (both original input paths were checked).

The durable edit proof completed all stages and retained every process creation
snapshot, but its single final readiness sample caught a new fresh-history gather.
That raw FAIL remains unchanged. `completion.json` references those completed edit
stages and a separate real bounded final readiness/identity/health gate, which
passed: six unchanged processes, correct original policy SHA, all three games in
world, zero replay failures/invalid policy events. No further script edits or
restarts were needed for that final gate. Both live claims were released while
the verified intended runtime remained available.

Protected gathers deliberately bypass grouping. The verifier must wait for pending
history to finish both before edits and after restoration; allow a bounded longer
actual-effect wait when a fresh gather starts mid-test. This does not relax native
animation counters or permit scripts to alter baseline structure or identity. Two timeouts from
this campaign remain retained as harness timing evidence. Cumulative history cap
counts of one per client and one report-only join gap window were observed; zero
native replay failures does not establish complete historical/phase fidelity.
Gesture, thought-bubble and world-layer differences remain visible in the captures.
This packet-policy proof does not close those existing presentation limits or prove
simulation independent of native TS3.


## Typed animation and audio rules

An optional zero-argument `rules()` returns up to16 maps. The required
`split_before(record)` interface remains compatible. No `rules()` means no payload
edits. The worker evaluates helpers and validates the entire table before atomic
activation; no script runs per record on the actor. Rhai measures aggregate nested
output sizes: rule-table evaluation has a separate8192-byte aggregate string and
128-entry aggregate map limit (16 maps x8 keys). The grouping callback retains its
256-string/8-map limits; per-rule assets remain bounded to128 bytes.

```rhai
fn split_before(record) { false }
fn aliases(player) {
    [#{kind:"anim_param", player:player,
       machine_type:"7", machine_group:"8", machine_instance:"9",
       param:"10", from:"source_clip", to:"known_same_machine_clip"},
     #{kind:"audio_name", player:player, op:"o",
       from:"source_sound", to:"known_sound"}]
}
fn rules() { aliases("2") }
```

This schema example uses fictional IDs/assets; substitute observed engine-valid
values before live use. `examples/sync-payload.rhai` is a runnable empty scaffold,
so enabling it never assumes an installed game asset. Add/remove helpers or maps
by replacing the source file; no Rust rebuild is needed. Player is positive u64
text or `"*"`; resource type/group and param are canonical u32 text, instance is
u64 text. Type/instance/param must be nonzero. Values are1..128 ASCII bytes from
letters/digits and `_ - . : /`. Every value is text. Unknown keys, unsupported
operations, overlapping selectors (including wildcard/specific conflicts), wrong
return types and one invalid map reject the whole candidate. Native asset
existence and compatibility require actual engine evidence; syntax validation
cannot prove them.

`anim_param` matches the full type/group/instance from preceding `A` acquisition,
recipient, parameter hash and exact source value. It rewrites only `Ps` field4.
This is a string parameter/clip-selection seam, not a SACS state-hash rewrite.
Driver/actor/host identity, E/R states and bridge references remain unchanged.
Acquisition metadata spans packets, invalidates on reacquire/X/host Z/nav epoch,
and fails closed for unknown machines. Source matching is single-pass.

`audio_name` supports only object starts `Au o` (loop) and `Au w` (flags), changing
only the hex sound-name field. Object/op/handle/loop/flags and later property/stop
records remain intact. A positive start selects the current alias; a repeated
synthetic start for that lifetime retains its selected alias through edits until
object-scoped x/z terminates it. No synthetic restart is added. Unsupported array,
point, effect or other audio operations pass through. Existing presenter voice
ownership/duplicate-start behavior is outside this Rust rule extension.

A revision is deferred while any history/baseline/epoch transaction is pending,
including its final release pump. The same active aliases transform synthetic
history and held/live data. Navigation clears machine metadata while ongoing sound
lifetimes survive. Accepted LOAD_SAVE commits a new world generation. A crate-private control marker
uses the existing ordered history/epoch drain before clearing all mapping state;
it never reaches public routing/encoding and works with navigation disabled.
New-world driver events wait behind that cut (at most1024 events/4MiB). A capacity
failure stops the actor visibly before any partial held prefix can publish. Recovery
requires a corrected policy-aware controller restart and complete client baseline;
there is no automatic success/readiness claim after a failed cut.
A second LOAD_SAVE is refused until the actor acknowledges the preceding cut;
a stale completion cannot acknowledge a newer generation. Default policy-off
keeps original delivery order. A rejected load does not replace the world.

Metadata is bounded to64 recipients and1024 machine/sound bindings each. A malformed
whole event is forwarded exactly and invalidates uncertain associations; unknown
synthetic audio after that bypass is kept original until its lifetime is known.
A conservative full public-wire-envelope expansion check precedes transformation.
If the mapped output cannot fit, exact original bytes are forwarded and metadata
is re-observed from those actual originals, including original sound-name pins.
No partial rewritten event is published, and the actor keeps serving.

`SYNC_POLICY_STATE` additionally reports:

```text
policy|...|rules=N|pending_revision=N|pending_sha256=HEX|deferred=N
transforms|animation_values=N|audio_names=N|baseline_values=N|malformed_records=N|unknown_drivers=N|capacity_skips=N|output_fallbacks=N|uncertain_audio_skips=N
transform|player=2|animation_values=N|audio_names=N
last_transform|player=2|kind=Ps|source_seq=N|driver=N|param=N|from_hex=HEX|to_hex=HEX
last_transform|player=2|kind=Au|source_seq=N|object=N|handle=N|op=o|from_hex=HEX|to_hex=HEX
```

At most16 last-transform rows are retained. Counts represent actual changed fields,
including protected baseline values; unchanged matches do not increment them.
The TCP regressions cover a pending/final-baseline revision cut, cross-packet
acquisition and reused drivers, alias reload between audio start/property/stop,
invalid candidate retention, malformed mixed batches and near-limit output fallback.
Offline proof and native/live asset/effect acceptance remain separate gates.


### Payload extension offline evidence (2026-11-13)

Immutable source/test tip `b614383d` passed313 Linux clean-room tests with default
features disabled and strict Clippy. This excludes PC-only repository fixtures,
Windows ABI and native/live acceptance. Production actor socket cases passed for
both payload aliases, revision pinning through final history delivery, start/edit/
property/stop lifetime, stale cache invalidation, overflow fallback, default-off
exact output and ordered world cuts with/without Ne. A world-cut hold overflow
asserts a named actor failure with neither client's held prefix published. First
attempts exposed an external reused-actor test visibility issue and Rhai's aggregate
nested output budgets; both failures remain retained in the campaign artifacts.
Native asset compatibility and rewritten actual playback still require live proof.


The first native campaign preserved all three native games through a warm install
and delivered transformed positive Ps values to both real clients. Its audio gate
failed: nested native recorder names were uppercase BridgeLineCodec hex, while the
initial mapper used the public scalar decoder's lowercase-only contract. The failed
receipt, original-policy restoration and probe-owned call stop remain retained;
that campaign is not a successful combined transform acceptance.

Correction `ea76d216` decodes recorder name bytes using the existing case-insensitive
bridge decoder and then strict UTF-8, without widening the public wire decoder.
Malformed starts invalidate old alias metadata before exact original forwarding.
It passed315 immutable clean-room tests; new actual-serve cases cover upper/mixedcase
o/w starts, flags, revision pinning, property/stop, odd/nonhex/invalid UTF-8 and
fresh-lifetime recovery. The corrected Windows release compiled with two jobs.
Its corrected live gate passed without changing any of the six process identities:
positive Ps rewrites were the latest received parameter writes and both clients'
valid same-machine native bindings played the replacement clip. A source sound
handle mapped on both clients, retained those mappings across rule removal, and
an explicit server stop cleared both. Candidate rejection and original-source
restoration passed. This proves those selected native assets and lifecycle paths;
it does not establish general phase fidelity or recorded speaker output.
Native clip proof requires the rewritten
Ps to be the latest received write for its driver/parameter after lifecycle resets,
not a superseded historical transform followed by a natural matching clip.

Subsequent vehicle travel exposed a separate presentation gap: native non-Sim
vehicle movement has no continuous transform stream in this branch. An aligned
two-client watch reproduced the gap. Fresh-target history must also retain that
target's placement/posture/lifecycle records until its boundary baseline is sent,
so an old captured transform cannot follow newer live placement. Established
targets continue streaming during independent SACS history pages. Legacy boundary
paging still owns the native target-selection context and cannot safely interleave
dumps until those pages finish. Broader gameplay acceptance remains blocked on
these separately tracked boundaries.

When supported by the native shell, boundary history requests now append a snapshot
token and explicit target scope to `snapshot-driver` for the boundary handle. Every
page validates the echoed token and stable cursor. Legacy replies without that
metadata retain their previous paging behavior. Boundary cursors never establish
SACS coverage or discard global live records. All boundary pages remain protected
from dump interleaving: the snapshot can include props beyond the fresh Sim IDs,
so freezing pages alone does not establish a safe complete live hold scope. An
incomplete tokenized history is rejected rather than published as a partial baseline.
