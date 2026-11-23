# Claimed participant initialization

A model row and a Sim alias do not instantiate a client Sim. A participant that
was not instantiated in the client's loaded save therefore needs the existing
description and spawn messages before its presentation baseline. This path is
separate from a future ordinary NPC presence journal. It never infers a despawn
from a delta omission or removes an actor when a claim is released.

The native bridge explicitly advertises this capability. Deterministic drivers
leave it unsupported unless their fixture implements it; a native failure never
turns the capability off. There is no new public command or native verb.

## Initialization and receipts

For each joined recipient and current claimed object, the stream requires an
actual native snapshot row with a nonzero description and finite position.
Existing generation-checked Full/Delta reads can supply that witness. If none is
available, one asynchronous internal full snapshot uses the ordinary shared IO
slot. That witness-only read does not publish a public FullSnapshot or advance
the stream's model index, cadence watermark or delivery receipts.

The stream captures world, authority, recipient session and exact object/person
identity before exporting the existing description payload. It then prepares
two private, recipient-specific delivery tokens. The actor validates and strips
them before queuing ordinary `SimDescriptionData`, then `SimSpawned`. Only the
successful intended recipient advances; a failed first queue cannot authorize
the second. Affected history waits for those receipts, while unrelated live
presentation continues. Initial Join audiences are materialized before incoming
pages even between ordinary presentation deadlines; history retry backoff stays
unchanged.

These are **queue receipts, not native creation acknowledgements**. The client
must also order description/spawn dispatch ahead of later ring animations. Its
shared lifecycle barrier drains preceding held animations into the ordered
bridge queue and routes subsequent animations through that queue until the
control dispatch completes. A client with only the old immediate ring fast path
does not satisfy this ordering contract. Native spawn/import errors remain
errors; final acceptance must verify the actual object, description and visible
presentation on every client.

Pending reads and prepared events cannot survive world, authority or recipient
session changes. Successful creation records survive an unrelated authority
change for the same world/session/object/person. Claim removal retires the
obligation without despawning. A reported replacement description for an object
invalidates its creation record and owes fresh history; an absent delta row
does neither.

## Public byte arrays

Both proactive initialization and an explicit `RequestSimDescription` use the
same public raw-byte hexadecimal helper internally. Streaming sends each of the
three byte-array fields as a raw `BULK`, with the introducing event first and
increasing bulk IDs. An empty table still has one empty chunk. These are binary
bytes, including non-UTF-8 values, and never a decimal list or UTF-8 text. Always
using raw bulks also avoids interpreting inline hexadecimal `b123` as a bulk ID.
The replay ring marks the introducing event as bulk-bearing, so a resume cannot
replay a stranded header without its binary data.

`examples/description_wire_corpus.rs` emits four bounded synthetic cases through
the actual production encoder, followed by `SimSpawned`: non-UTF-8 bytes, empty
tables, the 512 KiB export bound and a byte sequence resembling a bulk reference.
Its JSON `cases[].lines` strings exclude line terminators. A consumer writes one
terminator per string. This is first-party protocol test data, not a native game
fixture or a replacement for live import and creation validation. The real C#
`PublicLineCodecV1.EventReceiver` and client `PublicWireConnection` must consume
the generated corpus as a separate interoperability gate.

## Bounds and recovery

Server-side history capture can run before client initialization completes;
publication cannot. Select and all Boundary pages retain their protected
LastTargets ownership. Once selection classifies the target drivers, unrelated
established animation records continue while export retries. Typed live acquire
and actor-binding records also identify fresh drivers created after selection.
Their handles stay held through the whole gather, including release/reacquire,
so a later incarnation cannot overtake older buffered records. This metadata is
bounded to 4,096 handles per gather and fails the actor visibly on overflow.
Legacy histories without a cursor also wait for initialization queue receipts.

Description replies use the existing native `AUTONOMY_PROBE` metadata plus the
owned bulk slot. Both synchronous and asynchronous reads validate one exact
result/slot/length/CRC/segment tuple, native slot capacity and strict UTF-8,
then verify bulk correlation and CRC. Starting an export clears any previously
unclaimed description; failed or stale replies cannot reuse it.

The ledger admits at most 64 claimed identities and 256 recipient/object pairs.
An oversized intake fails the actor visibly before partial initialization. An
export admits at most 512 KiB across the three native byte tables, and both
encoded events must fit the existing public envelope. Only one encoded
description awaits actor receipts at a time. No description cache persists.

Witness/export failures retry no sooner than one second and share fair turns
with existing Dump, history and state IO. Missing native identity remains
pending and diagnostic. Initialization-held presentation is bounded by 65,536
rows and 16 MiB; overflow stops the actor with a named error, never a partial
baseline or a false readiness claim. Native processes are not stopped by this
failure. Recovery requires fixing the cause and the normal warm reattachment.

At an accepted actual world-generation replacement, initialized old gathers
retain their captured readiness for the existing ordered epoch drain.
Uninitialized obsolete gathers retain ownership of any pending native request
until its reply is consumed, then retire their unpublished partial/held data.
Surviving recipients immediately owe new-world Join history, so retirement
cannot produce a false Join-ready interval. Cancellation is counted separately.
New-world description IO never runs through the old epoch/LastTargets cut.
Ordinary Join atomicity and protected Boundary paging remain unchanged.

`RESUME` retains its existing contract: it replays acknowledged history for a
client that retained its presentation state. It is not a cold native-client
initialization API. A cold presenter uses Join. Rebind transfers initial Join
planning debt and remints pending participant tokens; no new recipient inherits
an in-flight export. An already initialized old-world gather can retain its
sealed readiness only under that retained-client resume contract.

## Diagnostics and verification

`FRONT_DOOR_STATE` appends `participant_pending`, `participant_stage`,
`participant_queued`, `participant_errors`, `participant_stale` and bounded
`participant_last_error_hex` counters. `participant_world_cancellations` and
`participant_world_cancel_hex` retain obsolete-world cancellation context.
The `queued` count describes successful server queue initialization only.

Socket regressions use the normal serve entry with capability enabled. They
check both recipients' exact description/spawn/history order, continued Dump
progress during export failure, a held stale export through an epoch cut, and
an animation page arriving when native Join completes between presentation
deadlines. Unit tests separately cover token/payload tampering, same-ID ABA,
per-recipient retry, identity replacement and bounds. Committed-tip offline
results, Windows construction and actual native acceptance are separate gates.

Candidate `afbc21d7` passed 375 immutable clean-room tests and strict Clippy.
Its production encoder's four binary cases also passed the real C# codec and
client-reader checks, including empty tables, non-UTF8 bytes, the size limit and
bulk-reference-like bytes. A normal Windows warm update preserved the three
native games. Both clients decoded descriptions before spawn, with no new wire
errors, and the formerly missing claimed actor remained present. Native import
reported `existed` for the saved descriptions; fresh-description import is not
established by that result. The moving gate remains separate and open.
