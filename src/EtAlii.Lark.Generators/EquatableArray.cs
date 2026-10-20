using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace EtAlii.Lark.Generators
{
    /// <summary>An immutable array with value equality, so that incremental generator models cache well.</summary>
    internal readonly struct EquatableArray<T> : IEquatable<EquatableArray<T>>, IReadOnlyList<T>
        where T : IEquatable<T>
    {
        private readonly T[]? _items;

        public EquatableArray(IEnumerable<T> items)
        {
            _items = items.ToArray();
        }

        public static EquatableArray<T> Empty => new(Array.Empty<T>());

        public int Count => _items?.Length ?? 0;

        public T this[int index] => _items![index];

        public bool Equals(EquatableArray<T> other) => (_items ?? Array.Empty<T>()).SequenceEqual(other._items ?? Array.Empty<T>());

        public override bool Equals(object? obj) => obj is EquatableArray<T> other && Equals(other);

        public override int GetHashCode()
        {
            var hash = 17;
            foreach (var item in _items ?? Array.Empty<T>())
            {
                hash = unchecked(hash * 31 + (item?.GetHashCode() ?? 0));
            }
            return hash;
        }

        public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)(_items ?? Array.Empty<T>())).GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
