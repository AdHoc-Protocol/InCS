// Copyright 2025 Chikirev Sirguy, Unirail Group
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
using System.Threading;

namespace org.unirail.collections;

/// <summary>
/// Implements a lock-free, fixed-size ring buffer for efficient data exchange.
/// A ring buffer is a data structure that uses a single, fixed-size buffer as if it were connected end-to-end.
/// It is useful for buffering data streams, inter-thread communication, and FIFO (First-In, First-Out) data handling.
/// This implementation uses a power-of-two buffer size and bitwise operations for fast index wrapping.
/// For multi-threaded access, it uses a spin-lock in <see cref="GetMultiThreaded"/> and <see cref="PutMultiThreaded"/>.
/// </summary>
/// <remarks>
/// The spin-lock used in multi-threaded methods is suitable for low-contention scenarios. For high-contention environments,
/// consider more advanced synchronization or lock-free algorithms to avoid spin-wait overhead.
/// </remarks>
/// <typeparam name="T">The type of data stored in the ring buffer.</typeparam>
public class RingBuffer<T>{
    private readonly T[]  buffer;
    private readonly uint mask;
    private const    int  SpinWaitDelay = 10;
    private volatile int  lockState;
    private volatile uint readIndex;
    private volatile uint writeIndex;

    /// <summary>
    /// Initializes a new instance of the <see cref="RingBuffer{T}"/> class with a buffer size of 2^powerOfTwo.
    /// </summary>
    /// <param name="powerOfTwo">The exponent to determine the buffer size (2^powerOfTwo). Must be between 0 and 30.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if <paramref name="powerOfTwo"/> is negative or exceeds 30.</exception>
    public RingBuffer(int powerOfTwo)
    {
        if( powerOfTwo < 0 || powerOfTwo > 30 )
            throw new ArgumentOutOfRangeException(nameof(powerOfTwo), "Power of two must be between 0 and 30.");

        var size = 1U << powerOfTwo;
        mask   = size - 1;
        buffer = new T[size];
    }

    /// <summary>
    /// Gets the total capacity of the ring buffer.
    /// </summary>
    public int Length => buffer.Length;

    /// <summary>
    /// Gets the current number of elements in the ring buffer.
    /// </summary>
    public int Count => (int)(writeIndex - readIndex);

    /// <summary>
    /// Retrieves data from the ring buffer in a multi-threaded environment using a spin-lock.
    /// </summary>
    /// <param name="value">Receives the value read from the buffer, if successful.</param>
    /// <returns>True if data was retrieved; false if the buffer is empty.</returns>
    public bool GetMultiThreaded(ref T value)
    {
        if( IsEmpty ) return false; // Early check to avoid unnecessary locking

        while( Interlocked.CompareExchange(ref lockState, 1, 0) == 1 )
            Thread.SpinWait(SpinWaitDelay);

        try { return Get(ref value); }
        finally { lockState = 0; }
    }

    /// <summary>
    /// Retrieves data from the ring buffer in a single-threaded or thread-safe context.
    /// </summary>
    /// <param name="value">Receives the value read from the buffer, if successful.</param>
    /// <returns>True if data was retrieved; false if the buffer is empty.</returns>
    public bool Get(ref T value)
    {
        if( readIndex == writeIndex ) return false;

        value = buffer[readIndex & mask];
        readIndex++;
        return true;
    }

    /// <summary>
    /// Peeks at the next item in the ring buffer without removing it, in a multi-threaded environment.
    /// </summary>
    /// <param name="value">Receives the value at the read index, if available.</param>
    /// <returns>True if an item was peeked; false if the buffer is empty.</returns>
    public bool PeekMultiThreaded(ref T value)
    {
        if( IsEmpty ) return false;

        while( Interlocked.CompareExchange(ref lockState, 1, 0) == 1 )
            Thread.SpinWait(SpinWaitDelay);

        try { return Peek(ref value); }
        finally { lockState = 0; }
    }

    /// <summary>
    /// Peeks at the next item in the ring buffer without removing it.
    /// </summary>
    /// <param name="value">Receives the value at the read index, if available.</param>
    /// <returns>True if an item was peeked; false if the buffer is empty.</returns>
    public bool Peek(ref T value)
    {
        if( readIndex == writeIndex ) return false;

        value = buffer[readIndex & mask];
        return true;
    }

    /// <summary>
    /// Adds data to the ring buffer in a multi-threaded environment using a spin-lock.
    /// </summary>
    /// <param name="value">The data to add to the buffer.</param>
    /// <returns>True if data was added; false if the buffer is full.</returns>
    public bool PutMultiThreaded(T value)
    {
        if( IsFull ) return false;

        while( Interlocked.CompareExchange(ref lockState, 1, 0) == 1 )
            Thread.SpinWait(SpinWaitDelay);

        try { return Put(value); }
        finally { lockState = 0; }
    }

    /// <summary>
    /// Adds data to the ring buffer in a single-threaded or thread-safe context.
    /// </summary>
    /// <param name="value">The data to add to the buffer.</param>
    /// <returns>True if data was added; false if the buffer is full.</returns>
    public bool Put(T value)
    {
        if( IsFull ) return false;

        buffer[writeIndex & mask] = value;
        writeIndex++;
        return true;
    }

    /// <summary>
    /// Clears the ring buffer by resetting the read and write indices.
    /// </summary>
    public void Clear()
    {
        readIndex  = 0;
        writeIndex = 0;
        if( typeof(T).IsClass ) Array.Clear(buffer, 0, buffer.Length);
    }

    /// <summary>
    /// Gets a value indicating whether the ring buffer is full.
    /// </summary>
    public bool IsFull => writeIndex - readIndex >= (uint)buffer.Length;

    /// <summary>
    /// Gets a value indicating whether the ring buffer is empty.
    /// </summary>
    public bool IsEmpty => readIndex == writeIndex;
}