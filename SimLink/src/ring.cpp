#include "ring.h"

#include <cstring>

namespace simlink
{
namespace
{
uint32_t Align8(uint32_t value)
{
    return (value + 7u) & ~7u;
}

void WriteRecordHeader(uint8_t* at, uint32_t size, uint16_t kind, uint16_t lane, int32_t seq, uint32_t chars, int64_t qpc,
                       bool full)
{
    std::memcpy(at + 0, &size, 4);
    std::memcpy(at + 4, &kind, 2);
    std::memcpy(at + 6, &lane, 2);
    if (full)
    {
        std::memcpy(at + 8, &seq, 4);
        std::memcpy(at + 12, &chars, 4);
        std::memcpy(at + 16, &qpc, 8);
    }
}
}  // namespace

// 64-bit fields must be read and written whole even in this 32-bit process (the reader is a 64-bit .NET process).
uint64_t Load64(const uint8_t* base, uint32_t offset)
{
    return static_cast<uint64_t>(
        InterlockedCompareExchange64(reinterpret_cast<volatile LONG64*>(const_cast<uint8_t*>(base) + offset), 0, 0));
}

void Store64(uint8_t* base, uint32_t offset, uint64_t value)
{
    InterlockedExchange64(reinterpret_cast<volatile LONG64*>(base + offset), static_cast<LONG64>(value));
}

void Increment64(uint8_t* base, uint32_t offset)
{
    InterlockedIncrement64(reinterpret_cast<volatile LONG64*>(base + offset));
}

uint32_t Load32(const uint8_t* base, uint32_t offset)
{
    return static_cast<uint32_t>(
        InterlockedCompareExchange(reinterpret_cast<volatile LONG*>(const_cast<uint8_t*>(base) + offset), 0, 0));
}

void Store32(uint8_t* base, uint32_t offset, uint32_t value)
{
    InterlockedExchange(reinterpret_cast<volatile LONG*>(base + offset), static_cast<LONG>(value));
}

void InitSection(Ring& ring, uint8_t* base, uint32_t ringSize, uint32_t pid, const char* build)
{
    std::memset(base, 0, kHeaderSize);
    std::memcpy(base + off::Magic, kMagic, sizeof(kMagic));
    Store32(base, off::LayoutVersion, kLayoutVersion);
    Store32(base, off::HeaderSize, kHeaderSize);
    Store32(base, off::Pid, pid);
    Store32(base, off::RingOffset, kHeaderSize);
    Store32(base, off::RingSize, ringSize);
    LARGE_INTEGER frequency;
    QueryPerformanceFrequency(&frequency);
    Store64(base, off::QpcFrequency, static_cast<uint64_t>(frequency.QuadPart));
    Store32(base, off::NativeVersion, static_cast<uint32_t>(kNativeVersion));
    if (build != nullptr)
    {
        strncpy_s(reinterpret_cast<char*>(base + off::Build), 64, build, _TRUNCATE);
    }

    ring.base = base;
    ring.data = base + kHeaderSize;
    ring.size = ringSize;
    ring.busy = 0;
    Store32(base, off::State, StateCreated);
}

int Publish(Ring& ring, uint16_t lane, int32_t seq, const wchar_t* chars, uint32_t count, int64_t qpc)
{
    if (InterlockedCompareExchange(&ring.busy, 1, 0) != 0)
    {
        Increment64(ring.base, off::Contended);
        return 0;
    }

    int written = 0;
    const uint32_t need = Align8(kRecordHeader + count * 2u);
    if (count <= (ring.size / 4u) && need <= ring.size / 2u)
    {
        uint64_t write = Load64(ring.base, off::WritePos);
        const uint64_t read = Load64(ring.base, off::ReadPos);
        const uint64_t used = write - read;
        uint32_t position = static_cast<uint32_t>(write & (ring.size - 1u));
        const uint32_t tail = ring.size - position;
        const uint64_t total = need <= tail ? need : static_cast<uint64_t>(tail) + need;
        if (used + total <= ring.size)
        {
            if (need > tail)
            {
                // Not enough room before the end: a pad record to the end, then the record at the start.
                WriteRecordHeader(ring.data + position, tail, KindPad, 0, 0, 0, 0, false);
                write += tail;
                position = 0;
            }

            uint8_t* at = ring.data + position;
            WriteRecordHeader(at, need, KindData, lane, seq, count, qpc, true);
            std::memcpy(at + kRecordHeader, chars, count * 2u);
            MemoryBarrier();
            Store64(ring.base, off::WritePos, write + need);
            Increment64(ring.base, off::Published);
            written = 1;
        }
    }

    if (!written)
    {
        Increment64(ring.base, off::Dropped);
    }

    InterlockedExchange(&ring.busy, 0);
    return written;
}

namespace
{
struct BatchContext
{
    wchar_t* out;
    uint32_t capacity;
    BatchRead result;
};

int OnBatchRecord(void* context, uint16_t lane, int32_t, const wchar_t* chars, uint32_t count, int64_t qpc)
{
    BatchContext& batch = *static_cast<BatchContext*>(context);
    if (lane == kLaneBatchEnd)
    {
        return batch.result.records > 0 ? 2 : 1;  // the end of this batch: consume it and stop (a stray one: skip)
    }

    if (lane != kLaneReplayLine)
    {
        return 1;
    }

    if (batch.result.used + count + 1u > batch.capacity)
    {
        if (batch.result.records == 0)
        {
            batch.result.needFirst = static_cast<int32_t>(count + 1u);
        }
        return 0;
    }

    std::memcpy(batch.out + batch.result.used, chars, count * sizeof(wchar_t));
    batch.result.used += count;
    batch.out[batch.result.used++] = L'\n';
    batch.result.records++;
    if (qpc < batch.result.oldest)
    {
        batch.result.oldest = qpc;
    }
    if (qpc > batch.result.newest)
    {
        batch.result.newest = qpc;
    }
    return 1;
}
}  // namespace

BatchRead ReadBatch(Ring& ring, wchar_t* out, uint32_t capacity)
{
    BatchContext batch = {out, capacity, BatchRead()};
    if (Consume(ring, &OnBatchRecord, &batch) == kConsumeClaimed)
    {
        return BatchRead();  // the writer took these lines back: the caller must not apply them
    }
    return batch.result;
}

int Consume(Ring& ring, RecordFn onRecord, void* context)
{
    const uint64_t write = Load64(ring.base, off::WritePos);
    MemoryBarrier();
    const uint64_t start = Load64(ring.base, off::ReadPos);
    uint64_t read = start;
    int count = 0;
    while (read < write)
    {
        const uint32_t position = static_cast<uint32_t>(read & (ring.size - 1u));
        const uint8_t* at = ring.data + position;
        uint32_t size;
        uint16_t kind;
        std::memcpy(&size, at, 4);
        std::memcpy(&kind, at + 4, 2);
        if (size == 0 || (size & 7u) != 0 || size > ring.size - position)
        {
            count = -1;
            break;
        }

        if (kind == KindData)
        {
            uint16_t lane;
            int32_t seq;
            uint32_t chars;
            int64_t qpc;
            std::memcpy(&lane, at + 6, 2);
            std::memcpy(&seq, at + 8, 4);
            std::memcpy(&chars, at + 12, 4);
            std::memcpy(&qpc, at + 16, 8);
            if (kRecordHeader + static_cast<uint64_t>(chars) * 2u > size)
            {
                count = -1;
                break;
            }

            const int verdict = onRecord(context, lane, seq, reinterpret_cast<const wchar_t*>(at + kRecordHeader), chars, qpc);
            if (verdict == 0)
            {
                break;
            }

            ++count;
            if (verdict == 2)
            {
                read += size;
                break;
            }
        }
        else if (kind != KindPad)
        {
            count = -1;
            break;
        }

        read += size;
    }

    MemoryBarrier();
    if (read != start)
    {
        const LONG64 seen = InterlockedCompareExchange64(reinterpret_cast<volatile LONG64*>(ring.base + off::ReadPos),
                                                         static_cast<LONG64>(read), static_cast<LONG64>(start));
        if (static_cast<uint64_t>(seen) != start)
        {
            return kConsumeClaimed;
        }
    }
    return count;
}
}  // namespace simlink
