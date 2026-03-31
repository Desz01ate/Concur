# MPMC Bounded Channel Design (Concur)

Date: 2026-03-31
Status: Draft approved in chat, written for implementation planning

## 1. Goal

Design an optimized bounded channel for **MPMC** (multi-producer, multi-consumer), tuned for:
- balanced throughput and tail latency under contention,
- strict no-loss/no-duplication delivery,
- competing consumers (each item consumed once globally),
- no strict global FIFO requirement.

This design is intentionally MPMC-first and does not inherit the MPSC stripe semantics.

## 2. Public Surface

Introduce:
- `Concur.Implementations.MpmcBoundedChannel<T> : IChannel<T>`

Maintain existing `IChannel<T>` behavioral contract:
- `WriteAsync` blocks asynchronously when bounded capacity is full,
- `CompleteAsync` stops future writes and allows draining buffered items,
- `FailAsync(ex)` stops future writes, drains buffered items, then propagates `ex`,
- async enumeration supports concurrent competing consumers.

## 3. High-Level Architecture

Use **sharded bounded lock-free rings** with global permits:

- Channel owns `N` independent shards (`N` configurable, default based on CPU count).
- Each shard is a bounded lock-free MPMC ring (Vyukov-style sequence slots).
- Global backpressure and wake signaling use semaphores:
  - `availableSlots` initialized to global capacity,
  - `availableItems` initialized to 0.

Data flow:
1. Producer acquires one slot permit.
2. Producer enqueues into one shard.
3. Producer releases one item permit.
4. Consumer acquires one item permit.
5. Consumer dequeues from preferred shard or steals from others.
6. Consumer releases one slot permit.

This preserves bounded memory globally and distributes contention locally.

## 4. Internal Data Structures

### 4.1 Channel-Level Fields

- `Shard[] shards`
- `int shardCount`
- `SemaphoreSlim availableSlots`
- `SemaphoreSlim availableItems`
- `int completionState` (`0 = open`, `1 = completed/faulted`)
- `Exception? completionException` (set only by first `FailAsync`)
- `int blockedReaders` / `int blockedWriters` (waiter counts for close/fail wakeups)
- Optional diagnostic counters (`pendingItems`, contention stats)

### 4.2 Shard Structure

Each shard holds:
- `Slot[] slots` (fixed capacity, power-of-two)
- `int mask` (`capacity - 1`)
- `long enqueuePos`
- `long dequeuePos`

`Slot`:
- `long sequence`
- `T item`

Sequence protocol (Vyukov):
- Free slot expected sequence at enqueue: `pos`
- Occupied slot expected sequence at dequeue: `pos + 1`
- After dequeue, set sequence to `pos + capacity` (slot becomes free for future cycle)

## 5. Producer Path (`WriteAsync`)

1. Fast-path closed check; if closed throw `InvalidOperationException`.
2. Acquire one slot permit with bounded spin-then-async wait:
   - short `SpinWait` budget for microbursts,
   - fallback to `availableSlots.WaitAsync(token)`.
3. Re-check closed state after wake.
4. Attempt enqueue:
   - Choose initial shard via thread-local probe.
   - Try local enqueue; if contention/temporarily full, probe other shards with rotating start.
   - Use CAS on shard `enqueuePos` to claim position.
5. On success:
   - publish item using slot sequence release-store,
   - release one `availableItems` permit.
6. If closed race is detected before publish, return slot permit and throw.

No global lock on the hot path.

## 6. Consumer Path (`MoveNextAsync`)

1. Try fast dequeue without waiting:
   - home-shard first (consumer-local affinity),
   - then steal pass over other shards.
2. If successful:
   - set `Current`,
   - release one slot permit,
   - return `true`.
3. If unsuccessful:
   - if closed and drained: return `false` or throw stored failure,
   - else spin briefly, then wait on `availableItems.WaitAsync(token)`.
4. After wake, retry dequeue loop.
5. On cancellation: return `false` (align existing channel enumerator behavior).

Consumers are competing: each item observed exactly once globally.

## 7. Completion and Failure Semantics

### 7.1 Complete

- First caller flips `completionState` via interlocked exchange.
- Subsequent calls no-op (idempotent).
- No new writes allowed after state change.
- Buffered items remain readable.
- Wake blocked readers/writers so they can observe closure.

### 7.2 Fail

- First caller flips `completionState`, stores `completionException`.
- Subsequent calls no-op; first exception wins.
- Buffered items remain readable.
- After drain, readers observe stored exception.
- Wake blocked readers/writers.

### 7.3 Drain Detection

Treat channel as drained when:
- completion is set, and
- dequeue attempts across shards fail after consuming all item permits.

Readers perform this check after failed dequeue and after wakeups.

## 8. Tail-Latency Optimizations

- Bounded spin-before-block for both read/write paths.
- Shard affinity for consumers to improve cache locality.
- Stealing with rotating/randomized probe start to reduce herd effects.
- Keep critical sections lock-free; no global mutex in hot paths.
- Keep per-operation allocation at zero on happy path.

## 9. Correctness Invariants

1. **No duplication**: each successful dequeue corresponds to exactly one successful enqueue.
2. **No loss**: every published item becomes available to exactly one consumer unless process aborts externally.
3. **Bounded capacity**: cannot exceed configured global capacity due to `availableSlots` permits.
4. **Permit symmetry**:
   - enqueue success -> +1 item permit,
   - dequeue success -> +1 slot permit.
5. **Close/fail safety**: post-close writes are rejected; pre-close buffered items still drain.

## 10. Implementation Plan Inputs

Expected files:
- `src/Concur/Implementations/MpmcBoundedChannel.cs` (new)
- `src/Concur.Tests/MpmcBoundedChannelTests.cs` (new)
- `src/Concur.Benchmark/Program.cs` (optional benchmark case addition)

Potential constructor shape:
- `MpmcBoundedChannel(int capacity, int? shardCount = null, int? shardCapacity = null)`

Sizing defaults:
- `shardCount = min(Environment.ProcessorCount, 8 or 16)`
- per-shard capacity chosen to cover global capacity with minimal slack; enforce powers of two.

## 11. Test Matrix

Reuse `BoundedChannelBehaviorTests` by deriving `MpmcBoundedChannelTests` and add MPMC-specific tests:

1. Multiple consumers + multiple producers consume all items exactly once.
2. High-contention soak with counts/sum validation.
3. Blocked writers are released on `CompleteAsync`/`FailAsync` and fail appropriately.
4. Blocked readers are released on `CompleteAsync`/`FailAsync` and terminate appropriately.
5. Cancellation for blocked write/read behaves as expected.
6. Idempotency of complete/fail and first-failure-wins semantics.

## 12. Risks and Mitigations

- Risk: semaphore over/under-release in close races.
  - Mitigation: strict state transitions and symmetry assertions in tests.
- Risk: shard imbalance under unlucky hashing.
  - Mitigation: probe fallback + rotating starts + benchmark-driven tuning.
- Risk: subtle memory ordering bugs in slot sequence protocol.
  - Mitigation: rely on interlocked/volatile APIs consistently and include stress tests.

## 13. Non-Goals

- Strict global FIFO ordering across producers.
- Broadcast semantics (same item to all consumers).
- Unbounded channel mode in this implementation.

