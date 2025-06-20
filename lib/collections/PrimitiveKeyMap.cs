﻿// Copyright 2025 Chikirev Sirguy, Unirail Group
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
//
// For inquiries, please contact: al8v5C6HU4UtqE9@gmail.com
// GitHub Repository: https://github.com/AdHoc-Protocol

using System.Collections;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace org.unirail.collections;

/// <summary>
/// Represents a collection of keys and values, where keys are unique primitive values of type <typeparamref name="K"/>.
/// This map implementation uses a hash table and is optimized for primitive keys.
/// </summary>
/// <typeparam name="K">The type of the primitive keys in the map. Must be an unmanaged type.</typeparam>
/// <typeparam name="V">The type of the values in the map.</typeparam>
[DebuggerDisplay("Count = {Count}")]
public class PrimitiveKeyMap<K, V> : IDictionary<K, V>, IReadOnlyDictionary<K, V> where K : unmanaged{
    private struct Entry{
        public K key;
        public V value;
    }

    private uint[]?  _buckets;
    private Entry[]? _entries;
    private uint[]?  _links; // 0-based indices into _entries for collision chains.

    // The _entries array is split into two regions to optimize for non-colliding entries:
    // 1. Lo-Region (indices 0 to _loSize-1): Stores entries that have caused a hash collision.
    // 2. Hi-Region (indices _entries.Length - _hiSize to _entries.Length-1): Stores entries that have not caused a collision.
    private int _hiSize; // Number of active entries in the high region.
    private int _loSize; // Number of active entries in the low region.

    private int  _version;
    private uint _mask;

    private KeysCollection?   _keys;
    private ValuesCollection? _values;

    private const int DefaultCapacity = 4;

    public PrimitiveKeyMap() : this(0) { }

    public PrimitiveKeyMap(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        if( capacity > 0 ) Initialize(capacity);
    }

    public PrimitiveKeyMap(IEnumerable<KeyValuePair<K, V>> collection) : this(collection is ICollection<KeyValuePair<K, V>> coll ?
                                                                                  coll.Count :
                                                                                  0)
    {
        ArgumentNullException.ThrowIfNull(collection);
        AddRange(collection);
    }

    public PrimitiveKeyMap(IDictionary<K, V> dictionary) : this(dictionary?.Count ?? 0)
    {
        ArgumentNullException.ThrowIfNull(dictionary);
        AddRange(dictionary);
    }

    private void AddRange(IEnumerable<KeyValuePair<K, V>> enumerable)
    {
        foreach( var pair in enumerable ) Add(pair.Key, pair.Value);
    }

    public int Count => _loSize + _hiSize;

    public bool IsReadOnly => false;

    public V this[K key]
    {
        get
        {
            if( TryGetValue(key, out var value) ) return value;
            throw new KeyNotFoundException("The given key was not present in the map.");
        }
        set => TryInsert(key, value, true);
    }

    public void Add(K key, V value)
    {
        if( !TryInsert(key, value, false) ) throw new ArgumentException("An item with the same key has already been added.");
    }

    void ICollection<KeyValuePair<K, V>>.Add(KeyValuePair<K, V> item) => TryInsert(item.Key, item.Value, true);

    private bool TryInsert(K key, V value, bool overwrite)
    {
        if( _buckets              == null ) Initialize(DefaultCapacity);
        else if( _entries!.Length <= _loSize + _hiSize ) Resize(_entries.Length * 2);

        ref var bucket = ref GetBucket((uint)key.GetHashCode());

        int dstIndex;
        if( bucket == 0 ) dstIndex = _entries!.Length - 1 - _hiSize++; // Bucket is empty: Place new entry in the Hi-Region (non-colliding entries).
        else                                                           //Collision detected (bucket is not empty).
        {
            // Traverse a collision chain to check for an existing key or find insertion point.
            for( uint i = bucket - 1, collisions = 0;; )
            {
                ref var entry = ref _entries![i];
                if( EqualityComparer<K>.Default.Equals(entry.key, key) )
                {
                    // Key found: Overwrite if allowed, otherwise return false (add failed).
                    if( !overwrite ) return false;
                    entry.value = value;
                    _version++;   // Update version as value changed.
                    return false; // Return false as no *new* entry was added (it was an update).
                }

                if( _loSize <= i ) break; //the key is not found, jump to the adding the new entry in the collided chain (Lo-Region)

                i = _links![i];
                if( _loSize + 1 < collisions++ ) throw new InvalidOperationException("Concurrent operations not supported.");
            }

            // Key not found in chain: Add new entry to the Lo-Region (colliding entries).
            if( _links!.Length == (dstIndex = _loSize++) ) Array.Resize(ref _links, Math.Max(16, Math.Min(_buckets!.Length, _links.Length * 2)));
            _links[dstIndex] = bucket - 1; // New entry links to the previous head of the chain.
        }

        ref var newEntry = ref _entries[dstIndex];
        newEntry.key   = key;
        newEntry.value = value;

        bucket = (uint)(dstIndex + 1);
        _version++;  // Update version for successful addition.
        return true; // Item successfully added.
    }

    public ICollection<K> Keys   => _keys ??= new KeysCollection(this);
    public ICollection<V> Values => _values ??= new ValuesCollection(this);

    IEnumerable<K> IReadOnlyDictionary<K, V>.Keys   => Keys;
    IEnumerable<V> IReadOnlyDictionary<K, V>.Values => Values;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ref uint GetBucket(uint hashCode) => ref _buckets![hashCode & _mask];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int Initialize(int capacity)
    {
        _version++;
        var size = GetPowerOfTwo(capacity);
        _buckets = new uint[size];
        _entries = new Entry[size];
        _links   = new uint[Math.Min(size, 16)];
        _loSize  = _hiSize = 0;
        _mask    = (uint)(size - 1);
        return size;
    }


    public void Clear()
    {
        if( Count == 0 ) return;
        _version++;
        if( _buckets != null ) Array.Clear(_buckets, 0, _buckets.Length);
        _loSize = 0;
        _hiSize = 0;
    }


    public bool ContainsKey(K key)
    {
        if( _loSize + _hiSize == 0 ) return false;

        var i = (int)GetBucket((uint)key.GetHashCode()) - 1;
        if( i == -1 ) return false;

        for( var collisions = 0;; )
        {
            if( EqualityComparer<K>.Default.Equals(_entries![i].key, key) ) return true;

            if( _loSize <= i ) return false;

            i = (int)_links![i];
            if( _loSize < ++collisions ) throw new InvalidOperationException("Hash collision chain is unexpectedly long. Possible data corruption.");
        }
    }

    public bool ContainsValue(V value)
    {
        // Iterate lo-region
        for( var i = 0; i < _loSize; i++ )
            if( EqualityComparer<V>.Default.Equals(_entries![i].value, value) )
                return true;
        if( _hiSize == 0 ) return false;

        // Iterate hi-region
        for( var i = _entries!.Length - _hiSize; i < _entries.Length; i++ )
            if( EqualityComparer<V>.Default.Equals(_entries![i].value, value) )
                return true;

        return false;
    }

    public bool TryGetValue(K key, [MaybeNullWhen(false)] out V value)
    {
        if( _loSize + _hiSize == 0 )
        {
            value = default;
            return false;
        }

        var i = (int)GetBucket((uint)key.GetHashCode()) - 1;
        if( i == -1 )
        {
            value = default;
            return false;
        }

        for( var collisions = 0;; )
        {
            ref var entry = ref _entries![i];
            if( EqualityComparer<K>.Default.Equals(entry.key, key) )
            {
                value = entry.value;
                return true;
            }

            if( _loSize <= i )
            {
                value = default;
                return false;
            }

            i = (int)_links![i];
            if( _loSize < ++collisions ) throw new InvalidOperationException("Hash collision chain is unexpectedly long. Possible data corruption.");
        }
    }


    private void Resize(int newSize)
    {
        _version++;
        var oldEntries                                                       = _entries;
        var oldLoSize                                                        = _loSize;
        var oldHiSize                                                        = _hiSize;
        if( _links.Length < 0xFF && _links.Length < _buckets.Length ) _links = _buckets;//reuse buckets as links
        Initialize(newSize);

        for( var i = 0; i                              < oldLoSize; i++ ) Copy(in oldEntries![i]);
        for( var i = oldEntries!.Length - oldHiSize; i < oldEntries.Length; i++ ) Copy(in oldEntries[i]);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Copy(in Entry entry)
    {
        ref var bucket = ref GetBucket((uint)entry.key.GetHashCode());
        var     i      = (int)bucket - 1;
        int     dstIndex;

        if( i == -1 ) // Empty bucket, insert into hi-region
            dstIndex = _entries!.Length - 1 - _hiSize++;
        else // Collision, insert into lo-region
        {
            if( _links!.Length == (dstIndex = _loSize++) ) Array.Resize(ref _links, Math.Max(16, Math.Min(_buckets!.Length, _links.Length * 2)));
            _links[dstIndex] = (uint)i;
        }

        _entries![dstIndex] = entry;
        bucket              = (uint)(dstIndex + 1);
    }


    // Moves data from src index to dst index and updates all links pointing to src.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Move(int src, int dst, ref Entry dstEntry)
    {
        if( src == dst ) return;

        ref var srcEntry  = ref _entries![src];
        ref var srcBucket = ref GetBucket((uint)srcEntry.key.GetHashCode());
        var     index     = (int)srcBucket - 1;

        if( index == src ) // The bucket points to it
            srcBucket = (uint)(dst + 1);
        else // A link points to it. Find that link.
        {
            ref var link = ref _links![index];
            for( ; link != src; link = ref _links![index] )
                index = (int)link;

            link = (uint)dst;
        }

        if( src < _loSize ) _links![dst] = _links![src];

        dstEntry = srcEntry;
    }

    public bool Remove(K key)
    {
        if( _loSize + _hiSize == 0 ) return false;

        ref var bucket      = ref GetBucket((uint)key.GetHashCode());
        var     removeIndex = (int)bucket - 1;
        if( removeIndex == -1 ) return false;

        ref var removeEntry = ref _entries![removeIndex];

        if( _loSize <= removeIndex ) // In hi-region (no collision chain)
        {
            if( !EqualityComparer<K>.Default.Equals(removeEntry.key, key) ) return false;

            // Action: Remove the Hi-Region item.
            // To avoid a "hole" in the Hi-Region, the *lowest* item in the Hi-Region is moved into the position of the item being removed.
            Move(_entries.Length - _hiSize, removeIndex, ref removeEntry);
            _hiSize--;
            bucket = 0; // The bucket pointed to the hi-item directly and is now empty.
            _version++;
            return true;
        }

        ref var link = ref _links![removeIndex];

        // Item is in lo-region, or is the head of a chain.
        if( EqualityComparer<K>.Default.Equals(removeEntry.key, key) ) // The item to be removed is the head of the collision chain.
            bucket = link + 1;                                         //Bucket now points to the next item, bypassing the removed head.
        else
        {
            var     last      = removeIndex;     // Index of the previous node in the chain.
            ref var lastEntry = ref removeEntry; // Ref to the previous node's data.

            if( EqualityComparer<K>.Default.Equals((removeEntry = ref _entries![removeIndex = (int)link]).key, key) ) // Found the key in the *second* node of the chain.
                if( removeIndex < _loSize )                                                                           // this 'SecondNode' is not the terminal entry (in lo-region),
                    link = _links![removeIndex];                                                                      //  relink to bypass it. `link` is a ref to _links[last], so this modifies the previous link.
                else                                                                                                  // 'SecondNode' is the terminal entry (in hi-region (end of chain))
                {
                    //the head of the chain lost link and become single,
                    removeEntry = lastEntry;               //  so move it to the Hi-Region, to the place of removed tail
                    bucket      = (uint)(removeIndex + 1); // Bucket updated to point to the new Hi-Region location.

                    removeEntry = ref lastEntry; //and switch cleanup to the old head place
                    removeIndex = last;
                }
            else if( _loSize <= removeIndex ) return false; // The second node is the terminal entry (in hi-region) but didn't match. So, the key is not in the map.
            else
                for( var collisions = 0;; ) // Continue traversing the lo-region chain.
                {
                    lastEntry = ref removeEntry;
                    ref var prevLink = ref link;

                    if( EqualityComparer<K>.Default.Equals((removeEntry = ref _entries![removeIndex = (int)(link = ref _links![last = removeIndex])]).key, key) )
                    {
                        if( removeIndex < _loSize )     // Found in lo-region.
                            link = _links[removeIndex]; //  relink to bypass it.
                        else
                        {
                            //found entry to remove is the `terminal entry` (in the Hi-Region).So, previous entry now became the `terminal entry` and move to vacant place in the `Hi-Region`.
                            removeEntry = lastEntry;
                            prevLink    = (uint)removeIndex; //  relink prev entry to the new  `terminal entry` in the `Hi-Region`.

                            removeEntry = ref lastEntry; //and switch cleanup to the vacant place
                            removeIndex = last;
                        }

                        break;
                    }

                    if( _loSize     <= removeIndex ) return false; // Reached Hi-Region (end of chain) without finding key.
                    if( _loSize + 1 < collisions++ ) throw new InvalidOperationException("Concurrent operations not supported.");
                }
        }

        // To avoid a "hole" in the Lo-Region, the *highest* item in the Lo-Region is moved into the position of the item being removed.
        Move(_loSize - 1, removeIndex, ref removeEntry);
        _loSize--;
        _version++;
        return true;
    }

    bool ICollection<KeyValuePair<K, V>>.Remove(KeyValuePair<K, V> item) => TryGetValue(item.Key, out var value) && EqualityComparer<V>.Default.Equals(value, item.Value) && Remove(item.Key);

    bool ICollection<KeyValuePair<K, V>>.Contains(KeyValuePair<K, V> item) { return TryGetValue(item.Key, out var value) && EqualityComparer<V>.Default.Equals(value, item.Value); }


    void ICollection<KeyValuePair<K, V>>.CopyTo(KeyValuePair<K, V>[] dst, int dstIndex)
    {
        ArgumentNullException.ThrowIfNull(dst);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)dstIndex, (uint)dst.Length);
        if( dst.Length - dstIndex < Count ) throw new ArgumentException("Destination array is not long enough.");

        // Copy lo-region
        for( var i = 0; i < _loSize; i++ )
        {
            ref var entry = ref _entries![i];
            dst[dstIndex++] = new KeyValuePair<K, V>(entry.key, entry.value);
        }

        if( _hiSize == 0 ) return;

        // Copy hi-region
        for( var i = _entries!.Length - _hiSize; i < _entries.Length; i++ )
        {
            ref var entry = ref _entries[i];
            dst[dstIndex++] = new KeyValuePair<K, V>(entry.key, entry.value);
        }
    }


    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int GetPowerOfTwo(int capacity) => capacity <= DefaultCapacity ?
                                                          DefaultCapacity :
                                                          (int)BitOperations.RoundUpToPowerOf2((uint)capacity);

    public Enumerator                                               GetEnumerator() => new(this);
    IEnumerator<KeyValuePair<K, V>> IEnumerable<KeyValuePair<K, V>>.GetEnumerator() => GetEnumerator();
    IEnumerator IEnumerable.                                        GetEnumerator() => GetEnumerator();

    public struct Enumerator : IEnumerator<KeyValuePair<K, V>>
    {
        private readonly PrimitiveKeyMap<K, V> _map;
        private          int                   _version;
        private          int                   _index; // -1=before iteration, 0 to _loSize-1=lo-region, _entries.Length - _hiSize to _entries.Length-1=hi-region, int.MaxValue-1=finished
        private          KeyValuePair<K, V>    _current;

        internal Enumerator(PrimitiveKeyMap<K, V> map)
        {
            _map     = map;
            _version = map._version;
            _index   = -1;
            _current = default;
        }

        public bool MoveNext()
        {
            if (_version   != _map._version) throw new InvalidOperationException("Collection was modified during enumeration.");
            if (_map.Count == 0) return false;

            if (++_index == int.MaxValue)
            {
                _index = int.MaxValue - 1;
                return false;
            }

            if (_index == _map._loSize)
            {
                if (_map._hiSize == 0)
                {
                    _index = int.MaxValue - 1;
                    return false;
                }
                _index = _map._entries!.Length - _map._hiSize; // Jump to hi-region
            }

            if (_index == _map._entries!.Length)
            {
                _index = int.MaxValue - 1;
                return false;
            }

            ref var entry = ref _map._entries[_index];
            _current = new KeyValuePair<K, V>(entry.key, entry.value);
            return true;
        }

        public KeyValuePair<K, V> Current => _version != _map._version ?
                                                 throw new InvalidOperationException("Collection was modified during enumeration.") :
                                                 _index == -1 || _index == int.MaxValue - 1 ?
                                                     throw new InvalidOperationException("Enumeration has either not started or has finished.") :
                                                     _current;

        object? IEnumerator.Current => Current;

        public void Reset()
        {
            _version = _map._version;
            _index   = -1;
            _current = default;
        }

        public void Dispose() { }
    }

    public int EnsureCapacity(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        var currentCapacity = _entries?.Length ?? 0;
        if( currentCapacity >= capacity ) return currentCapacity;
        _version++;
        if( _buckets == null ) return Initialize(capacity);
        var newSize = GetPowerOfTwo(capacity);
        Resize(newSize);
        return newSize;
    }

    public void TrimExcess()
    {
        var count = Count;
        if( count == 0 )
        {
            Initialize(0);
            return;
        }

        if( _entries == null ) return;
        var newSize = GetPowerOfTwo(count);
        if( newSize < _entries.Length ) Resize(newSize);
    }

#region Helper Collections
    [DebuggerDisplay("Count = {Count}")]
    public sealed class KeysCollection : ICollection<K>, IReadOnlyCollection<K>{
        private readonly PrimitiveKeyMap<K, V> map;
        public KeysCollection(PrimitiveKeyMap<K, V> map) => this.map = map;
        public int  Count      => map.Count;
        public bool IsReadOnly => true;

        public void CopyTo(K[] dst, int dstIndex)
        {
            ArgumentNullException.ThrowIfNull(dst);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)dstIndex, (uint)dst.Length);
            if( dst.Length - dstIndex < Count ) throw new ArgumentException("Destination array is not long enough.");

            foreach( var item in map ) dst[dstIndex++] = item.Key;
        }

        public bool                   Contains(K item) => map.ContainsKey(item);
        public Enumerator             GetEnumerator()  => new(map);
        IEnumerator<K> IEnumerable<K>.GetEnumerator()  => GetEnumerator();
        IEnumerator IEnumerable.      GetEnumerator()  => GetEnumerator();
        void ICollection<K>.          Add(K    item)   => throw new NotSupportedException();
        bool ICollection<K>.          Remove(K item)   => throw new NotSupportedException();
        void ICollection<K>.          Clear()          => throw new NotSupportedException();

        public struct Enumerator : IEnumerator<K>{
            private PrimitiveKeyMap<K, V>.Enumerator _dictEnumerator;
            internal Enumerator(PrimitiveKeyMap<K, V> map) => _dictEnumerator = map.GetEnumerator();
            public bool         MoveNext() => _dictEnumerator.MoveNext();
            public K            Current    => _dictEnumerator.Current.Key;
            object? IEnumerator.Current    => Current;
            public void         Dispose()  => _dictEnumerator.Dispose();
            public void         Reset()    => _dictEnumerator.Reset();
        }
    }

    [DebuggerDisplay("Count = {Count}")]
    public sealed class ValuesCollection : ICollection<V>, IReadOnlyCollection<V>{
        private readonly PrimitiveKeyMap<K, V> map;
        public ValuesCollection(PrimitiveKeyMap<K, V> map) => this.map = map;
        public int  Count      => map.Count;
        public bool IsReadOnly => true;

        public void CopyTo(V[] dst, int dstIndex)
        {
            ArgumentNullException.ThrowIfNull(dst);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)dstIndex, (uint)dst.Length);
            if( dst.Length - dstIndex < Count ) throw new ArgumentException("Destination array is not long enough.");

            foreach( var item in map ) dst[dstIndex++] = item.Value;
        }

        public bool Contains(V item) => map.ContainsValue(item);

        public Enumerator             GetEnumerator() => new(map);
        IEnumerator<V> IEnumerable<V>.GetEnumerator() => GetEnumerator();
        IEnumerator IEnumerable.      GetEnumerator() => GetEnumerator();
        void ICollection<V>.          Add(V    item)  => throw new NotSupportedException();
        bool ICollection<V>.          Remove(V item)  => throw new NotSupportedException();
        void ICollection<V>.          Clear()         => throw new NotSupportedException();

        public struct Enumerator : IEnumerator<V>{
            private PrimitiveKeyMap<K, V>.Enumerator _dictEnumerator;
            internal Enumerator(PrimitiveKeyMap<K, V> map) => _dictEnumerator = map.GetEnumerator();
            public bool         MoveNext() => _dictEnumerator.MoveNext();
            public V            Current    => _dictEnumerator.Current.Value;
            object? IEnumerator.Current    => Current;
            public void         Dispose()  => _dictEnumerator.Dispose();
            public void         Reset()    => _dictEnumerator.Reset();
        }
    }
#endregion
}