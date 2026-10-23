namespace EtAlii.Lark.Utilities;

using System.Collections;

/// <summary>
/// A set that remembers insertion order. Used wherever upstream Lark relies on ordered sets
/// (Python dicts) to keep the output of the parsers deterministic.
/// </summary>
internal sealed class OrderedSet<T> : IReadOnlyCollection<T> where T : notnull
{
    private readonly Dictionary<T, LinkedListNode<T>> _index;
    private readonly LinkedList<T> _items = new();

    public OrderedSet() : this((IEqualityComparer<T>?)null) { }

    public OrderedSet(IEqualityComparer<T>? comparer)
    {
        _index = new Dictionary<T, LinkedListNode<T>>(comparer);
    }

    public OrderedSet(IEnumerable<T> items, IEqualityComparer<T>? comparer = null) : this(comparer)
    {
        foreach (var item in items) Add(item);
    }

    public int Count => _index.Count;

    public bool Add(T item)
    {
        if (_index.ContainsKey(item)) return false;
        _index[item] = _items.AddLast(item);
        return true;
    }

    public void AddRange(IEnumerable<T> items)
    {
        foreach (var item in items) Add(item);
    }

    public bool Contains(T item) => _index.ContainsKey(item);

    public bool Remove(T item)
    {
        if (!_index.Remove(item, out var node)) return false;
        _items.Remove(node);
        return true;
    }

    public void Clear()
    {
        _index.Clear();
        _items.Clear();
    }

    public IEnumerator<T> GetEnumerator() => _items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
