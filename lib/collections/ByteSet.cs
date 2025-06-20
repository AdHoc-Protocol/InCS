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

using System;
using System.Collections;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Text;

namespace org.unirail.collections;

/// <summary>
/// A high-performance struct for storing a set of one-byte keys (byte or sbyte) using a Vector256<ulong> to represent
/// 256 bits (keys 0-255). Optimized for SIMD operations, minimal allocations, and safe code.
/// The `_count` field tracks the number of elements, with special values indicating state.
/// <para>
/// This set is designed to hold up to 256 unique 1-byte values (0-255).
/// </para>
/// <para>
/// Check <see cref="IsValid"/> once before start using the ByteSet (as you do check not is null with reference type).
/// This is crucial for structs that can have an uninitialized default state.
/// </para>
/// </summary>
/// <typeparam name="K">Key type, restricted to a 1-byte unmanaged type (e.g., byte or sbyte).
/// This is enforced by a static constructor check.</typeparam>
public struct ByteSet<K> : ISet<K>, IReadOnlySet<K> where K : unmanaged{
    private Vector256<ulong> _bits = Vector256<ulong>.Zero; // 256 bits: 4 ulongs for keys 0-63, 64-127, 128-191, 192-255

    /// <summary>
    /// Tracks modifications to the set for enumeration consistency.
    /// Incremented on any operation that changes the set's contents (add, remove, clear, bulk operations).
    /// </summary>
    internal uint _version = 0; // Tracks modifications for enumeration consistency

    /// <summary>
    /// Tracks the total number of elements in the set.
    /// <list type="bullet">
    /// <item><term>-1</term><description>The set is in an invalid state (uninitialized or explicitly invalidated).</description></item>
    /// <item><term>-2</term><description>The actual count needs to be recalculated (lazy evaluation).</description></item>
    /// <item><term>0 to 256</term><description>The precise number of elements currently in the set.</description></item>
    /// </list>
    /// </summary>
    private int _count = -1; // Total elements, -1 indicates invalid state

    /// <summary>
    /// Gets the number of elements contained in the set.
    /// This property uses lazy evaluation: if the count is marked for recalculation (-2),
    /// it will compute the PopCount for all bits and update the internal count.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown if the set is in an invalid state (i.e., <see cref="IsValid"/> is false).</exception>
    public int Count
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _count switch
               {
                   -1 => throw new InvalidOperationException("Set is not valid."), // Cannot use an invalid set
                   -2 => _count = BitOperations.PopCount(_bits.GetElement(0)) +    // Recalculate PopCount for all 4 ulong segments
                                  BitOperations.PopCount(_bits.GetElement(1)) +
                                  BitOperations.PopCount(_bits.GetElement(2)) +
                                  BitOperations.PopCount(_bits.GetElement(3)),
                   _ => _count // Return the cached count
               };
    }

    /// <summary>
    /// Gets a value indicating whether the set is in a valid state(as not null in reference types).
    /// Check the `IsValid` once before start using the ByteSet (as you do check not  is null with reference type)
    /// </summary>
    public bool IsValid => _count != -1;

    /// <summary>
    /// Marks the set as invalid and clears its contents.
    /// An invalid set cannot be used until re-initialized by a constructor.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Invalidate() => _count = -1;

    // Static constructor for K type validation, runs once per generic instantiation (e.g., ByteSet<byte>).
    static ByteSet()
    {
        if( Unsafe.SizeOf<K>() != 1 ) throw new InvalidOperationException($"The Key type {typeof(K).Name} is {Unsafe.SizeOf<K>()} bytes, but must be 1 bytes.");
    }

    /// <summary>
    /// Initializes an empty <see cref="ByteSet{K}"/>, validating that K is a one-byte type.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown by static constructor if K is not byte or sbyte.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ByteSet() => _count = 0;

    /// <summary>
    /// Initializes a new <see cref="ByteSet{K}"/> with elements from the specified items.
    /// </summary>
    /// <param name="items">An array of keys to add to the set.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ByteSet(params K[] items) : this()
    {
        foreach( var item in items )
            Add(item);
    }


    /// <summary>
    /// Determines whether the current set equals another <see cref="ByteSet{K}"/>.
    /// Two sets are equal if they have the same elements and the same internal count.
    /// </summary>
    /// <param name="other">The other set to compare with.</param>
    /// <returns>True if the sets are equal; otherwise, false.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Equals(ByteSet<K> other) => _count == other._count && _bits == other._bits;

    public override bool Equals(object? obj) => obj is ByteSet<K> other && Equals(other);

    public static bool operator ==(ByteSet<K> left, ByteSet<K> right) => left.Equals(right);
    public static bool operator !=(ByteSet<K> left, ByteSet<K> right) => !left.Equals(right);

    /// <summary>
    /// Gets the hash code for the set.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override int GetHashCode() => HashCode.Combine(_bits, _count);

    /// <summary>
    /// Calculates the rank of a key (number of elements less than or equal the key).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal int Rank(byte key)
    {
        var index = key >> 6;
        var rank  = BitOperations.PopCount(_bits.GetElement(index) << 63 - key);
        for( var i = 0; i < index; i++ )
            rank += BitOperations.PopCount(_bits.GetElement(i));
        return rank;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool Set1(byte key)
    {
        ref var was = ref Unsafe.Add(ref Unsafe.As<Vector256<ulong>, ulong>(ref _bits), key >> 6);

        var now = was | 1UL << key;

        if( was == now ) return false;

        was = now;
        _count++;
        _version++;
        return true;
    }

    /// <summary>
    /// Removes the specified key (0-255) from the set.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool Set0(byte key)
    {
        if( _count == 0 ) return false;

        ref var was = ref Unsafe.Add(ref Unsafe.As<Vector256<ulong>, ulong>(ref _bits), key >> 6);

        var now = was & ~(1UL << key);

        if( was == now ) return false;

        was = now; // Direct write back
        _count--;
        _version++;
        return true;
    }

    /// <summary>
    /// Gets or sets the presence of a key (0-255).
    /// </summary>
    internal bool this[int key]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Get((byte)key);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => _ = value ?
                       Set1((byte)key) :
                       Set0((byte)key);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool Get(byte key) => _count != 0 && (_bits.GetElement(key >> 6) & 1UL << key) != 0;

    /// <summary>
    /// Checks if the current set contains all elements of another.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool ContainsAll(ByteSet<K> other) => Vector256.Equals(_bits & other._bits, other._bits).Equals(Vector256<ulong>.AllBitsSet);

    /// <summary>
    /// Adds all items from the source collection.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ByteSet<K> Set<T>(IEnumerable<T> src) where T : struct
    {
        foreach( var i in src )
        {
            var key = i;
            Set1(Unsafe.As<T, byte>(ref key));
        }

        _version++;
        return this;
    }

    /// <summary>
    /// Performs a union operation with another <see cref="ByteSet{K}"/>.
    /// Elements from the source set are added to the current set.
    /// </summary>
    /// <param name="src">The set to union with.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Or(ByteSet<K> src)
    {
        _bits |= src._bits;
        _version++;
        _count = -2; // Mark _count for recalculation as bits have changed in bulk
    }


    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool And(ByteSet<K> src)
    {
        _bits &= src._bits;
        _version++;
        _count = -2; //mark - need recount
        return true;
    }

    /// <summary>
    /// Removes elements present in another set (difference).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void ExceptWith(ByteSet<K> other)
    {
        _bits &= ~other._bits;
        _version++;
        _count = -2; //mark - need recount
    }

    /// <summary>
    /// Performs symmetric difference with another set.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Xor(ByteSet<K> src)
    {
        _bits ^= src._bits;
        _version++;
        _count = -2; //mark - need recount
    }


    public const int INVALID = -1;

    /// <summary>
    /// Finds the smallest key greater than the specified key.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal int Next1(int key)
    {
        if( _count == 0 || ++key >= 256 ) return INVALID;
        var index       = key >> 6;
        var relativeKey = key & 63;
        var word        = _bits.GetElement(index) >> relativeKey;
        if( word != 0 ) return key + BitOperations.TrailingZeroCount(word);
        for( var i = index + 1; i < 4; i++ )
        {
            word = _bits.GetElement(i);
            if( word != 0 ) return (i << 6) + BitOperations.TrailingZeroCount(word);
        }

        return INVALID;
    }


    public Enumerator GetEnumerator() => new(this);

    IEnumerator IEnumerable.      GetEnumerator() => GetEnumerator();
    IEnumerator<K> IEnumerable<K>.GetEnumerator() => GetEnumerator();

    public struct Enumerator : IEnumerator<K>{
        private readonly ByteSet<K> _set; // A copy of the ByteSet at the time the enumerator was created.

        // Due to ByteSet being a struct, this is a value copy.
        // Consequently, external modifications to the original ByteSet instance
        // after enumerator creation will NOT be detected by the _version check,
        // unless the ByteSet is a field within a class (like in NullableByteMap).
        private int _key;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal Enumerator(in ByteSet<K> set)
        {
            _set = set; // Copy the struct instance.
            Reset();
        }

        public K Current
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _key is INVALID or int.MaxValue ?
                       throw new InvalidOperationException("Enumeration has either not started or has already finished.") :
                       Unsafe.As<int, K>(ref _key);
        }

        object IEnumerator.Current => Current;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool MoveNext()
        {
            if( _key                      == int.MaxValue ) return false;
            if( (_key = _set.Next1(_key)) != -1 ) return true;
            _key = int.MaxValue;
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Reset() => _key = INVALID; // Reset to initial state.

        public void Dispose() { }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void ICollection<K>.Add(K item) => Add(item);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Add(K key) => Set1(Unsafe.As<K, byte>(ref key));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Remove(K key) => Set0(Unsafe.As<K, byte>(ref key));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Contains(K key) => Get(Unsafe.As<K, byte>(ref key));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void CopyTo(K[] dst, int dstIndex)
    {
        ArgumentNullException.ThrowIfNull(dst);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)dstIndex, (uint)dst.Length);
        if( dst.Length - dstIndex < Count ) throw new ArgumentException("Destination array is not long enough.");

        if( _count == 0 ) return;

        var currentCount = 0;
        // Iterate directly over the 4 ulong segments and their bits for maximum speed.
        for( var i = 0; i < 4; i++ )
        {
            var segment = _bits.GetElement(i);
            if( segment == 0 ) continue; // Skip empty segments

            for( var j = 0; j < 64; j++ )
                if( (segment & 1UL << j) != 0 )
                {
                    var byteKey = (byte)((i << 6) + j);
                    dst[dstIndex++] = Unsafe.As<byte, K>(ref byteKey);
                    currentCount++;
                    if( currentCount == Count ) return; // Stop early if all elements copied
                }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Clear()
    {
        if( _count == 0 ) return;
        _bits  = Vector256<ulong>.Zero;
        _count = 0;
        _version++;
    }

    public bool IsReadOnly => false;


    int IReadOnlyCollection<K>.Count => Count;

    public bool IsProperSubsetOf(IEnumerable<K> other)
    {
        var _other = new ByteSet<K>().Set(other);
        return Count < _other.Count && _other.ContainsAll(this);
    }

    public bool IsProperSupersetOf(IEnumerable<K> other)
    {
        var _other = new ByteSet<K>().Set(other);
        return _other.Count < Count && ContainsAll(_other);
    }

    public bool IsSubsetOf(IEnumerable<K> other) => new ByteSet<K>().Set(other).ContainsAll(this);

    public bool IsSupersetOf(IEnumerable<K> other) => ContainsAll(new ByteSet<K>().Set(other));

    public bool SetEquals(IEnumerable<K> other) => this == new ByteSet<K>().Set(other);

    public void IntersectWith(IEnumerable<K> other) => And(new ByteSet<K>().Set(other));

    public void UnionWith(IEnumerable<K> other) => Or(new ByteSet<K>().Set(other));

    public bool Overlaps(IEnumerable<K> other)
    {
        if( _count == 0 ) return false;
        foreach( var item in other )
            if( Contains(item) )
                return true;
        return false;
    }

    public void ExceptWith(IEnumerable<K> other)
    {
        foreach( var item in other )
            Remove(item);
    }

    public void SymmetricExceptWith(IEnumerable<K> other) => Xor(new ByteSet<K>().Set(other));

    public override string ToString() => ToJSON(new StringBuilder()).ToString();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public StringBuilder ToJSON(StringBuilder sb)
    {
        sb.Append('{');
        using var e     = GetEnumerator();
        var       first = true;
        while( e.MoveNext() )
        {
            if( first ) first = false;
            else sb.Append(',');
            sb.Append('"').Append(e.Current).Append("\":null");
        }

        sb.Append('}');
        return sb;
    }
}