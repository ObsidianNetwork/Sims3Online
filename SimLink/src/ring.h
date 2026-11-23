#pragma once

#include <windows.h>

#include <cstdint>

#include "simlink_layout.h"

namespace simlink
{
struct Ring
{
    uint8_t* base = nullptr;  // header
    uint8_t* data = nullptr;  // ring bytes
    uint32_t size = 0;        // power of two
    volatile LONG busy = 0;   // single-writer guard: a second writer is counted, not allowed in
};

// Initialises the header of a freshly created section and binds the ring to it.
void InitSection(Ring& ring, uint8_t* base, uint32_t ringSize, uint32_t pid, const char* build);

// Publishes one record; never blocks, never allocates. Returns 1 when written, 0 when dropped (ring full, record
// too large, or another writer was publishing).
int Publish(Ring& ring, uint16_t lane, int32_t seq, const wchar_t* chars, uint32_t count, int64_t qpc);

// Consumer side (the ring another process writes, e.g. the controller's inbound lane): calls onRecord for every
// complete data record and skips pads, then frees the space consumed. onRecord returns 1 to consume the record, 0 to
// stop before it (it stays for the next call), 2 to consume it and stop. Returns the number of data records consumed,
// or -1 when a record is malformed (consumption stops; the ring is left at the last good record), or kConsumeClaimed
// when the writer claimed the records while they were being read (see below): nothing counts as consumed then.
//
// The read position moves only by compare-and-swap from where the call started, so the writer can take back what the
// consumer hasn't read (the client controller reclaiming its backlog from a consumer that stopped) without a race: a
// consumer that read the same records commits nothing and must drop what onRecord saw.
typedef int (*RecordFn)(void* context, uint16_t lane, int32_t seq, const wchar_t* chars, uint32_t count, int64_t qpc);
constexpr int kConsumeClaimed = -2;
int Consume(Ring& ring, RecordFn onRecord, void* context);

// The client ring path's lanes: replay lines, and the marker the controller writes after each SACS_REPLAY-sized batch.
constexpr uint16_t kLaneReplayLine = 1;
constexpr uint16_t kLaneBatchEnd = 5;

struct BatchRead
{
    uint32_t used = 0;       // chars written (each line followed by '\n')
    int32_t needFirst = 0;   // > 0: the first line needs this many chars and didn't fit
    int records = 0;         // lines copied
    int64_t oldest = INT64_MAX;
    int64_t newest = 0;
};

// Copies the next batch of replay lines (up to and including its batch-end marker, which is consumed) into out, as
// many whole lines as fit; a batch that doesn't fit is returned in parts. Lines of other lanes are consumed and
// ignored. Returns the BatchRead (records == 0: nothing waiting, needFirst > 0, or the writer claimed the batch).
BatchRead ReadBatch(Ring& ring, wchar_t* out, uint32_t capacity);

uint64_t Load64(const uint8_t* base, uint32_t offset);
void Store64(uint8_t* base, uint32_t offset, uint64_t value);
void Increment64(uint8_t* base, uint32_t offset);
uint32_t Load32(const uint8_t* base, uint32_t offset);
void Store32(uint8_t* base, uint32_t offset, uint32_t value);
}  // namespace simlink
