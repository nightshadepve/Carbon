// REFERENCE SHIM. Facepunch.System.dll is not in this repository.
// BasePlayer.activePlayerList is a ListHashSet. The real collection is on the server.

using System.Collections;
using System.Collections.Generic;

public class ListHashSet<T> : IEnumerable<T>
{
    public IEnumerator<T> GetEnumerator()
    {
        yield break;
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }
}
