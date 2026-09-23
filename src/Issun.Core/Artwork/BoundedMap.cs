namespace Issun.Core.Artwork;

/// <summary>
/// A dictionary that forgets its least recently used entries past a fixed
/// size. relay.py's _artwork_cache, _links_cache and _unresolved_seen grew for
/// as long as the process lived, which was tolerable for a script restarted
/// with the PC; Issun sits in the tray for months. Forgetting an old entry
/// costs one repeat lookup if that track ever plays again.
///
/// Not thread-safe: <see cref="ArtworkResolver"/> guards every use with its
/// own lock.
/// </summary>
internal sealed class BoundedMap<TKey, TValue>(int capacity) where TKey : notnull
{
    private readonly Dictionary<TKey, LinkedListNode<(TKey Key, TValue Value)>> _index = new();
    private readonly LinkedList<(TKey Key, TValue Value)> _order = new();

    public int Count => _index.Count;

    public bool TryGet(TKey key, out TValue value)
    {
        if (_index.TryGetValue(key, out var node))
        {
            _order.Remove(node);
            _order.AddFirst(node);
            value = node.Value.Value;
            return true;
        }
        value = default!;
        return false;
    }

    /// <summary>Reads without counting as a use — for observers that shouldn't keep an entry alive.</summary>
    public bool Peek(TKey key, out TValue value)
    {
        if (_index.TryGetValue(key, out var node))
        {
            value = node.Value.Value;
            return true;
        }
        value = default!;
        return false;
    }

    public void Set(TKey key, TValue value)
    {
        if (_index.TryGetValue(key, out var existing))
        {
            _order.Remove(existing);
            _index.Remove(key);
        }
        _index[key] = _order.AddFirst((key, value));
        while (_index.Count > capacity && _order.Last is { } oldest)
        {
            _order.RemoveLast();
            _index.Remove(oldest.Value.Key);
        }
    }

    /// <summary>Adds <paramref name="key"/> if absent. True when it was added — "first time seen".</summary>
    public bool Add(TKey key, TValue value)
    {
        if (TryGet(key, out _))
            return false;
        Set(key, value);
        return true;
    }
}
