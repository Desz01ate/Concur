using System.Collections.Concurrent;
using Concur;

namespace Concur.Tests;

public class LruCacheTests
{
    [Fact]
    public void LruCache_WithinCapacity_RetainsSemaphores()
    {
        var cache = new LruCache<SemaphoreSlim>(maxCapacity: 3);

        var sem1 = cache.GetOrAdd(1, count => new SemaphoreSlim(count, count));
        var sem2 = cache.GetOrAdd(2, count => new SemaphoreSlim(count, count));
        var sem3 = cache.GetOrAdd(3, count => new SemaphoreSlim(count, count));

        // Verify all semaphores are cached
        Assert.Same(sem1, cache.GetOrAdd(1, count => new SemaphoreSlim(count, count)));
        Assert.Same(sem2, cache.GetOrAdd(2, count => new SemaphoreSlim(count, count)));
        Assert.Same(sem3, cache.GetOrAdd(3, count => new SemaphoreSlim(count, count)));
        
        Assert.Equal(3, cache.Count);
    }

    [Fact]
    public void LruCache_ExceedsCapacity_EvictsLeastRecentlyUsed()
    {
        var cache = new LruCache<SemaphoreSlim>(maxCapacity: 2);

        var sem1 = cache.GetOrAdd(1, count => new SemaphoreSlim(count, count));
        var sem2 = cache.GetOrAdd(2, count => new SemaphoreSlim(count, count));
        
        // Access sem1 to make it most recently used
        var retrievedSem1 = cache.GetOrAdd(1, count => new SemaphoreSlim(count, count));
        Assert.Same(sem1, retrievedSem1);

        // Adding a third item should evict sem2 (least recently used)
        var sem3 = cache.GetOrAdd(3, count => new SemaphoreSlim(count, count));

        Assert.Equal(2, cache.Count);
        
        // sem1 and sem3 should still be cached
        Assert.Same(sem1, cache.GetOrAdd(1, count => new SemaphoreSlim(count, count)));
        Assert.Same(sem3, cache.GetOrAdd(3, count => new SemaphoreSlim(count, count)));
        
        // sem2 should be evicted, so we get a new instance
        var newSem2 = cache.GetOrAdd(2, count => new SemaphoreSlim(count, count));
        Assert.NotSame(sem2, newSem2);
    }

    [Fact]
    public void LruCache_AccessUpdatesOrder()
    {
        var cache = new LruCache<SemaphoreSlim>(maxCapacity: 3);

        var sem1 = cache.GetOrAdd(1, count => new SemaphoreSlim(count, count));
        var sem2 = cache.GetOrAdd(2, count => new SemaphoreSlim(count, count));
        var sem3 = cache.GetOrAdd(3, count => new SemaphoreSlim(count, count));

        // Access sem1 to move it to front
        var retrievedSem1 = cache.GetOrAdd(1, count => new SemaphoreSlim(count, count));
        Assert.Same(sem1, retrievedSem1);

        // Add fourth item, should evict sem2 (now least recently used)
        var sem4 = cache.GetOrAdd(4, count => new SemaphoreSlim(count, count));

        Assert.Equal(3, cache.Count);
        
        // sem1, sem3, and sem4 should still be cached
        Assert.Same(sem1, cache.GetOrAdd(1, count => new SemaphoreSlim(count, count)));
        Assert.Same(sem3, cache.GetOrAdd(3, count => new SemaphoreSlim(count, count)));
        Assert.Same(sem4, cache.GetOrAdd(4, count => new SemaphoreSlim(count, count)));
        
        // sem2 should be evicted
        var newSem2 = cache.GetOrAdd(2, count => new SemaphoreSlim(count, count));
        Assert.NotSame(sem2, newSem2);
    }

    [Fact]
    public void LruCache_Clear_DisposesAllSemaphores()
    {
        var cache = new LruCache<SemaphoreSlim>(maxCapacity: 3);
        var disposedSemaphores = new List<bool>();

        // Create custom semaphores that track disposal
        var sem1 = cache.GetOrAdd(1, _ => new TrackingTestSemaphore(1, disposedSemaphores));
        var sem2 = cache.GetOrAdd(2, _ => new TrackingTestSemaphore(2, disposedSemaphores));

        cache.Clear();

        Assert.Equal(0, cache.Count);
        Assert.Equal(2, disposedSemaphores.Count);
        Assert.All(disposedSemaphores, disposed => Assert.True(disposed));
    }

    [Fact]
    public void LruCache_EvictionDisposesOldSemaphores()
    {
        var cache = new LruCache<SemaphoreSlim>(maxCapacity: 1);
        var disposedFlags = new ConcurrentBag<int>();

        var sem1 = cache.GetOrAdd(1, _ => new TrackingTestSemaphore(1, disposedFlags));
        
        // Adding second semaphore should evict and dispose first one
        var sem2 = cache.GetOrAdd(2, _ => new TrackingTestSemaphore(2, disposedFlags));

        Assert.Equal(1, cache.Count);
        
        // Give a moment for disposal to complete
        Thread.Sleep(10);
        
        Assert.Single(disposedFlags);
        Assert.Contains(1, disposedFlags); // First semaphore should be disposed
    }

    [Fact]
    public async Task LruCache_ConcurrentAccess_ThreadSafe()
    {
        var cache = new LruCache<SemaphoreSlim>(maxCapacity: 10);
        var tasks = new Task[100];
        var retrievedSemaphores = new ConcurrentBag<SemaphoreSlim>();

        for (var i = 0; i < tasks.Length; i++)
        {
            var concurrencyLimit = i % 5 + 1; // Use limits 1-5
            tasks[i] = Task.Run(() =>
            {
                var semaphore = cache.GetOrAdd(concurrencyLimit, count => new SemaphoreSlim(count, count));
                retrievedSemaphores.Add(semaphore);
            });
        }

        await Task.WhenAll(tasks);

        Assert.Equal(100, retrievedSemaphores.Count);
        Assert.True(cache.Count <= 10); // Should not exceed capacity
        Assert.True(cache.Count >= 1);  // Should have at least some items
    }

    [Fact] 
    public void LruCache_ZeroCapacity_ThrowsException()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new LruCache<SemaphoreSlim>(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LruCache<SemaphoreSlim>(-1));
    }

    [Fact]
    public void ConcurrencyManager_UsesLruCache()
    {
        // Clear any existing cache state
        ConcurrencyManager.ClearAll();

        var options1 = new GoOptions { MaxConcurrency = 1 };
        var options2 = new GoOptions { MaxConcurrency = 2 };

        var sem1 = ConcurrencyManager.GetSemaphore(options1);
        var sem2 = ConcurrencyManager.GetSemaphore(options2);

        // Should get same instances from cache
        Assert.Same(sem1, ConcurrencyManager.GetSemaphore(options1));
        Assert.Same(sem2, ConcurrencyManager.GetSemaphore(options2));
    }

    [Fact]
    public void ConcurrencyManager_CacheEviction_WorksWithManyLimits()
    {
        ConcurrencyManager.ClearAll();

        // Create more semaphores than the default cache size (50)
        var semaphores = new List<SemaphoreSlim>();
        for (var i = 1; i <= 60; i++)
        {
            var options = new GoOptions { MaxConcurrency = i };
            var semaphore = ConcurrencyManager.GetSemaphore(options);
            semaphores.Add(semaphore!);
        }

        // First few semaphores should be evicted
        var firstOptions = new GoOptions { MaxConcurrency = 1 };
        var retrievedFirst = ConcurrencyManager.GetSemaphore(firstOptions);
        Assert.NotSame(semaphores[0], retrievedFirst); // Should be a new instance due to eviction
    }

    [Fact]
    public void ConcurrencyManager_CacheProperties_ReturnCorrectValues()
    {
        ConcurrencyManager.ClearAll();
        
        Assert.Equal(50, ConcurrencyManager.MaxCacheCapacity);
        Assert.Equal(0, ConcurrencyManager.CurrentCacheSize);

        // Add some items to cache
        var options = new GoOptions { MaxConcurrency = 5 };
        ConcurrencyManager.GetSemaphore(options);

        Assert.Equal(1, ConcurrencyManager.CurrentCacheSize);
    }

    private sealed class TrackingTestSemaphore : SemaphoreSlim
    {
        private readonly object disposedCollection;
        private readonly int index;

        public TrackingTestSemaphore(int index, object disposedCollection) : base(1, 1)
        {
            this.index = index;
            this.disposedCollection = disposedCollection;
            
            if (disposedCollection is List<bool> list)
            {
                list.Add(false); // Initially not disposed
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                switch (disposedCollection)
                {
                    case List<bool> list when index <= list.Count:
                        list[index - 1] = true;
                        break;
                    case ConcurrentBag<int> bag:
                        bag.Add(index);
                        break;
                }
            }
            base.Dispose(disposing);
        }
    }
}