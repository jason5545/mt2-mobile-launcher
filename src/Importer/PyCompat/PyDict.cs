using System.Collections;
using System.Numerics;

namespace Mt2Mobile.Importer.PyCompat;

/// <summary>
/// Python 的 dict：照插入順序；鍵的相等照 Python（1 == 1.0 == True，字串照 code point）。
/// 值的型別跟 Python 對應：None → null、bool、int → long 或 BigInteger、float → double、str → string、
/// list → List&lt;object?&gt;、dict → PyDict。
/// </summary>
public sealed class PyDict : IEnumerable<KeyValuePair<object?, object?>>
{
    private readonly OrderedDictionary<object, object?> _items;

    public PyDict() => _items = new OrderedDictionary<object, object?>(PyKeyComparer.Instance);

    public PyDict(int capacity) => _items = new OrderedDictionary<object, object?>(capacity, PyKeyComparer.Instance);

    public PyDict(IEnumerable<KeyValuePair<object?, object?>> pairs) : this()
    {
        foreach (var (k, v) in pairs)
            this[k] = v;
    }

    public int Count => _items.Count;

    public object? this[object? key]
    {
        get => _items.TryGetValue(Wrap(key), out var v) ? v : throw new KeyNotFoundException(Py.Repr(key));
        set => _items[Wrap(key)] = value;
    }

    public bool ContainsKey(object? key) => _items.ContainsKey(Wrap(key));

    public bool TryGetValue(object? key, out object? value) => _items.TryGetValue(Wrap(key), out value);

    /// <summary>dict.get(key)：沒有就回 null</summary>
    public object? Get(object? key) => _items.TryGetValue(Wrap(key), out var v) ? v : null;

    public object? Get(object? key, object? fallback) => _items.TryGetValue(Wrap(key), out var v) ? v : fallback;

    public bool Remove(object? key) => _items.Remove(Wrap(key));

    /// <summary>dict.pop(key, None)</summary>
    public object? Pop(object? key)
    {
        var k = Wrap(key);
        if (!_items.TryGetValue(k, out var v)) return null;
        _items.Remove(k);
        return v;
    }

    /// <summary>dict.setdefault(key, value)</summary>
    public object? SetDefault(object? key, object? value)
    {
        var k = Wrap(key);
        if (_items.TryGetValue(k, out var v)) return v;
        _items[k] = value;
        return value;
    }

    public IEnumerable<object?> Keys
    {
        get { foreach (var k in _items.Keys) yield return Unwrap(k); }
    }

    public IEnumerable<object?> Values => _items.Values;

    public IEnumerator<KeyValuePair<object?, object?>> GetEnumerator()
    {
        foreach (var (k, v) in _items)
            yield return new KeyValuePair<object?, object?>(Unwrap(k), v);
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>鍵都是字串時的便利寫法</summary>
    public IEnumerable<string> StringKeys
    {
        get { foreach (var k in _items.Keys) yield return (string)k; }
    }

    public PyDict Copy()
    {
        var d = new PyDict(Count);
        foreach (var (k, v) in _items) d._items[k] = v;
        return d;
    }

    /// <summary>dict.update(other)</summary>
    public void Update(PyDict other)
    {
        foreach (var (k, v) in other._items) _items[k] = v;
    }

    private static object Wrap(object? key) => key ?? NoneKey.Instance;

    private static object? Unwrap(object key) => key is NoneKey ? null : key;

    private sealed class NoneKey
    {
        public static readonly NoneKey Instance = new();
    }

    /// <summary>Python 的 hash／== 規則：數值類（bool、int、float）彼此用數值比，字串照 code unit 相等</summary>
    private sealed class PyKeyComparer : IEqualityComparer<object>
    {
        public static readonly PyKeyComparer Instance = new();

        public new bool Equals(object? x, object? y)
        {
            if (ReferenceEquals(x, y)) return true;
            if (x is string sx) return y is string sy && string.Equals(sx, sy, StringComparison.Ordinal);
            if (y is string) return false;
            if (Py.IsNumber(x) && Py.IsNumber(y)) return Py.NumEquals(x!, y!);
            return object.Equals(x, y);
        }

        public int GetHashCode(object obj)
        {
            switch (obj)
            {
                case string s: return StringComparer.Ordinal.GetHashCode(s);
                case bool b: return (b ? 1L : 0L).GetHashCode();
                case long l: return l.GetHashCode();
                case BigInteger bi: return bi >= long.MinValue && bi <= long.MaxValue ? ((long)bi).GetHashCode() : bi.GetHashCode();
                case double d:
                    if (Math.Floor(d) == d && d >= -9.2e18 && d <= 9.2e18) return ((long)d).GetHashCode();
                    return d.GetHashCode();
                default: return obj.GetHashCode();
            }
        }
    }
}
