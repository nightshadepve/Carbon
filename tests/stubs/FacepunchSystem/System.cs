using System.Collections;
using System.Collections.Generic;

public class ListHashSet<T> : IEnumerable<T>
{
    public bool Contains(T item) { return false; }
    public IEnumerator<T> GetEnumerator() { yield break; }
    IEnumerator IEnumerable.GetEnumerator() { return GetEnumerator(); }
}

public class BufferList<T> : IEnumerable<T>
{
    public IEnumerator<T> GetEnumerator() { return new Enumerator(); }
    IEnumerator IEnumerable.GetEnumerator() { return GetEnumerator(); }

    public struct Enumerator : IEnumerator<T>
    {
        public T Current { get { return default(T); } }
        object IEnumerator.Current { get { return Current; } }
        public bool MoveNext() { return false; }
        public void Reset() { }
        public void Dispose() { }
    }
}

namespace Facepunch
{
    public struct StringView
    {
        public static implicit operator string(StringView value) { return string.Empty; }
        public static implicit operator StringView(string value) { return default(StringView); }
    }
}
