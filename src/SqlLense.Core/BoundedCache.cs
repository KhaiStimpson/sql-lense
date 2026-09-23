using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace SqlLense
{
    /// <summary>
    /// A concurrent memoization cache with a hard size cap. When the cap is exceeded the cache is
    /// simply cleared: SQL strings in a solution are a small, stable working set, so a full reset is
    /// rare and far cheaper than tracking recency on every hit.
    /// </summary>
    public sealed class BoundedCache<TKey, TValue>
        where TKey : notnull
    {
        private readonly int _capacity;
        private readonly ConcurrentDictionary<TKey, TValue> _items;

        public BoundedCache(int capacity, IEqualityComparer<TKey>? comparer = null)
        {
            _capacity = capacity;
            _items = new ConcurrentDictionary<TKey, TValue>(comparer ?? EqualityComparer<TKey>.Default);
        }

        public int Count => _items.Count;

        public TValue GetOrAdd(TKey key, Func<TKey, TValue> factory)
        {
            if (_items.TryGetValue(key, out var existing))
            {
                return existing;
            }

            var value = factory(key);
            if (_items.Count >= _capacity)
            {
                _items.Clear();
            }

            return _items.GetOrAdd(key, value);
        }

        public void Clear() => _items.Clear();
    }
}
