# Performance Optimizations Design

**Date:** 2026-03-31
**Status:** Approved
**Scope:** ConcurRoutine.cs, WaitGroup.cs

## Context

Benchmarks (see `benchmark-analysis.md`) show Concur's `Go()` wrappers are ~7-8% slower than raw TPL+Channel usage, with 36% more lock contentions. Memory allocation is identical at 2.01 MB. The two primary sources of overhead are:

1. The `ExecuteWithConcurrencyLimitAsync` indirection on every `Go()` call, even when no concurrency limit is configured
2. WaitGroup's `SemaphoreSlim` synchronization on `Add()`/`Done()` calls

## Design Decisions

- **Approach:** Aggressive optimization — complexity is acceptable for performance
- **API compatibility:** Public API unchanged; internal implementation changes only (minor additions permitted)
- **Dropped:** Lazy routine ID generation — `GenerateRoutineId()` is already exception-path-only, no optimization needed
- **Dropped:** Closure reduction via state tuples for generic overloads — invisible at benchmark scale, can be added incrementally if future benchmarks isolate this

## Optimization 1: Fast-Path Elimination of ExecuteWithConcurrencyLimitAsync

### Problem

Every `Go()` call currently follows this path:

```
Task.Run -> async lambda -> ExecuteWithConcurrencyLimitAsync -> async lambda (user action)
```

This creates 2 async state machines, 2 closures, and 1 `Func<Task>` delegate per call. When `options` is null (the common case), `ExecuteWithConcurrencyLimitAsync` simply does `await action(); return;` — pure overhead.

### Design

Remove `ExecuteWithConcurrencyLimitAsync` entirely. Replace with inline branching at the call site:

- **Fast path** (no concurrency limit): `Task.Run` directly wraps the user action in a try/catch. One async state machine, one closure.
- **Slow path** (concurrency limit present): `Task.Run` wraps the user action with inline `semaphore.WaitAsync()` / `semaphore.Release()` in a try/finally.

The branch condition is `options?.GetOrCreateSemaphore()` evaluated once before `Task.Run`.

### Pattern

Using `Go(Action, GoOptions?)` as the canonical example (all overloads follow the same pattern):

```csharp
public static void Go(Action action, GoOptions? options = null)
{
    var semaphore = options?.GetOrCreateSemaphore();

    if (semaphore is null)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                await HandleExceptionAsync(e, GenerateRoutineId(), options);
            }
        });
    }
    else
    {
        _ = Task.Run(async () =>
        {
            await semaphore.WaitAsync();
            try
            {
                action();
            }
            catch (Exception e)
            {
                await HandleExceptionAsync(e, GenerateRoutineId(), options);
            }
            finally
            {
                semaphore.Release();
            }
        });
    }
}
```

WaitGroup variants add `wg.Add(1)` before the branch and `wg.Done()` in the `finally` block of both paths.

The channel producer variant (`Go<T>(Func<DefaultChannel<T>, Task>, ...)`) follows the slow-path pattern for both branches, with the addition of `await channel.FailAsync(e)` in the catch block before `HandleExceptionAsync`.

### Affected Overloads

All `Go()` overloads in `ConcurRoutine.cs`:
- `Go(Action, GoOptions?)` and `Go(Func<Task>, GoOptions?)`
- `Go<T>(Func<DefaultChannel<T>, Task>, int?, GoOptions?)` (channel producer)
- `Go(WaitGroup, Action, GoOptions?)` and `Go(WaitGroup, Func<Task>, GoOptions?)`
- All generic variants: `Go<T>` through `Go<T1..T8>` (sync, async, with/without WaitGroup)

### What Is Removed

- The `ExecuteWithConcurrencyLimitAsync` private method is deleted entirely

## Optimization 2: Lock-Free WaitGroup

### Problem

The current WaitGroup uses `SemaphoreSlim` to protect `count` and `TaskCompletionSource`. Every `Add()` and `Done()` call acquires and releases the semaphore. Under 16 concurrent producers, this produces ~1,450 extra lock contentions versus the TPL baseline.

### Design

Replace the `SemaphoreSlim` + separate `count`/`tcs` fields with an **immutable state object** swapped atomically via `Interlocked.CompareExchange<T>`.

Both `count` and `tcs` are bundled into a single immutable `State` object. All mutations read the current state, compute the next state, and CAS-swap. This eliminates the two-variable atomicity problem that makes a naive lock-free approach (separate `Interlocked` on `count` and `volatile` on `tcs`) incorrect.

### Correctness Properties

- **Linearizable:** Every operation has a defined linearization point:
  - `Add(delta)` / `Done()`: the successful `Interlocked.CompareExchange`
  - `WaitAsync()` / `Wait()`: the read of `this.state`
- **Lock-free:** At least one thread makes progress per CAS round
- **No convoying:** A preempted thread does not block others
- **No early escape:** A waiter can only observe a completed TCS if count was atomically 0 at the CAS commit point
- **No rollback hazard:** Invalid states (negative count) are rejected before the CAS — no invalid state is ever written

### Contract

Matches Go's `sync.WaitGroup`:
- `Add` with positive delta must happen-before `Wait`/`WaitAsync` for correct usage
- Concurrent `Done()` calls are safe
- Concurrent `Add()` + `WaitAsync()` is linearizable but provides weak useful guarantees (waiter may or may not observe a concurrent Add depending on scheduling)

Concur's `Go(wg, action)` enforces Add-before-Wait structurally: `wg.Add(1)` is called synchronously before `Task.Run`, and `WaitAsync()` is called after all `Go()` calls return.

### Implementation

```csharp
public sealed class WaitGroup
{
    private sealed class State
    {
        public readonly int Count;
        public readonly TaskCompletionSource<bool> Tcs;

        public State(int count, TaskCompletionSource<bool> tcs)
        {
            this.Count = count;
            this.Tcs = tcs;
        }
    }

    private State state;

    public WaitGroup()
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        tcs.SetResult(true);
        this.state = new State(0, tcs);
    }

    public void Add(int delta)
    {
        SpinWait spin = default;

        while (true)
        {
            var current = this.state;
            var newCount = current.Count + delta;

            if (newCount < 0)
            {
                throw new InvalidOperationException("WaitGroup counter cannot be negative.");
            }

            State next;

            if (current.Count == 0 && delta > 0)
            {
                var freshTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                next = new State(newCount, freshTcs);
            }
            else
            {
                next = new State(newCount, current.Tcs);
            }

            if (Interlocked.CompareExchange(ref this.state, next, current) == current)
            {
                if (newCount == 0)
                {
                    current.Tcs.TrySetResult(true);
                }

                return;
            }

            spin.SpinOnce();
        }
    }

    public void Done() => this.Add(-1);

    public Task WaitAsync() => this.state.Tcs.Task;

    public void Wait() => this.state.Tcs.Task.GetAwaiter().GetResult();
}
```

### What Is Removed

- `SemaphoreSlim semaphore` field
- All `semaphore.Wait()` / `semaphore.WaitAsync()` / `semaphore.Release()` calls
- The `lock` acquisition in `WaitAsync()` and `Wait()`

### Allocation Impact

- One `State` object (~32 bytes) per successful CAS
- WaitGroup operations are low-frequency (once per goroutine, not per channel write)
- For 16 goroutines: ~33 State objects per benchmark iteration (~1KB) — negligible
- For 10,000 goroutines: ~320KB of short-lived Gen0 objects — still negligible

## Expected Impact

| Metric | Current | After Optimization |
|--------|---------|-------------------|
| Async state machines per Go() call | 2 | 1 (fast path) |
| Closures per Go() call | 2 | 1 (fast path) |
| Func<Task> delegates per Go() call | 1 | 0 (fast path) |
| WaitGroup lock acquisitions per Add/Done | 2 (SemaphoreSlim) | 0 (CAS) |
| WaitGroup lock acquisitions per WaitAsync | 2 (SemaphoreSlim) | 0 (read) |

## Testing Strategy

- All existing unit tests must pass unchanged (public API is unchanged)
- Add targeted tests for WaitGroup under concurrent Add/Done stress
- Add tests for negative-count rejection under concurrency
- Run benchmarks before and after to measure actual improvement
- Compare lock contentions (ThreadingDiagnoser) specifically
