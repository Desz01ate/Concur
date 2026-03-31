# MPMC Bounded Channel Design (Concur)

Date: 2026-03-31
Status: Revised after review

## 1. Goal

Design an optimized bounded channel for **MPMC** (multi-producer, multi-consumer), tuned for:
- balanced throughput and tail latency under contention,
- strict no-loss/no-duplication delivery,
- competing consumers (each item consumed once globally),
- no strict global FIFO requirement.

This design is intentionally MPMC-first and does not inherit the MPSC stripe semantics.

## 2. Public Surface

Introduce:
- `public sealed class MpmcBoundedChannel<T> : IChannel<T>` in `Concur.Implementations`

Maintain existing `IChannel<T>` behavioral contract:
- `WriteAsync` blocks asynchronously when bounded capacity is full,
- `CompleteAsync` stops future writes and allows draining buffered items,
- `FailAsync(ex)` stops future writes, drains buffered items, then propagates `ex`,
- async enumeration supports concurrent competing consumers.

`ConcurRoutine.Go<T>()` requires no API changes. Users opt into MPMC by passing
`channelFactory: () => new MpmcBoundedChannel<T>(...)`.

## 3. High-Level Architecture

Use **sharded bounded lock-free rings** with global permits:

- Channel owns `N` independent shards (`N` configurable, default based on CPU count).
- Each shard is a bounded lock-free MPMC ring (Vyukov-style sequence slots).
- Global backpressure and wake signaling use semaphores:
  - `availableSlots` initialized to the **requested logical capacity**,
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
- `TaskCompletionSource completionSignal` (`RunContinuationsAsynchronously`)
- `long nextWriterShardProbe` (global seed; each write snapshots start shard via interlocked increment)
- `long nextReaderShardSeed` (enumerator home-shard seed)

### 4.2 Shard Structure

Each shard holds:
- `Slot[] slots` (fixed capacity, power-of-two)
- `int mask` (`capacity - 1`)
- `int capacity`
- `long enqueuePos`
- `long dequeuePos`

`Slot`:
- `long sequence`
- `T item`

Sequence protocol (Vyukov):
- Free slot expected sequence at enqueue: `pos`
- Occupied slot expected sequence at dequeue: `pos + 1`
- After dequeue, set sequence to `pos + capacity` (slot becomes free for future cycle)

### 4.3 Capacity Math (Explicit)

Inputs:
- `requestedCapacity` (constructor argument, must be > 0)
- `shardCount` (must be > 0)

Formula:
1. `basePerShard = ceil(requestedCapacity / shardCount)`
2. `perShardCapacity = NextPowerOfTwo(max(2, basePerShard))`
3. `physicalCapacity = shardCount * perShardCapacity`
4. `availableSlots` initial count = `requestedCapacity` (logical bound)

Example: `requestedCapacity=100`, `shardCount=8`:
- `basePerShard=13`
- `perShardCapacity=16`
- `physicalCapacity=128`
- logical write limit remains 100 via `availableSlots`

So memory is provisioned to physical capacity, but externally visible bounded behavior is logical capacity.

## 5. .NET Memory Ordering Requirements (ARM64-safe)

The ring protocol must use explicit .NET synchronization primitives, not architecture assumptions.

Required primitives:
- `Interlocked.CompareExchange(ref enqueuePos, ...)` and `Interlocked.CompareExchange(ref dequeuePos, ...)` for position claims.
- `Volatile.Read(ref slot.sequence)` for sequence checks.
- `Volatile.Write(ref slot.sequence, newSeq)` for publish/free transitions.
- Plain write to `slot.item` occurs before publish `Volatile.Write`.
- Plain read of `slot.item` occurs after successful claim and before free `Volatile.Write`.

Rule of thumb for implementation:
- Producer: write payload, then release-publish sequence with `Volatile.Write`.
- Consumer: observe sequence with acquire `Volatile.Read`, then read payload.

This is mandatory for correctness on x64 and ARM64.

## 6. Producer Path (`WriteAsync`)

1. Fast-path closed check; if closed throw `InvalidOperationException`.
2. Acquire one slot permit with bounded spin-then-async wait:
   - short `SpinWait` budget for microbursts,
   - fallback to `WaitForSlotOrCompletionAsync(token)` which races slot wait with completion signal.
3. Re-check closed state after wake.
4. Attempt enqueue into shards:
   - Choose initial shard start as `(int)(Interlocked.Increment(ref nextWriterShardProbe) % shardCount)`.
   - Probe all shards (rotating order) with lock-free enqueue attempts.
5. If all shard attempts fail after full probe pass (transient contention case):
   - do bounded spin/yield retry loops while permit is held,
   - after retry budget exhausted, `await Task.Yield()` and retry probe.
   - **Never return the slot permit during open state**; permit implies eventual enqueue progress.
   - Retry loop is intentionally unbounded until enqueue succeeds or channel transitions to completed/faulted.
6. On success:
   - publish item using `Volatile.Write` on slot sequence,
   - release one `availableItems` permit.
7. If closed race is detected before publish, return slot permit and throw.

No global lock on the hot path.

## 7. Consumer Path (`MoveNextAsync`)

1. Try fast dequeue without waiting:
   - home-shard first,
   - then steal pass over other shards.
2. If successful:
   - set `Current`,
   - release one slot permit,
   - return `true`.
3. If unsuccessful:
   - if closed and drained: return `false` or throw stored failure,
   - else spin briefly, then wait on `WaitForItemOrCompletionAsync(token)`.
4. After wake, retry dequeue loop.
5. On cancellation: return `false` (align existing channel enumerator behavior).

Consumers are competing: each item observed exactly once globally.

### 7.1 Consumer Home-Shard Strategy

- Each enumerator gets a stable home shard at construction:
  - `homeShard = (int)(Interlocked.Increment(ref nextReaderShardSeed) % shardCount)`
- On each dequeue attempt:
  - try `homeShard` first,
  - then probe remaining shards with rotating start offset.

This gives locality while preventing starvation under asymmetric consumer counts.

## 8. Completion and Failure Semantics

### 8.1 Complete

- First caller flips `completionState` via `Interlocked.Exchange`.
- Subsequent calls no-op (idempotent).
- No new writes allowed after state change.
- Buffered items remain readable.
- Signal `completionSignal.TrySetResult()` to wake waits that are racing semaphore wait with completion.

### 8.2 Fail

- First caller flips `completionState`, stores `completionException`.
- Subsequent calls no-op; first exception wins.
- Buffered items remain readable.
- After drain, readers observe stored exception.
- Signal `completionSignal.TrySetResult()` to wake waits that are racing semaphore wait with completion.

### 8.3 Drain Detection

Treat channel as drained when:
- completion is set, and
- dequeue attempts across shards fail after consuming all item permits.

Readers perform this check after failed dequeue and after wakeups.

### 8.4 Wait-For-Completion Race Pattern

`WaitForSlotOrCompletionAsync(token)` and `WaitForItemOrCompletionAsync(token)` must avoid
permit leaks when completion races semaphore waits.

Required rule:
- Do **not** implement this as raw `Task.WhenAny(semaphore.WaitAsync(), completionSignal.Task)`
  without cleanup, because the semaphore task can complete later and consume a permit.

Recommended pattern:
1. Fast-check completion state.
2. Try immediate `semaphore.Wait(0)` fast path.
3. Await `semaphore.WaitAsync(linkedToken)` where `linkedToken` is canceled by either:
   - caller cancellation token, or
   - `completionSignal` transition.
4. If wake was due to completion, re-check channel state in caller loop and do not assume permit ownership.

This ensures no unpaired semaphore acquisition is left behind.

## 9. Tail-Latency Optimizations

- Bounded spin-before-block for both read/write paths.
- Stable consumer home-shard affinity for cache locality.
- Stealing with rotating/randomized probe start to reduce herd effects.
- Keep critical sections lock-free; no global mutex in hot paths.
- Keep per-operation allocation at zero on happy path.

## 10. Correctness Invariants

1. **No duplication**: each successful dequeue corresponds to exactly one successful enqueue.
2. **No loss**: every published item becomes available to exactly one consumer unless process aborts externally.
3. **Bounded logical capacity**: in-flight item count cannot exceed `requestedCapacity` due to `availableSlots` permits.
4. **Permit symmetry**:
   - enqueue success -> +1 item permit,
   - dequeue success -> +1 slot permit.
5. **Close/fail safety**: post-close writes are rejected; pre-close buffered items still drain.
6. **Physical capacity safety**: shard enqueue only occurs when a free ring slot exists by sequence protocol.

## 11. Implementation Plan Inputs

Expected files:
- `src/Concur/Implementations/MpmcBoundedChannel.cs` (new)
- `src/Concur.Tests/MpmcBoundedChannelTests.cs` (new)
- `src/Concur.Benchmark/Program.cs` (optional benchmark case addition)

Potential constructor shape:
- `MpmcBoundedChannel(int capacity, int? shardCount = null)`

Sizing defaults:
- `shardCount = min(Environment.ProcessorCount, 8 or 16)`
- per-shard capacity computed using Section 4.3 formula.
- No public `shardCapacity` override in v1 by design; keep external API minimal and auto-tuned.

## 12. Test Matrix

Reuse `BoundedChannelBehaviorTests` by deriving `MpmcBoundedChannelTests` and add MPMC-specific tests:

1. Constructor validation:
   - zero/negative capacity throws,
   - zero/negative shardCount throws.
2. Single-shard FIFO:
   - with `shardCount=1`, insertion order is preserved.
3. Competing-consumer correctness:
   - multiple producers + multiple consumers consume all items exactly once.
4. High-contention soak:
   - producers >> shards and consumers >> shards to stress steal paths.
5. Global capacity bound:
   - concurrent writes block at logical bound and resume after reads.
6. Write cancellation when full:
   - blocked `WriteAsync` with canceled token throws `OperationCanceledException`.
7. Reader cancellation:
   - canceled enumeration exits without exception.
8. Completion/failure wakeups:
   - blocked readers/writers are released on complete/fail and observe correct terminal behavior.
9. Idempotency and first-failure-wins semantics.

## 13. Risks and Mitigations

- Risk: permit/accounting drift in close races.
  - Mitigation: completion-signal race waits + permit symmetry assertions in tests.
- Risk: shard imbalance under unlucky probing.
  - Mitigation: rotating starts + thread-local seeds + benchmark tuning.
- Risk: memory ordering bugs in slot sequence protocol.
  - Mitigation: strict primitive usage from Section 5 and stress tests on ARM64/x64 CI.

## 14. Non-Goals

- Strict global FIFO ordering across producers.
- Broadcast semantics (same item to all consumers).
- Unbounded channel mode in this implementation.
