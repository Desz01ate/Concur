using System.Collections.Concurrent;

namespace Concur;

/// <summary>
/// A thread-safe LRU cache for a <see cref="T"/> instances with automatic disposal of evicted items.
/// </summary>
internal sealed class LruCache<T>
{
    private readonly int maxCapacity;
    private readonly ConcurrentDictionary<int, CacheNode> cache = new();
    private readonly object lockObject = new();
    private CacheNode? head;
    private CacheNode? tail;

    /// <summary>
    /// Initializes a new instance of the LRU cache with the specified maximum capacity.
    /// </summary>
    /// <param name="maxCapacity">The maximum number of items to store in the cache.</param>
    public LruCache(int maxCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxCapacity, nameof(maxCapacity));

        this.maxCapacity = maxCapacity;
    }

    /// <summary>
    /// Gets or creates a node for the specified concurrency limit.
    /// </summary>
    /// <param name="maxConcurrency">The maximum concurrency limit.</param>
    /// <param name="factory">Factory function to create a new node if not found in cache.</param>
    /// <returns>A node with the specified limit.</returns>
    public T GetOrAdd(int maxConcurrency, Func<int, T> factory)
    {
        // A single lock to ensure atomicity of the get-and-move operation.
        lock (this.lockObject)
        {
            if (this.cache.TryGetValue(maxConcurrency, out var existingNode))
            {
                this.MoveToHead(existingNode);
                return existingNode.Value;
            }
        }

        var node = factory(maxConcurrency);
        var newNode = new CacheNode(maxConcurrency, node);

        lock (this.lockObject)
        {
            // Double-check is necessary.
            if (this.cache.TryGetValue(maxConcurrency, out var existingNode))
            {
                this.MoveToHead(existingNode);

                // Dispose the newly created but unused item.
                if (node is IDisposable disposable)
                {
                    disposable.Dispose();
                }

                return existingNode.Value;
            }

            this.cache[maxConcurrency] = newNode;
            this.AddToHead(newNode);

            if (this.cache.Count > this.maxCapacity)
            {
                this.EvictLeastRecentlyUsed();
            }
        }

        return node;
    }

    /// <summary>
    /// Removes and disposes all cached nodes.
    /// </summary>
    public void Clear()
    {
        lock (this.lockObject)
        {
            foreach (var node in this.cache.Values)
            {
                if (node.Value is IDisposable disposable)
                {
                    disposable.Dispose();
                }
            }

            this.cache.Clear();
            this.head = null;
            this.tail = null;
        }
    }

    /// <summary>
    /// Gets the current number of items in the cache.
    /// </summary>
    public int Count => this.cache.Count;

    private void MoveToHead(CacheNode node)
    {
        lock (this.lockObject)
        {
            if (node == this.head)
            {
                return;
            }

            this.RemoveNode(node);
            this.AddToHead(node);
        }
    }

    private void AddToHead(CacheNode node)
    {
        node.Next = this.head;
        node.Prev = null;

        if (this.head != null)
        {
            this.head.Prev = node;
        }

        this.head = node;

        this.tail ??= node;
    }

    private void RemoveNode(CacheNode node)
    {
        if (node.Prev != null)
        {
            node.Prev.Next = node.Next;
        }
        else
        {
            this.head = node.Next;
        }

        if (node.Next != null)
        {
            node.Next.Prev = node.Prev;
        }
        else
        {
            this.tail = node.Prev;
        }
    }

    private void EvictLeastRecentlyUsed()
    {
        if (this.tail == null) return;

        var evictedNode = this.tail;
        this.cache.TryRemove(evictedNode.Key, out _);
        this.RemoveNode(evictedNode);

        if (evictedNode.Value is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    private sealed class CacheNode(int key, T value)
    {
        public int Key { get; } = key;

        public T Value { get; } = value;

        public CacheNode? Prev { get; set; }

        public CacheNode? Next { get; set; }
    }
}