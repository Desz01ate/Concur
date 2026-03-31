# Performance Optimizations Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Eliminate per-call async overhead in `Go()` and replace WaitGroup's `SemaphoreSlim` with a lock-free CAS-based implementation to close the ~7-8% performance gap with raw TPL.

**Architecture:** Two independent optimizations. (1) Fast-path branching in `ConcurRoutine.Go()` skips `ExecuteWithConcurrencyLimitAsync` when no concurrency limit is configured, removing one async state machine, one closure, and one delegate per call. (2) WaitGroup replaces `SemaphoreSlim` with an immutable `State` object swapped atomically via `Interlocked.CompareExchange`, achieving zero lock contention on `Add()`/`Done()`.

**Tech Stack:** .NET 8, C#, xUnit, BenchmarkDotNet

---

## File Structure

| File | Action | Responsibility |
|------|--------|----------------|
| `src/Concur/WaitGroup.cs` | Modify | Replace SemaphoreSlim with CAS-based State |
| `src/Concur.Tests/WaitGroupTests.cs` | Modify | Add concurrent stress tests |
| `src/Concur/ConcurRoutine.cs` | Modify | Inline fast/slow path, delete `ExecuteWithConcurrencyLimitAsync` |

---

### Task 1: Lock-Free WaitGroup — Write New Concurrency Tests

**Files:**
- Modify: `src/Concur.Tests/WaitGroupTests.cs`

These tests target the CAS-specific invariants. They should pass against the current SemaphoreSlim implementation too (they test behavior, not implementation), which proves the tests are valid before we change anything.

- [ ] **Step 1: Add concurrent stress test for many rapid Add/Done cycles**

Add this test to the end of `WaitGroupTests.cs`:

```csharp
[Fact]
public async Task Add_WithManyConcurrentDoneCalls_CountReachesZero()
{
    // Stress-tests that concurrent Done() calls all decrement correctly
    // and the final Done() signals the TCS exactly once.
    var iterations = 500;

    for (var i = 0; i < iterations; i++)
    {
        var wg = new WaitGroup();
        var routineCount = 100;

        for (var j = 0; j < routineCount; j++)
        {
            wg.Add(1);
        }

        var tasks = new Task[routineCount];

        for (var j = 0; j < routineCount; j++)
        {
            tasks[j] = Task.Run(() =>
            {
                Thread.SpinWait(10);
                wg.Done();
            });
        }

        var completedInTime = await Task.WhenAny(wg.WaitAsync(), Task.Delay(5000)) == wg.WaitAsync();
        Assert.True(completedInTime, $"WaitGroup did not complete in iteration {i}");

        await Task.WhenAll(tasks);
    }
}
```

- [ ] **Step 2: Add test for negative count rejection under concurrency**

Add this test to `WaitGroupTests.cs`:

```csharp
[Fact]
public async Task Done_WithMoreDoneThanAdd_ThrowsInvalidOperationException()
{
    var wg = new WaitGroup();
    wg.Add(1);
    wg.Done();

    var exception = Record.Exception(() => wg.Done());
    Assert.NotNull(exception);
    Assert.IsType<InvalidOperationException>(exception);
}
```

- [ ] **Step 3: Add test for multiple phase reuse with high concurrency**

Add this test to `WaitGroupTests.cs`:

```csharp
[Fact]
public async Task WaitAsync_WithMultiplePhases_EachPhaseCompletesIndependently()
{
    // Runs multiple sequential phases on the same WaitGroup instance,
    // each with high concurrency, to verify that phase transitions
    // (0 -> positive -> 0) create fresh TCS objects correctly.
    var wg = new WaitGroup();
    var phases = 50;
    var routinesPerPhase = 50;

    for (var phase = 0; phase < phases; phase++)
    {
        var completed = 0;

        for (var j = 0; j < routinesPerPhase; j++)
        {
            wg.Add(1);

            _ = Task.Run(() =>
            {
                Thread.SpinWait(10);
                Interlocked.Increment(ref completed);
                wg.Done();
            });
        }

        await wg.WaitAsync();

        Assert.Equal(routinesPerPhase, Volatile.Read(ref completed));
    }
}
```

- [ ] **Step 4: Run tests to verify they pass against the current implementation**

Run: `dotnet test src/Concur.Tests/ --filter "FullyQualifiedName~WaitGroupTests" --no-restore`
Expected: All tests PASS (including the new ones)

- [ ] **Step 5: Commit**

```bash
git add src/Concur.Tests/WaitGroupTests.cs
git commit -m "test: add concurrent stress tests for WaitGroup ahead of lock-free refactor"
```

---

### Task 2: Lock-Free WaitGroup — Replace SemaphoreSlim with CAS

**Files:**
- Modify: `src/Concur/WaitGroup.cs`

- [ ] **Step 1: Replace the entire WaitGroup implementation**

Replace the contents of `src/Concur/WaitGroup.cs` with:

```csharp
namespace Concur;

/// <summary>
/// A WaitGroup waits for a collection of routines to finish.
/// The main routine calls Add to set the number of routines to wait for.
/// Then each of the routines runs and calls Done when finished.
/// At the same time, Wait can be used to block until all routines have finished.
/// A WaitGroup must not be copied after first use.
/// </summary>
/// <remarks>
/// This implementation is lock-free using an immutable state object
/// swapped atomically via <see cref="Interlocked.CompareExchange{T}"/>.
/// All operations are linearizable. Add/Done have their linearization point
/// at the successful CAS; WaitAsync/Wait linearize at the state read.
/// </remarks>
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

    /// <summary>
    /// Initializes a new instance of the <see cref="WaitGroup"/> class.
    /// </summary>
    public WaitGroup()
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        tcs.SetResult(true);
        this.state = new State(0, tcs);
    }

    /// <summary>
    /// Adds a delta to the WaitGroup counter.
    /// If the counter becomes zero, all routines blocked on Wait are released.
    /// If the counter goes negative, it panics.
    /// </summary>
    /// <param name="delta">The number of routines to add to the wait group.</param>
    /// <exception cref="InvalidOperationException">Thrown when the counter goes negative.</exception>
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

    /// <summary>
    /// Decrements the WaitGroup counter by one.
    /// </summary>
    public void Done()
    {
        this.Add(-1);
    }

    /// <summary>
    /// Blocks until the WaitGroup counter is zero asynchronously.
    /// </summary>
    public Task WaitAsync()
    {
        return this.state.Tcs.Task;
    }

    /// <summary>
    /// Blocks until the WaitGroup counter is zero.
    /// </summary>
    public void Wait()
    {
        this.state.Tcs.Task.GetAwaiter().GetResult();
    }
}
```

- [ ] **Step 2: Run all tests to verify correctness**

Run: `dotnet test src/Concur.Tests/ --no-restore`
Expected: All tests PASS

- [ ] **Step 3: Commit**

```bash
git add src/Concur/WaitGroup.cs
git commit -m "perf: replace WaitGroup SemaphoreSlim with lock-free CAS"
```

---

### Task 3: Fast-Path — Write Test for Slow Path (Concurrency-Limited) Behavior

Existing tests in `ConcurrencyLimitingTests.cs` already cover the slow path (semaphore-based concurrency limiting). We need to verify these still pass after the refactor. No new tests needed here — just confirm the baseline.

**Files:**
- Test: `src/Concur.Tests/ConcurrencyLimitingTests.cs` (read-only, verify passing)

- [ ] **Step 1: Run concurrency limiting tests to establish baseline**

Run: `dotnet test src/Concur.Tests/ --filter "FullyQualifiedName~ConcurrencyLimitingTests" --no-restore`
Expected: All 7 tests PASS

- [ ] **Step 2: Commit (no code changes — baseline verified)**

No commit needed. Move to next task.

---

### Task 4: Fast-Path — Refactor Core Go() Overloads

**Files:**
- Modify: `src/Concur/ConcurRoutine.cs:58-78` (delete `ExecuteWithConcurrencyLimitAsync`)
- Modify: `src/Concur/ConcurRoutine.cs:85-157` (refactor 5 core overloads)

This task refactors the 5 non-generic `Go()` overloads:
1. `Go(Action, GoOptions?)`
2. `Go(Func<Task>, GoOptions?)`
3. `Go<T>(Func<DefaultChannel<T>, Task>, int?, GoOptions?)` (channel producer)
4. `Go(WaitGroup, Action, GoOptions?)`
5. `Go(WaitGroup, Func<Task>, GoOptions?)`

- [ ] **Step 1: Delete `ExecuteWithConcurrencyLimitAsync`**

Delete lines 53-78 in `ConcurRoutine.cs` — the entire `ExecuteWithConcurrencyLimitAsync` method:

```csharp
// DELETE THIS ENTIRE METHOD:
/// <summary>
/// Executes an action with concurrency limiting if specified in options.
/// </summary>
/// <param name="action">The action to execute.</param>
/// <param name="options">Optional configuration options including concurrency limits.</param>
private static async Task ExecuteWithConcurrencyLimitAsync(Func<Task> action, GoOptions? options)
{
    var semaphore = options?.GetOrCreateSemaphore();

    if (semaphore == null)
    {
        await action();
        return;
    }

    await semaphore.WaitAsync();

    try
    {
        await action();
    }
    finally
    {
        semaphore.Release();
    }
}
```

- [ ] **Step 2: Refactor `Go(Action, GoOptions?)`**

Replace the existing method body with:

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

- [ ] **Step 3: Refactor `Go(Func<Task>, GoOptions?)`**

Replace the existing method body with:

```csharp
public static void Go(Func<Task> func, GoOptions? options = null)
{
    var semaphore = options?.GetOrCreateSemaphore();

    if (semaphore is null)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await func();
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
                await func();
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

- [ ] **Step 4: Refactor `Go<T>(Func<DefaultChannel<T>, Task>, int?, GoOptions?)` (channel producer)**

Replace the existing method body with:

```csharp
public static IChannel<T, DefaultChannel<T>> Go<T>(Func<DefaultChannel<T>, Task> producer, int? capacity = null, GoOptions? options = null)
{
    var channel = new DefaultChannel<T>(capacity);
    var semaphore = options?.GetOrCreateSemaphore();

    if (semaphore is null)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await producer(channel);
            }
            catch (Exception e)
            {
                await channel.FailAsync(e);
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
                await producer(channel);
            }
            catch (Exception e)
            {
                await channel.FailAsync(e);
                await HandleExceptionAsync(e, GenerateRoutineId(), options);
            }
            finally
            {
                semaphore.Release();
            }
        });
    }

    return channel;
}
```

- [ ] **Step 5: Refactor `Go(WaitGroup, Action, GoOptions?)`**

Replace the existing method body with:

```csharp
public static void Go(WaitGroup wg, Action action, GoOptions? options = null)
{
    wg.Add(1);
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
            finally
            {
                wg.Done();
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
                wg.Done();
                semaphore.Release();
            }
        });
    }
}
```

- [ ] **Step 6: Refactor `Go(WaitGroup, Func<Task>, GoOptions?)`**

Replace the existing method body with:

```csharp
public static void Go(WaitGroup wg, Func<Task> func, GoOptions? options = null)
{
    wg.Add(1);
    var semaphore = options?.GetOrCreateSemaphore();

    if (semaphore is null)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await func();
            }
            catch (Exception e)
            {
                await HandleExceptionAsync(e, GenerateRoutineId(), options);
            }
            finally
            {
                wg.Done();
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
                await func();
            }
            catch (Exception e)
            {
                await HandleExceptionAsync(e, GenerateRoutineId(), options);
            }
            finally
            {
                wg.Done();
                semaphore.Release();
            }
        });
    }
}
```

- [ ] **Step 7: Run all tests**

Run: `dotnet test src/Concur.Tests/ --no-restore`
Expected: All tests PASS

- [ ] **Step 8: Commit**

```bash
git add src/Concur/ConcurRoutine.cs
git commit -m "perf: inline fast/slow path for core Go() overloads, remove ExecuteWithConcurrencyLimitAsync"
```

---

### Task 5: Fast-Path — Refactor Generic Sync Overloads (No WaitGroup)

**Files:**
- Modify: `src/Concur/ConcurRoutine.cs` — region `Generics - Sync` (lines 237-459)

All 8 overloads follow the same pattern. Each currently wraps the action in `ExecuteWithConcurrencyLimitAsync`. Replace with inline fast/slow path branching.

- [ ] **Step 1: Refactor `Go<T>(Action<T>, T, GoOptions?)`**

Replace the existing method body with:

```csharp
public static void Go<T>(Action<T> func, T p, GoOptions? options = null)
{
    var semaphore = options?.GetOrCreateSemaphore();

    if (semaphore is null)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                func(p);
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
                func(p);
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

- [ ] **Step 2: Refactor `Go<T1, T2>(Action<T1, T2>, T1, T2, GoOptions?)`**

Replace the existing method body with:

```csharp
public static void Go<T1, T2>(Action<T1, T2> func, T1 p1, T2 p2, GoOptions? options = null)
{
    var semaphore = options?.GetOrCreateSemaphore();

    if (semaphore is null)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                func(p1, p2);
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
                func(p1, p2);
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

- [ ] **Step 3: Refactor `Go<T1, T2, T3>` through `Go<T1..T8>` (remaining 6 overloads)**

Apply the identical pattern to the remaining 6 sync overloads (`Go<T1, T2, T3>` through `Go<T1, T2, T3, T4, T5, T6, T7, T8>`). Each follows the exact same structure as Step 2, adding one more parameter to the `func(...)` call. The pattern is:

```csharp
public static void Go<T1, ..., TN>(Action<T1, ..., TN> func, T1 p1, ..., TN pN, GoOptions? options = null)
{
    var semaphore = options?.GetOrCreateSemaphore();

    if (semaphore is null)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                func(p1, ..., pN);
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
                func(p1, ..., pN);
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

- [ ] **Step 4: Run tests for sync generics**

Run: `dotnet test src/Concur.Tests/ --filter "FullyQualifiedName~ConcurRoutineTests" --no-restore`
Expected: All tests PASS

- [ ] **Step 5: Commit**

```bash
git add src/Concur/ConcurRoutine.cs
git commit -m "perf: inline fast/slow path for generic sync Go() overloads"
```

---

### Task 6: Fast-Path — Refactor Generic Async Overloads (No WaitGroup)

**Files:**
- Modify: `src/Concur/ConcurRoutine.cs` — region `Generics - Async` (lines 461-691)

Same pattern as Task 5, but with `await func(...)` instead of `func(...)`.

- [ ] **Step 1: Refactor `Go<T>(Func<T, Task>, T, GoOptions?)`**

Replace the existing method body with:

```csharp
public static void Go<T>(Func<T, Task> func, T p, GoOptions? options = null)
{
    var semaphore = options?.GetOrCreateSemaphore();

    if (semaphore is null)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await func(p);
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
                await func(p);
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

- [ ] **Step 2: Refactor remaining 7 async overloads (`Go<T1, T2>` through `Go<T1..T8>`)**

Apply the identical pattern to all remaining async overloads. Each follows the same structure as Step 1, adding more parameters to `await func(...)`. The pattern is:

```csharp
public static void Go<T1, ..., TN>(Func<T1, ..., TN, Task> func, T1 p1, ..., TN pN, GoOptions? options = null)
{
    var semaphore = options?.GetOrCreateSemaphore();

    if (semaphore is null)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await func(p1, ..., pN);
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
                await func(p1, ..., pN);
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

- [ ] **Step 3: Run tests for async generics**

Run: `dotnet test src/Concur.Tests/ --filter "FullyQualifiedName~ConcurRoutineTests" --no-restore`
Expected: All tests PASS

- [ ] **Step 4: Commit**

```bash
git add src/Concur/ConcurRoutine.cs
git commit -m "perf: inline fast/slow path for generic async Go() overloads"
```

---

### Task 7: Fast-Path — Refactor Generic Sync WaitGroup Overloads

**Files:**
- Modify: `src/Concur/ConcurRoutine.cs` — region `Generics - Sync, WaitGroup` (lines 693-978)

Same fast/slow path pattern, with `wg.Add(1)` before the branch and `wg.Done()` in both `finally` blocks.

- [ ] **Step 1: Refactor `Go<T>(WaitGroup, Action<T>, T, GoOptions?)`**

Replace the existing method body with:

```csharp
public static void Go<T>(WaitGroup wg, Action<T> func, T p, GoOptions? options = null)
{
    wg.Add(1);
    var semaphore = options?.GetOrCreateSemaphore();

    if (semaphore is null)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                func(p);
            }
            catch (Exception e)
            {
                await HandleExceptionAsync(e, GenerateRoutineId(), options);
            }
            finally
            {
                wg.Done();
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
                func(p);
            }
            catch (Exception e)
            {
                await HandleExceptionAsync(e, GenerateRoutineId(), options);
            }
            finally
            {
                wg.Done();
                semaphore.Release();
            }
        });
    }
}
```

- [ ] **Step 2: Refactor remaining 7 overloads (`Go<T1, T2>` through `Go<T1..T8>` with WaitGroup)**

Apply the identical pattern. Each follows the same structure as Step 1, adding more parameters. The pattern is:

```csharp
public static void Go<T1, ..., TN>(WaitGroup wg, Action<T1, ..., TN> func, T1 p1, ..., TN pN, GoOptions? options = null)
{
    wg.Add(1);
    var semaphore = options?.GetOrCreateSemaphore();

    if (semaphore is null)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                func(p1, ..., pN);
            }
            catch (Exception e)
            {
                await HandleExceptionAsync(e, GenerateRoutineId(), options);
            }
            finally
            {
                wg.Done();
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
                func(p1, ..., pN);
            }
            catch (Exception e)
            {
                await HandleExceptionAsync(e, GenerateRoutineId(), options);
            }
            finally
            {
                wg.Done();
                semaphore.Release();
            }
        });
    }
}
```

- [ ] **Step 3: Run tests for sync WaitGroup generics**

Run: `dotnet test src/Concur.Tests/ --filter "FullyQualifiedName~ConcurRoutineTests" --no-restore`
Expected: All tests PASS

- [ ] **Step 4: Commit**

```bash
git add src/Concur/ConcurRoutine.cs
git commit -m "perf: inline fast/slow path for generic sync WaitGroup Go() overloads"
```

---

### Task 8: Fast-Path — Refactor Generic Async WaitGroup Overloads

**Files:**
- Modify: `src/Concur/ConcurRoutine.cs` — region `Generics - Async, WaitGroup` (lines 981-1327)

Same pattern with `await func(...)`, `wg.Add(1)` before the branch, and `wg.Done()` in both `finally` blocks.

- [ ] **Step 1: Refactor `Go<T>(WaitGroup, Func<T, Task>, T, GoOptions?)`**

Replace the existing method body with:

```csharp
public static void Go<T>(WaitGroup wg, Func<T, Task> func, T p, GoOptions? options = null)
{
    wg.Add(1);
    var semaphore = options?.GetOrCreateSemaphore();

    if (semaphore is null)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await func(p);
            }
            catch (Exception e)
            {
                await HandleExceptionAsync(e, GenerateRoutineId(), options);
            }
            finally
            {
                wg.Done();
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
                await func(p);
            }
            catch (Exception e)
            {
                await HandleExceptionAsync(e, GenerateRoutineId(), options);
            }
            finally
            {
                wg.Done();
                semaphore.Release();
            }
        });
    }
}
```

- [ ] **Step 2: Refactor remaining 7 overloads (`Go<T1, T2>` through `Go<T1..T8>` async WaitGroup)**

Apply the identical pattern. Each follows the same structure as Step 1, adding more parameters. The pattern is:

```csharp
public static void Go<T1, ..., TN>(WaitGroup wg, Func<T1, ..., TN, Task> func, T1 p1, ..., TN pN, GoOptions? options = null)
{
    wg.Add(1);
    var semaphore = options?.GetOrCreateSemaphore();

    if (semaphore is null)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await func(p1, ..., pN);
            }
            catch (Exception e)
            {
                await HandleExceptionAsync(e, GenerateRoutineId(), options);
            }
            finally
            {
                wg.Done();
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
                await func(p1, ..., pN);
            }
            catch (Exception e)
            {
                await HandleExceptionAsync(e, GenerateRoutineId(), options);
            }
            finally
            {
                wg.Done();
                semaphore.Release();
            }
        });
    }
}
```

- [ ] **Step 3: Run the full test suite**

Run: `dotnet test src/Concur.Tests/ --no-restore`
Expected: All tests PASS (all existing + new WaitGroup stress tests)

- [ ] **Step 4: Commit**

```bash
git add src/Concur/ConcurRoutine.cs
git commit -m "perf: inline fast/slow path for generic async WaitGroup Go() overloads"
```

---

### Task 9: Final Validation — Full Test Suite + Build

**Files:**
- All modified files (read-only verification)

- [ ] **Step 1: Clean build**

Run: `dotnet build src/Concur.sln --configuration Release --no-restore`
Expected: Build succeeded with 0 errors, 0 warnings

- [ ] **Step 2: Run full test suite**

Run: `dotnet test src/Concur.Tests/ --configuration Release --no-restore`
Expected: All tests PASS

- [ ] **Step 3: Verify `ExecuteWithConcurrencyLimitAsync` is fully removed**

Search for any remaining references:

Run: `grep -r "ExecuteWithConcurrencyLimitAsync" src/`
Expected: No matches found

- [ ] **Step 4: Verify no `SemaphoreSlim` in WaitGroup**

Run: `grep -r "SemaphoreSlim\|semaphore" src/Concur/WaitGroup.cs`
Expected: No matches found

- [ ] **Step 5: No commit needed — validation only**
