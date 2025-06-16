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
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using org.unirail.collections;

namespace org.unirail{
    public interface Network{
        public interface Channel{
            // Defines events that occur on a network channel.
            // These events represent state changes in the connection lifecycle,
            // both from the perspective of external to internal and internal to external connections.
            public enum Event{
                // Event triggered when an external entity connects to the internal system.
                EXT_INT_CONNECT = 0,

                // Event triggered when the internal system connects to an external entity.
                INT_EXT_CONNECT = 1,

                // Event triggered when an external entity disconnects from the internal system.
                EXT_INT_DISCONNECT = 2,

                // Event triggered when the internal system disconnects from an external entity.
                INT_EXT_DISCONNECT = 3,

                // Event triggered when a timeout occurs during communication.
                TIMEOUT = 4,
            }
        }

        public interface WebSocket{
            // Defines events specific to WebSocket communication, extending the base Channel events.
            // Includes events for connection establishment, closure, and WebSocket control frames like PING and PONG.
            public enum Event{
                // Event triggered when the server initiates a connection to the client.
                INT_EXT_CONNECT = 6,

                // Event triggered when the client initiates a connection to the server.
                EXT_INT_CONNECT = 7,

                // WebSocket Close event, triggered when a CLOSE frame is received or sent.
                CLOSE = OPCode.CLOSE,

                // WebSocket Ping event, triggered when a PING frame is received.
                PING = OPCode.PING,

                // WebSocket Pong event, triggered when a PONG frame is received, usually in response to a Ping.
                PONG = OPCode.PONG,

                // Event for an empty WebSocket frame, which can be used for keep-alive signals or as a placeholder.
                EMPTY_FRAME = 11,
            }

            // Internal enum for WebSocket frame masking and flags.
            internal enum Mask{
                // Final fragment flag. Indicates that this is the last fragment in a message.
                FIN = 0b1000_0000,

                // Opcode mask. Used to extract the opcode from the first byte of a WebSocket frame.
                OPCODE = 0b0000_1111,

                // Mask bit. Indicates whether the payload is masked (client-to-server messages must be masked).
                MASK = 0b1000_0000,

                // Payload length mask. Used to extract the payload length from the second byte of a WebSocket frame.
                LEN = 0b0111_1111,
            }

            // Defines WebSocket frame opcodes as per RFC 6455.
            public enum OPCode{
                // Continuation frame. Used for fragmented messages.
                CONTINUATION = 0x00,

                // Text frame. Indicates that the payload data is text encoded as UTF-8.
                TEXT_FRAME = 0x01,

                // Binary frame. Indicates that the payload data is binary data.
                BINARY_FRAME = 0x02,

                // Connection close frame. Indicates that a connection close is requested.
                CLOSE = 0x08,

                // Ping frame. Used to check the health of the connection.
                PING = 0x09,

                // Pong frame. Response to a Ping frame.
                PONG = 0x0A
            }

            // Internal enum to track the state of WebSocket frame processing during reception.
            // This state machine helps parse incoming WebSocket frames byte by byte.
            internal enum State{
                // Handshake state. Initial state when a WebSocket connection is being established.
                HANDSHAKE = 0,

                // New frame state. Ready to process a new WebSocket frame header.
                NEW_FRAME = 1,

                // Payload length byte state. Processing the byte that contains payload length information.
                PAYLOAD_LENGTH_BYTE = 2,

                // Payload length bytes state. Processing extended payload length bytes (if payload length is 126 or 127).
                PAYLOAD_LENGTH_BYTES = 3,

                // XOR byte 0 state. Processing the first byte of the masking key.
                XOR0 = 4,

                // XOR byte 1 state. Processing the second byte of the masking key.
                XOR1 = 5,

                // XOR byte 2 state. Processing the third byte of the masking key.
                XOR2 = 6,

                // XOR byte 3 state. Processing the fourth byte of the masking key.
                XOR3 = 7,

                // Data byte 0 state. Processing the first byte of the payload data.
                DATA0 = 8,

                // Data byte 1 state. Processing subsequent bytes of the payload data.
                DATA1 = 9,

                // Data byte 2 state. Processing subsequent bytes of the payload data.
                DATA2 = 10,

                // Data byte 3 state. Processing subsequent bytes of the payload data.
                DATA3 = 11,

                // Discard state. Discarding remaining payload bytes, for example, after processing a control frame.
                DISCARD = 12
            }
        }

        /// <summary>
        /// Abstract base class for TCP network communication, providing a foundation for both clients and servers.
        /// It utilizes asynchronous socket operations via SocketAsyncEventArgs for efficient non-blocking I/O.
        /// Type parameters SRC and DST define the source and destination for byte streams, respectively.
        /// </summary>
        /// <typeparam name="SRC">The type that provides bytes to be transmitted (BytesSrc).</typeparam>
        /// <typeparam name="DST">The type that consumes received bytes (BytesDst).</typeparam>
        /// <remarks>
        /// <para>
        /// This implementation is based on the SocketAsyncEventArgs pattern for high-performance asynchronous socket operations.
        /// It maintains a pool of reusable channels (SocketAsyncEventArgs) to minimize object allocation and garbage collection overhead.
        /// </para>
        /// <para>
        /// The asynchronous socket operation pattern involves:
        /// <list type="number">
        ///     <item>Allocate or reuse a SocketAsyncEventArgs context object.</item>
        ///     <item>Set context properties for the operation (callback, buffer, etc.).</item>
        ///     <item>Call the appropriate socket method (e.g., xxxAsync) to start the asynchronous operation.</item>
        ///     <item>In the callback, check if the operation completed synchronously or asynchronously by examining the return value of xxxAsync.</item>
        ///     <item>Query context properties for operation status and results.</item>
        ///     <item>Reuse or return the context object to a pool.</item>
        /// </list>
        /// </para>
        /// <para>
        /// The lifecycle of SocketAsyncEventArgs is managed by the application and asynchronous I/O references.
        /// Retaining a reference to the context allows for reuse, which is beneficial for performance.
        /// </para>
        /// </remarks>
        /// <seealso cref="SocketAsyncEventArgs"/>
        /// <seealso href="https://docs.microsoft.com/en-us/dotnet/framework/network-programming/socket-performance-enhancements-in-version-3-5?redirectedfrom=MSDN">Socket Performance Enhancements in Version 3.5</seealso>
        public abstract class TCP<SRC, DST>
            where DST : AdHoc.BytesDst
            where SRC : AdHoc.BytesSrc{
#region> TCP code
#endregion> Network.TCP

            /// <summary>
            /// The head of a one-way linked list of communication channels.
            /// Each channel represents a connection and points to the next channel in the list via <see cref="Channel.next"/>.
            /// </summary>
            public readonly Channel channels;

            // Action delegate for setting up buffers for SocketAsyncEventArgs using a shared ArrayPool.
            // This allows efficient buffer reuse and reduces memory allocation overhead.
            private readonly Action<SocketAsyncEventArgs> buffers;

            /// <summary>
            /// Constant representing a free channel, indicated by -1 in the <see cref="Channel.receive_time"/> field.
            /// </summary>
            protected const long FREE = -1;

            /// <summary>
            /// Factory function to create new communication channels of type <see cref="Channel"/>.
            /// This delegate is used to instantiate new channels as needed, for example, when accepting new connections.
            /// </summary>
            public readonly Func<TCP<SRC, DST>, Channel> new_channel;

            /// <summary>
            /// Name identifier for this TCP host instance. Used for logging and debugging purposes.
            /// </summary>
            public string name;

            /// <summary>
            /// Initializes a new instance of the <see cref="TCP{SRC, DST}"/> class.
            /// </summary>
            /// <param name="name">The name of this TCP host instance.</param>
            /// <param name="new_channel">The factory function for creating new channels.</param>
            /// <param name="buffer_size">The size of the buffer to allocate for each channel for sending and receiving data.</param>
            public TCP(string name, Func<TCP<SRC, DST>, Channel> new_channel, int buffer_size)
            {
                this.name = name;
                buffers   = dst => dst.SetBuffer(ArrayPool<byte>.Shared.Rent(buffer_size), 0, buffer_size); // Initialize buffer allocation action using ArrayPool.
                channels  = (this.new_channel = new_channel)(this);                                         // Initialize the channel list with the first channel created by the factory.
            }

            /// <summary>
            /// Attempts to allocate a free communication channel from the existing pool.
            /// If no free channel is available, it creates a new one and adds it to the pool.
            /// </summary>
            /// <returns>An available <see cref="Channel"/> for communication.</returns>
            protected Channel allocate()
            {
                var ch = channels;                    // Start traversal from the head of the channel linked list.
                for( ; !ch.activate(); ch = ch.next ) // Iterate through channels until a free one is activated or the end is reached.
                    if( ch.next == null )             // If reached the end of the list without finding a free channel.
                    {
                        var ret = new_channel(this); // Create a new channel using the factory function.

                        // Initialize timestamps to mark the new channel as active.
                        ret.receive_time = ret.transmit_time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                        // Atomically append the new channel to the end of the linked list to handle concurrency.
                        while( Interlocked.CompareExchange(ref ch.next, ret, null) != null )
                            ch = ch.next; // If another thread added a channel concurrently, move to the new end and retry.

                        return ret; // Return the newly created and added channel.
                    }

                ch.transmit_time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); // Update transmit timestamp upon successful allocation.
                return ch;                                                         // Return the allocated channel.
            }

            /// <summary>
            /// Event handler for handling failures and exceptions that occur within the TCP communication layer.
            /// By default, it logs the error source and exception details to the console.
            /// In debug mode, it also prints the stack trace for more detailed debugging information.
            /// </summary>
            public Action<object, Exception> onFailure = (src, t) =>
                                                         {
                                                             Console.WriteLine("onFailure " + src);
#if DEBUG
                                                             Console.WriteLine(new Exception("onFailure").StackTrace); // Print stack trace in debug mode for detailed error context.
#endif
                                                             Console.WriteLine(t); // Print the exception details.
                                                         };


            /// <summary>
            /// Triggers the maintenance thread to perform maintenance tasks immediately.
            /// This method is virtual and can be overridden in derived classes to implement specific triggering logic if needed.
            /// </summary>
            public virtual void TriggerMaintenance() { }

            /// <summary>
            /// Asynchronously triggers the maintenance thread to perform maintenance tasks immediately.
            /// This method is virtual and can be overridden in derived classes to implement specific asynchronous triggering logic if needed.
            /// </summary>
            public virtual async Task TriggerMaintenanceAsync() { }

            /// <summary>
            /// Swaps the current event handler <see cref="onEvent"/> with another provided handler.
            /// This allows for dynamically changing the event handling logic at runtime.
            /// </summary>
            /// <param name="other">The new event handler to set.</param>
            /// <returns>The old event handler that was replaced.</returns>
            public Action<Channel, int> swap(Action<Channel, int> other)
            {
                var ret = onEvent; // Store the current event handler.
                onEvent = other;   // Replace the current handler with the new one.
                return ret;        // Return the old handler.
            }

            /// <summary>
            /// Default event handler for network channel events.
            /// It logs various connection and communication events to the console.
            /// This handler can be replaced or extended by assigning a new delegate to <see cref="onEvent"/>.
            /// </summary>
            public Action<Channel, int> onEvent = (channel, Event) =>
                                                  {
#if DEBUG
                                                      Console.WriteLine("debugging stack of onEvent");
                                                      Console.WriteLine(new StackTrace().ToString()); // Print stack trace in debug mode for event context.
#endif
                                                      switch( Event )
                                                      {
                                                          case (int)Network.Channel.Event.EXT_INT_CONNECT:
                                                              Console.WriteLine(channel.host + ":Received connection from " + channel.peer_ip);
                                                              return;
                                                          case (int)Network.Channel.Event.INT_EXT_CONNECT:
                                                              Console.WriteLine(channel.host + ":Connected to " + channel.peer_ip);
                                                              return;
                                                          case (int)Network.Channel.Event.EXT_INT_DISCONNECT:
                                                              Console.WriteLine(channel.host + ":Remote peer " + channel.peer_ip + " has dropped the connection.");
                                                              return;
                                                          case (int)Network.Channel.Event.INT_EXT_DISCONNECT:
                                                              Console.WriteLine(channel.host + ":This host has dropped the connection to " + channel.peer_ip);
                                                              return;
                                                          case (int)Network.Channel.Event.TIMEOUT:
                                                              Console.WriteLine(channel.host + ":Timeout while receiving from " + channel.peer_ip);
                                                              return;
                                                          case (int)Network.WebSocket.Event.EXT_INT_CONNECT:
                                                              Console.WriteLine(channel.host + ":Websocket from " + channel.peer_ip);
                                                              return;
                                                          case (int)Network.WebSocket.Event.INT_EXT_CONNECT:
                                                              Console.WriteLine(channel.host + ":Websocket to " + channel.peer_ip);
                                                              return;
                                                          case (int)Network.WebSocket.Event.PING:
                                                              Console.WriteLine(channel.host + ":PING from " + channel.peer_ip);
                                                              return;
                                                          case (int)Network.WebSocket.Event.PONG:
                                                              Console.WriteLine(channel.host + ":PONG from " + channel.peer_ip);
                                                              return;
                                                          default:
                                                              Console.WriteLine(channel.peer_ip + " event: " + Event); // Log unknown events with peer IP and event code.
                                                              break;
                                                      }
                                                  };

            /// <summary>
            /// Represents a communication channel, encapsulating a socket connection and associated data buffers and event handlers.
            /// It extends <see cref="SocketAsyncEventArgs"/> to manage asynchronous socket operations efficiently.
            /// </summary>
            public class Channel : SocketAsyncEventArgs{

#region> Channel code
#endregion> Network.TCP.Channel

                /// <summary>
                /// The external socket associated with this channel. Represents the actual network connection.
                /// It is nullable to represent channels that are not yet connected or have been closed.
                /// </summary>
                public Socket? ext;

                /// <summary>
                /// Gets the IP endpoint of the remote peer connected to this channel.
                /// </summary>
                public EndPoint peer_ip => ext!.RemoteEndPoint!;

                /// <summary>
                /// Identifier for the peer at the other end of the connection. Can be used for peer identification and management.
                /// </summary>
                public long peer_id = 0;

                /// <summary>
                /// Identifier for the current communication session on this channel. Useful for session management and tracking.
                /// </summary>
                public long session_id = 0;

                /// <summary>
                /// Timestamp of the last received data in Unix milliseconds.
                /// If <see cref="receive_time"/> equals <see cref="FREE"/>, the channel is considered available for reuse.
                /// </summary>
                public long receive_time = FREE;

                /// <summary>
                /// Atomically attempts to mark this channel as active by updating <see cref="receive_time"/> from <see cref="FREE"/> to the current timestamp.
                /// This method is thread-safe and used to ensure exclusive access to a channel when allocating it for a new connection.
                /// </summary>
                /// <returns><c>true</c> if the channel was successfully activated (i.e., it was free and is now in use); otherwise, <c>false</c>.</returns>
                public bool activate() => Interlocked.CompareExchange(ref receive_time, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), FREE) != FREE;

                /// <summary>
                /// Timestamp of the last transmitted data in Unix milliseconds.
                /// </summary>
                public long transmit_time = FREE;

                /// <summary>
                /// Gets a value indicating whether the channel is currently active (in use).
                /// A channel is active if <see cref="receive_time"/> is greater than 0 (not equal to <see cref="FREE"/>).
                /// </summary>
                public bool is_active => 0 < receive_time;

                /// <summary>
                /// Reference to the host TCP instance that manages this channel.
                /// Provides access to host-level resources and functionalities.
                /// </summary>
                public readonly TCP<SRC, DST> host;


                /// <summary>
                /// Initializes a new instance of the <see cref="Channel"/> class.
                /// </summary>
                /// <param name="host">The host TCP instance that owns this channel.</param>
                public Channel(TCP<SRC, DST> host)
                {
                    receive_mate.Completed += OnCompleted; // Attach the completion handler for receive operations.
                    DisconnectReuseSocket  =  true;        // Enable socket reuse after disconnection for efficiency.
                    onNewBytesToTransmitArrive = // Initialize action to handle new bytes for transmission.
                        _ =>
                        {
                            if( ext != null && idle_transmitter_activated() ) // If socket is valid and transmitter is ready.
                                transmit();                                   // Initiate data transmission.
                        };
                    this.host = host; // Set the reference to the host TCP instance.
                }

#region close
                /// <summary>
                /// Initiates a graceful close of the channel. This method should be called only after the last packet has been sent.
                /// It follows a four-step graceful close sequence to ensure all data is transmitted and acknowledged before closing the socket.
                /// </summary>
                /// <remarks>
                /// <para>
                /// **Correct Sequence for Closing an Active Socket:**
                /// <list type="number">
                ///     <item>Send the Last Packet Using SendAsync.</item>
                ///     <item>Call <c>socket.Shutdown(SocketShutdown.Send)</c> to signal no more data will be sent.</item>
                ///     <item>Use <c>ReceiveAsync</c> to wait for the peer's acknowledgment (TCP ACK) and its own FIN.</item>
                ///     <item>After receiving the final acknowledgment (zero bytes received) and data, call <c>socket.Close()</c>.</item>
                /// </list>
                /// </para>
                /// <para>
                /// **Important Notes:**
                /// <list type="bullet">
                ///     <item>This method should be called only after ensuring all pending send operations are completed.</item>
                ///     <item>Refer to the Windows Sockets documentation (<see href="https://learn.microsoft.com/en-us/windows/win32/api/winsock2/nf-winsock2-shutdown">Shutdown function (winsock2.h)</see>) for details on socket shutdown behavior.</item>
                ///     <item>Avoid using <see cref="Socket.Shutdown(SocketShutdown)"/> from .NET documentation as it might not accurately represent the underlying socket behavior.</item>
                /// </list>
                /// </para>
                /// </remarks>
                public void CloseGracefully()
                {
                    ClosingGracefully = true;           // Mark that a graceful close is in progress.
                    ext?.Shutdown(SocketShutdown.Send); // Initiate TCP shutdown by sending FIN to indicate no more data will be sent.
                }

                // Indicates if a graceful close is in progress for this channel.
                private bool ClosingGracefully = false;

                /// <summary>
                /// Timeout in milliseconds for waiting for a graceful close to complete.
                /// If a graceful close does not complete within this timeout, a forced close may be initiated.
                /// </summary>
                public int ClosingGracefullyTimeout = 1000;

                /// <summary>
                /// Forces the immediate asynchronous closure of the channel.
                /// This method abruptly terminates the connection without waiting for pending data to be sent or received.
                /// </summary>
                public void Close()
                {
                    ClosingGracefully = false;          // Reset graceful close flag.
                    ext?.Shutdown(SocketShutdown.Both); // Initiate immediate shutdown of both send and receive directions.
                    ext?.Close();                       // Close the socket, releasing all associated resources.
                }

                /// <summary>
                /// Closes the connection without disposing of resources immediately.
                /// This method allows trusted users to reconnect within a timeout session and potentially restore the previous state.
                /// </summary>
                /// <remarks>
                /// <para>
                /// **Warning:** This approach poses a potential DDoS risk and should only be used for trusted user connections.
                /// </para>
                /// <para>
                /// **Important:** Do not call this method directly from sending or receiving threads.
                /// Doing so may disrupt ongoing operations and lead to crashes or inconsistent states.
                /// Instead, trigger the maintenance thread to handle channel closures safely.
                /// Use <see cref="Close()"/> for normal channel closure to ensure the channel is not busy.
                /// </para>
                /// </remarks>
                public virtual void Close_not_dispose()
                {
                    if( ext == null ) return; // Exit if the socket is already null or not initialized.

                    host.onEvent(this, (int)Network.Channel.Event.INT_EXT_DISCONNECT); // Notify host of internal disconnection event.

                    // CRITICAL: Always call Shutdown before Close for connection-oriented sockets to ensure data integrity.
                    // Refer to MSDN documentation for Socket.Shutdown and Socket.Close for details.
                    try { ext?.Shutdown(SocketShutdown.Both); } // Initiate shutdown in both directions.
                    catch( Exception )
                    { /* Ignore exceptions during shutdown, socket might already be closed. */
                    }

                    ext?.Close();           // Close the socket to release resources.
                    Thread.SpinWait(1);     // Introduce a brief pause to allow for cleanup operations to complete.
                    ext = null;             // Reset the external socket reference.
                    activate_transmitter(); // Prevent accidental triggering of transmitter after close.

                    // If both transmitter and receiver are not open, proceed to full dispose to release resources.
                    if( (transmitter == null || !transmitter.isOpen()) && (receiver == null || !receiver.isOpen()) )
                        Close_and_dispose(); // Fully dispose resources if no I/O operations are active.
                }

                /// <summary>
                /// Closes the connection and disposes of all associated resources, returning the channel to a free state.
                /// </summary>
                /// <remarks>
                /// **Important:** Do not call this method directly from sending or receiving threads.
                /// Doing so may disrupt ongoing operations and lead to crashes or inconsistent states.
                /// Instead, use <see cref="Close()"/> for normal channel closure to ensure the channel is not busy.
                /// Trigger the maintenance thread to handle channel closures safely.
                /// </remarks>
                public void Close_and_dispose()
                {
                    // Atomically check if the channel is already free and exit if so to prevent redundant operations.
                    if( Interlocked.Exchange(ref receive_time, FREE) == FREE )
                        return; // Already disposed or free.

                    Close();              // Close the socket connection.
                    transmitter?.Close(); // Close the transmitter if it exists.
                    receiver?.Close();    // Close the receiver if it exists.

                    RemoteEndPoint = null; // Clear remote endpoint information.

                    // Return allocated buffers to the shared ArrayPool for reuse to reduce GC overhead.
                    if( Buffer != null )
                    {
                        ArrayPool<byte>.Shared.Return(Buffer);
                        SetBuffer(null, 0, 0); // Clear the buffer in SocketAsyncEventArgs.
                    }

                    if( receive_mate.Buffer != null )
                    {
                        ArrayPool<byte>.Shared.Return(receive_mate.Buffer);
                        receive_mate.SetBuffer(null, 0, 0); // Clear the buffer in receive_mate SocketAsyncEventArgs.
                    }

                    on_disposed?.Invoke(this); // Invoke the disposed event handler if registered.
                }
#endregion

                /// <summary>
                /// Event handler invoked when a connection is successfully established for this channel.
                /// Can be used to perform actions immediately after connection, such as sending initial data or configuring communication parameters.
                /// </summary>
                public Action<Channel>? on_connected;

                /// <summary>
                /// Event handler invoked when this channel is disposed and resources are released.
                /// Can be used for cleanup operations or logging when a channel is no longer in use.
                /// </summary>
                public Action<Channel>? on_disposed;

#region Transmitting
                /// <summary>
                /// Source of bytes to be transmitted over this channel.
                /// Implements <see cref="AdHoc.BytesSrc"/> to provide a byte stream for sending data.
                /// </summary>
                public SRC? transmitter;

                /// <summary>
                /// Event handler invoked when all packets currently available in the transmitter have been successfully sent through the socket.
                /// This event can be used to signal the completion of a transmission batch or to trigger subsequent actions after sending data.
                /// </summary>
                public Action<Channel>? on_all_packs_sent;

                /// <summary>
                /// Overrides the base <see cref="SocketAsyncEventArgs.OnCompleted(SocketAsyncEventArgs)"/> method to handle completion events for asynchronous socket operations.
                /// This method is called by the .NET framework when an asynchronous operation initiated on this channel completes.
                /// </summary>
                /// <param name="_transmit">The SocketAsyncEventArgs instance representing the completed operation.</param>
                protected override void OnCompleted(SocketAsyncEventArgs _transmit)
                {
                    // Prevent maintenance operations from interfering with I/O completion.
                    while( locked_for_maintenance() ) Thread.SpinWait(5);
                    try
                    {
                        if( _transmit.SocketError == SocketError.Success ) // Check if the socket operation completed successfully.
                            switch( _transmit.LastOperation )              // Determine the type of completed operation.
                            {
                                case SocketAsyncOperation.Connect:
                                    transmiter_connected(ConnectSocket); // Handle successful connection completion.
                                    return;
                                case SocketAsyncOperation.Disconnect:
                                    host.onEvent(this, (int)Network.Channel.Event.INT_EXT_DISCONNECT); // Notify of disconnect event.
                                    return;
                                case SocketAsyncOperation.Send:
                                    activate_transmitter(); // Reset transmitter state to allow new data to be sent.
                                    transmit();             // Initiate the next transmission if more data is available.
                                    return;
                            }
                        else
                            host.onFailure(this, new Exception("SocketError:" + _transmit.SocketError)); // Handle socket errors.
                    }
                    finally { pending_send_receive_completed(); } // Signal completion of pending send/receive operation for maintenance synchronization.
                }

                // Handles the event when an outgoing connection to a remote peer is successfully established.
                // Initializes the transmitter, sets up buffers, and starts the receiving process if a receiver is configured.
                internal void transmiter_connected(Socket? ext)
                {
                    this.ext      = ext;                                            // Assign the connected socket to the channel.
                    transmit_time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); // Update transmit timestamp.

                    host.onEvent(this, (int)Network.Channel.Event.INT_EXT_CONNECT); // Notify host of successful outgoing connection.

                    if( Buffer == null ) // Allocate transmit buffer if not already allocated.
                        host.buffers(this);
                    else
                        SetBuffer(0, Buffer.Length); // Reset buffer offset and count if buffer is reused.

                    idle_transmitter();                                                           // Mark transmitter as idle, ready for new data.
                    transmitter!.subscribeOnNewBytesToTransmitArrive(onNewBytesToTransmitArrive); // Subscribe to notifications for new data from the transmitter source.

                    if( receiver            == null ) return;                     // Half-duplex mode: Exit if no receiver is configured for this channel.
                    if( receive_mate.Buffer == null ) host.buffers(receive_mate); // Allocate receive buffer if not already allocated.

                    on_connected?.Invoke(this); // Invoke the 'on_connected' event handler.

                    if( !ext!.ReceiveAsync(receive_mate) ) receive(); // Start asynchronous receive operation. If synchronous, call receive method directly.
                }

                // Callback function triggered when new bytes are available in the internal source layer for transmission via the external socket.
                // This action is typically subscribed to by the transmitter (<see cref="transmitter"/>) to initiate data sending.
                protected readonly Action<AdHoc.BytesSrc> onNewBytesToTransmitArrive;

                /// <summary>
                /// Gets or sets the send timeout in milliseconds for socket operations on this channel.
                /// This timeout applies to synchronous Send calls. Asynchronous operations may have their own timeout mechanisms.
                /// </summary>
                public int TransmitTimeout { get => ext!.SendTimeout; set => ext!.SendTimeout = value; }

                // Initiates the transmission of data from the transmitter's buffer over the socket.
                // This method is called when new data is available for sending or when a previous send operation completes.
                private void transmit()
                {
                    do
                        while( transmit(Buffer!) )     // Attempt to load data from the transmitter and prepare the send buffer.
                            if( ext!.SendAsync(this) ) // Initiate asynchronous send operation.
                                return;                // If asynchronous, the operation completion will be handled in OnCompleted.
                    while( transmitter_in_use() );     // Continue transmission loop as long as the transmitter is marked as in use (has more data).

                    on_all_packs_sent?.Invoke(this); // Invoke the 'on_all_packs_sent' event handler after all data is transmitted.
                }

                // Loads data from the transmitter (<see cref="transmitter"/>) into the provided buffer for transmission.
                // <param name="dst">The destination byte array buffer to load data into.</param>
                // <returns><c>true</c> if data was successfully loaded into the buffer and is ready for transmission; <c>false</c> if no data is available from the transmitter.</returns>
                protected virtual bool transmit(byte[] dst)
                {
                    var bytes = transmitter!.Read(dst, 0, dst.Length); // Read data from the transmitter into the buffer.
                    if( bytes < 1 ) return false;                      // No data available to transmit.
                    SetBuffer(0, bytes);                               // Configure the SocketAsyncEventArgs buffer to send the read data.
                    return true;                                       // Data is ready for transmission.
                }

                // Volatile lock variable to manage transmitter state (idle or in use).
                // 1 indicates transmitter is in use or locked, 0 indicates idle or unlocked.
                protected volatile int transmit_lock = 1;

                // Resets the transmitter lock to idle state and checks if it was previously in use.
                // <returns><c>true</c> if the transmitter was previously in use (lock value was not 0); otherwise, <c>false</c>.</returns>
                protected bool transmitter_in_use() => Interlocked.Exchange(ref transmit_lock, 0) != 0;

                // Resets the transmitter lock to the idle state (unlocked).
                // This prepares the transmitter to be triggered again when new data packets arrive for sending.
                protected void idle_transmitter() => transmit_lock = 0;

                // Activates the transmitter and checks if it was previously idle.
                // <returns><c>true</c> if the transmitter was previously idle and is now activated (lock value transitioned from 0 to 1); otherwise, <c>false</c>.</returns>
                protected bool idle_transmitter_activated() => Interlocked.Increment(ref transmit_lock) == 1;

                // Activates the transmitter by incrementing the transmit lock counter.
                // This method can be used to ensure that the transmitter is ready to process and send new data.
                protected void activate_transmitter() => Interlocked.Increment(ref transmit_lock);
#endregion

#region Receiving
                /// <summary>
                /// Destination for bytes received from the external socket.
                /// Implements <see cref="AdHoc.BytesDst"/> to consume the incoming byte stream.
                /// </summary>
                public DST? receiver;

                // SocketAsyncEventArgs instance specifically for receive operations on this channel.
                // Used to manage asynchronous receive calls and their completion events.
                internal readonly SocketAsyncEventArgs receive_mate = new();


                /// <summary>
                /// Overload of <see cref="OnCompleted(SocketAsyncEventArgs)"/> to handle completion events specifically for receive operations.
                /// This method is called when an asynchronous receive operation initiated on this channel's socket completes.
                /// </summary>
                /// <param name="src">The source object (typically the socket) that triggered the event.</param>
                /// <param name="_receive">The SocketAsyncEventArgs instance representing the completed receive operation.</param>
                protected void OnCompleted(object? src, SocketAsyncEventArgs _receive)
                {
                    // Prevent maintenance operations from interfering with I/O completion.
                    while( locked_for_maintenance() ) Thread.SpinWait(5);
                    receive_time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); // Update receive timestamp on any receive event.
                    try
                    {
                        switch( _receive.SocketError )
                        {
                            case SocketError.Success:            // Socket operation completed successfully.
                                switch( _receive.LastOperation ) // Check the type of the last operation.
                                {
                                    case SocketAsyncOperation.Disconnect:
                                        host.onEvent(this, (int)Network.Channel.Event.EXT_INT_DISCONNECT); // Notify host of external disconnect.
                                        return;
                                    case SocketAsyncOperation.Receive:
                                        if( ClosingGracefully ) // Check if graceful closing is in progress.
                                        {
                                            if( receive_mate.Offset + receive_mate.BytesTransferred == 0 ) // Check if zero bytes were received, indicating peer FIN.
                                            {
                                                Close(); // Complete graceful close by closing the socket.
                                                return;
                                            }

                                            ReceiveTimeout = ClosingGracefullyTimeout; // Extend receive timeout during graceful close.
                                        }

                                        receive(); // Process received data and initiate next receive operation.
                                        return;
                                }

                                break;
                            case SocketError.TimedOut: // Socket operation timed out.
                                if( ClosingGracefully )
                                    Close(); // If graceful close timeout, proceed to close.
                                else
                                    host.onEvent(this, (int)Network.Channel.Event.TIMEOUT); // Notify host of receive timeout event.

                                break;
                            default:                                                                        // Other socket errors.
                                host.onFailure(this, new Exception("SocketError:" + _receive.SocketError)); // Handle socket error using failure handler.
                                break;
                        }
                    }
                    finally { pending_send_receive_completed(); } // Signal completion of pending send/receive operation for maintenance synchronization.
                }

                // Initializes the receiver when a new external connection is established.
                // Sets up receive buffers and event handlers, and starts the initial receive operation.
                internal void receiver_connected(Socket ext)
                {
                    this.ext     = ext;                                            // Assign the connected socket to the channel.
                    receive_time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); // Update receive timestamp.

                    host.onEvent(this, (int)Network.Channel.Event.EXT_INT_CONNECT); // Notify host of external connection event.
                    if( !ext.Connected )                                            // Check if the connection is still active after event handling.
                        return;                                                     // If connection was closed during event handling, exit.

                    if( receive_mate.Buffer == null ) host.buffers(receive_mate); // Allocate receive buffer if not already allocated.

                    if( transmitter != null ) // If a transmitter is configured (full-duplex channel).
                    {
                        idle_transmitter();                                                          // Mark transmitter as idle.
                        if( Buffer == null ) host.buffers(this);                                     // Allocate transmit buffer if not already allocated.
                        transmitter.subscribeOnNewBytesToTransmitArrive(onNewBytesToTransmitArrive); // Subscribe transmitter to new data notifications.
                    }

                    on_connected?.Invoke(this); // Invoke the 'on_connected' event handler.

                    // Start asynchronous receive operation. If synchronous, call receive method directly.
                    if( !this.ext!.ReceiveAsync(receive_mate) ) receive();
                }

                /// <summary>
                /// Stops receiving data on this channel by shutting down the receive direction of the socket.
                /// This method can be used to temporarily or permanently halt incoming data flow.
                /// </summary>
                public void StopReceiving()
                {
                    ext?.Shutdown(SocketShutdown.Receive); // Shutdown the receive direction of the socket.
                }

                /// <summary>
                /// Gets or sets the receive timeout in milliseconds for synchronous Receive calls on this channel.
                /// During a graceful close, it uses <see cref="ClosingGracefullyTimeout"/>; otherwise, it uses the socket's ReceiveTimeout.
                /// </summary>
                public int ReceiveTimeout
                {
                    get => ClosingGracefully ?
                               ClosingGracefullyTimeout : // Use graceful close timeout if closing gracefully.
                               ext!.ReceiveTimeout;       // Otherwise, use the socket's receive timeout.

                    set => ext!.ReceiveTimeout = ClosingGracefully ?
                                                     ClosingGracefullyTimeout : // Use graceful close timeout if closing gracefully.
                                                     value;                     // Otherwise, set the socket's receive timeout.
                }

                // Processes incoming data received in the <see cref="receive_mate.Buffer"/>.
                // This method is called after a receive operation completes successfully.
                // It writes the received bytes to the receiver (<see cref="receiver"/>) and initiates the next receive operation.
                private void receive()
                {
                    try
                    {
                        do
                        {
                            if( receive_mate.BytesTransferred == 0 ) // Check if zero bytes were transferred, indicating connection closure by peer.
                            {
                                Close(); // Close the channel as the remote end has closed the connection.
                                return;
                            }

                            // Write the received data to the receiver destination.
                            receive(receive_mate.Buffer!, receive_mate.Offset + receive_mate.BytesTransferred);
                        }
                        while( !ext!.ReceiveAsync(receive_mate) ); // Initiate the next asynchronous receive operation.
                    }
                    catch( Exception e ) { host.onFailure(this, e); } // Handle any exceptions during the receive process.
                }

                // Writes the received bytes to the internal layer destination (<see cref="receiver"/>).
                // This method is responsible for passing the received data up to the application layer for processing.
                // <param name="src">The source buffer containing received bytes.</param>
                // <param name="src_bytes">The number of bytes received in the source buffer.</param>
                protected virtual void receive(byte[] src, int src_bytes) => receiver!.Write(src, 0, src_bytes);
#endregion

                /// <summary>
                /// Pointer to the next channel in the linked list of channels managed by the host TCP instance.
                /// Used for channel traversal and management within the TCP host.
                /// </summary>
                public Channel? next;

#region maintenance
                // Lock to control access to maintenance operations on this channel.
                // It is used to synchronize maintenance tasks with ongoing send/receive operations, ensuring data consistency and preventing race conditions.
                //
                // The lock state is represented by an integer:
                // <list type="bullet">
                //     <item>&lt; 0: Maintenance is in progress or waiting to start.</item>
                //     <item><see cref="int.MinValue"/>: System is ready for maintenance.</item>
                //     <item>&gt; 0: Number of in-progress non-maintenance operations.</item>
                // </list>
                //
                private volatile int maintenance_lock = 0;

                // Handles maintenance tasks for this channel that require exclusive access.
                // This method is called by the maintenance thread to perform channel-specific maintenance operations.
                // <returns>The time in milliseconds until the next maintenance task should be performed. Return <see cref="uint.MaxValue"/> if no further maintenance is needed immediately.</returns>
                protected internal virtual uint maintenance() { return uint.MaxValue; }

                // Checks if maintenance is currently waiting to be performed on this channel.
                // <returns><c>true</c> if maintenance is waiting (i.e., <see cref="maintenance_lock"/> is less than 0); otherwise, <c>false</c>.</returns>
                protected internal bool waiting_for_maintenance() { return maintenance_lock < 0; }

                // Checks if the channel is ready for maintenance.
                // This occurs when all non-maintenance operations have completed and the channel is in a quiescent state.
                // <returns><c>true</c> if the channel is ready for maintenance (i.e., <see cref="maintenance_lock"/> is equal to <see cref="int.MinValue"/>); otherwise, <c>false</c>.</returns>
                protected internal bool ready_for_maintenance() { return maintenance_lock == int.MinValue; }

                // Completes the maintenance process for this channel and resets the <see cref="maintenance_lock"/> to 0, allowing normal operations to resume.
                // This method should be called after maintenance tasks are finished.
                protected internal void maintenance_completed() { Interlocked.Exchange(ref maintenance_lock, 0); }

                // Signals the completion of a non-maintenance operation (send or receive) and updates the maintenance lock.
                // This method should be called after any send or receive operation completes to allow maintenance to proceed when appropriate.
                protected void pending_send_receive_completed() { Interlocked.Decrement(ref maintenance_lock); }

                // Checks if the channel is currently locked for maintenance, preventing new non-maintenance operations from starting.
                // If the channel is not locked for maintenance, it attempts to acquire a lock by incrementing the <see cref="maintenance_lock"/>.
                // <returns><c>true</c> if the channel is locked for maintenance (i.e., <see cref="maintenance_lock"/> is less than 0), indicating that no new non-maintenance operations should be started; otherwise, <c>false</c>.</returns>
                protected bool locked_for_maintenance()
                {
                    int t;
                    do
                        t = maintenance_lock;                                                      // Read current lock value.
                    while( Interlocked.CompareExchange(ref maintenance_lock, t < 0 ?               // Attempt to increment lock only if not already in maintenance.
                                                                                 t :               // If in maintenance, keep the lock value unchanged.
                                                                                 t + 1, t) != t ); // Increment lock if not in maintenance.
                    return t < 0;                                                                  // Return true if the lock was already in maintenance mode (t < 0).
                }

                // Schedules maintenance for this channel by locking it to prevent new non-maintenance operations from starting.
                // This method prepares the channel for maintenance tasks, ensuring that they can be performed without interference from ongoing I/O operations.
                protected internal void schedule_maintenance()
                {
                    int t;
                    do
                        t = maintenance_lock;                                                                 // Read current lock value.
                    while( Interlocked.CompareExchange(ref maintenance_lock, t < 0 ?                          // If already in maintenance, keep the lock value unchanged.
                                                                                 t :                          // Otherwise, set lock to int.MinValue + t to indicate maintenance scheduled.
                                                                                 int.MinValue + t, t) != t ); // Set lock to maintenance scheduled state.
                }
#endregion
            }

            /// <summary>
            /// Represents a WebSocket communication channel, extending the base <see cref="Channel"/> class with WebSocket-specific functionalities.
            /// It handles WebSocket frame encoding, decoding, and handshake processes.
            /// </summary>
            public class WebSocket : Channel{

#region> WebSocket code
#endregion> Network.TCP.WebSocket

                /// <summary>
                /// Initializes a new instance of the <see cref="WebSocket"/> class.
                /// WebSocket channels require a TCP server with a buffer size of at least 256 bytes to accommodate HTTP headers during handshake.
                /// </summary>
                /// <param name="host">The host TCP instance managing this WebSocket channel.</param>
                public WebSocket(TCP<SRC, DST> host) : base(host) { }

                /// <summary>
                /// Closes the WebSocket connection gracefully by sending a CLOSE frame with the specified code and reason.
                /// </summary>
                /// <param name="code">The close code indicating the reason for closure. See WebSocket close codes for valid values.</param>
                /// <param name="why">Optional reason for closure, providing a human-readable message. If null, only the close code is sent.</param>
                public void close_gracefully(int code, string? why)
                {
                    var frame = frames.Value!.get();               // Acquire a control frame data object from the thread-local pool for reuse.
                    frame.OPcode = Network.WebSocket.OPCode.CLOSE; // Set the opcode of the frame to CLOSE.

                    frame.buffer[0] = (byte)(code >> 8); // Set the close code's high byte in the frame buffer.
                    frame.buffer[1] = (byte)code;        // Set the close code's low byte in the frame buffer.

                    if( why == null )           // If no close reason is provided.
                        frame.buffer_bytes = 2; // Set the buffer length to just the 2-byte close code.
                    else
                    {
                        // Copy the close reason string into the frame buffer, starting after the close code.
                        for( int i = 0, max = why.Length; i < max; i++ ) frame.buffer[i + 2] = (byte)why[i];
                        frame.buffer_bytes = 2 + why.Length; // Update buffer length to include the close code and reason.
                    }

                    recycle_frame(Interlocked.Exchange(ref urgent_frame_data, frame_data)); // Mark this frame as urgent and replace any existing urgent frame.
                    onNewBytesToTransmitArrive(null);                                       // Trigger transmission to send the CLOSE frame.
                }


                /// <summary>
                /// Sends a WebSocket PING frame to check the connection health.
                /// </summary>
                /// <param name="msg">Optional message to include in the PING frame. Can be null for an empty PING.</param>
                public void ping(string? msg)
                {
                    var frame = frames.Value!.get();              // Acquire a control frame data object from the thread-local pool for reuse.
                    frame.OPcode = Network.WebSocket.OPCode.PING; // Set the opcode of the frame to PING.

                    if( msg == null )           // If no message is provided for the PING frame.
                        frame.buffer_bytes = 0; // Set buffer length to 0 for an empty PING.
                    else
                    {
                        // Copy the message string into the frame buffer.
                        for( int i = 0, max = msg.Length; i < max; i++ )
                            frame.buffer[i] = (byte)msg[i];

                        frame.buffer_bytes = msg.Length; // Set buffer length to the message length.
                    }

                    recycle_frame(Interlocked.Exchange(ref urgent_frame_data, frame_data)); // Mark this frame as urgent and replace any existing urgent frame.
                    onNewBytesToTransmitArrive(null);                                       // Trigger transmission to send the PING frame.
                }

                /// <inheritdoc/>
                public override void Close_not_dispose()
                {
                    if( ext == null ) return;                                         // Exit if socket is already null.
                    state              = Network.WebSocket.State.HANDSHAKE;           // Reset state to handshake for potential reuse.
                    sent_closing_frame = false;                                       // Reset closing frame flag.
                    frame_bytes_left   = BYTE = xor0 = xor1 = xor2 = xor3 = 0;        // Reset frame parsing variables.
                    OPcode             = Network.WebSocket.OPCode.CONTINUATION;       // Reset opcode.
                    if( frame_data        != null ) recycle_frame(frame_data);        // Recycle any pending frame data.
                    if( urgent_frame_data != null ) recycle_frame(urgent_frame_data); // Recycle any pending urgent frame data.
                    base.Close();                                                     // Call base class Close_not_dispose method.
                }

#region Transmitting
                // Flag indicating whether a closing frame has been sent.
                // Used to track the state of WebSocket closing handshake.
                private bool sent_closing_frame;

                // Volatile field to hold urgent control frame data (e.g., CLOSE, PING frames) that needs to be transmitted immediately.
                // Using volatile ensures that the latest frame data is always accessed across threads.
                volatile ControlFrameData? urgent_frame_data;

                /// <inheritdoc/>
                protected override bool transmit(byte[] dst)
                {
                    var frame_data = Interlocked.Exchange(ref urgent_frame_data, null); // Atomically get and clear any urgent frame data.

                    if( frame_data == null ) // If no urgent frame data to send.
                    {
                        frame_data = this.frame_data; // Get the current frame data.
                        if( !catch_ready_frame() )    // Try to catch a ready frame, if not ready, treat as no frame data.
                            frame_data = null;        // No frame data available to send.
                    }

                    // WebSocket Frame Structure (RFC 6455):
                    //     0                   1                   2                   3
                    //     0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1
                    //    +-+-+-+-+-------+-+-------------+-------------------------------+
                    //    |F|R|R|R| opcode|M| Payload len |    Extended payload length    |
                    //    |I|S|S|S|  (4)  |A|     (7)     |             (16/64)           |
                    //    |N|V|V|V|       |S|             |   (if payload len==126/127)   |
                    //    | |1|2|3|       |K|             |                               |
                    //    +-+-+-+-+-------+-+-------------+ - - - - - - - - - - - - - - - +

                    var s = (frame_data != null ?              // Calculate the starting offset for payload data in dst buffer.
                                 frame_data.buffer_bytes + 2 : // Control frame header size (2 bytes) + control payload size.
                                 0) + 10;                      // Add extra 10 bytes for maximum possible header size (for binary payload).

                    var len = transmitter!.Read(dst, s, dst.Length - s); // Read data from transmitter into dst buffer starting at offset 's'.

                    if( 0 < len ) // If data was read from transmitter (payload data).
                    {
                        var max = s + len; // Calculate end position of payload data in dst buffer.

                        switch( len ) // Determine payload length encoding based on 'len'.
                        {
                            case < 126:                                                                                     // Payload length is 0-125 bytes.
                                dst[s -= 2] = (int)Network.WebSocket.Mask.FIN | (int)Network.WebSocket.OPCode.BINARY_FRAME; // Set FIN bit and BINARY_FRAME opcode.
                                dst[s + 1]  = (byte)len;                                                                    // Payload length directly in the second byte.
                                break;

                            case < 0x1_0000:                                                                                // Payload length is 126-65,535 bytes.
                                dst[s -= 4] = (int)Network.WebSocket.Mask.FIN | (int)Network.WebSocket.OPCode.BINARY_FRAME; // Set FIN bit and BINARY_FRAME opcode.
                                dst[s + 1]  = 126;                                                                          // Indicate 16-bit extended payload length.
                                dst[s + 2]  = (byte)(len >> 8);                                                             // High byte of payload length.
                                dst[s + 3]  = (byte)len;                                                                    // Low byte of payload length.
                                break;

                            default:                                                                                         // Payload length is 65,536 bytes or more.
                                dst[s -= 10] = (int)Network.WebSocket.Mask.FIN | (int)Network.WebSocket.OPCode.BINARY_FRAME; // Set FIN bit and BINARY_FRAME opcode.
                                dst[s + 1]   = 127;                                                                          // Indicate 64-bit extended payload length.

                                // 8-byte extended payload length (most significant 4 bytes are zero).
                                dst[s + 2] = 0;
                                dst[s + 3] = 0;
                                dst[s + 4] = 0;
                                dst[s + 5] = 0;
                                dst[s + 6] = (byte)(len >> 24); // Bytes 6-9: 64-bit payload length.
                                dst[s + 7] = (byte)(len >> 16);
                                dst[s + 8] = (byte)(len >> 8);
                                dst[s + 9] = (byte)len;
                                break;
                        }

                        if( frame_data != null ) // If there is a control frame to send.
                        {
                            sent_closing_frame = frame_data.OPcode == Network.WebSocket.OPCode.CLOSE;   // Check if it's a CLOSE frame.
                            recycle_frame(frame_data.get_frame(dst, s -= frame_data.buffer_bytes + 2)); // Write control frame header and payload to dst.
                        }

                        SetBuffer(s, max - s); // Configure SocketAsyncEventArgs buffer for sending WebSocket frame (header + payload).
                        return true;           // Indicate that data is prepared for sending.
                    }

                    if( frame_data == null ) // If no payload data and no control frame to send.
                    {
                        SetBuffer(0, 0); // Reset buffer to default size (no data to send).
                        return false;    // Indicate no data to send.
                    }

                    sent_closing_frame = frame_data.OPcode == Network.WebSocket.OPCode.CLOSE; // Check if control frame is a CLOSE frame.
                    recycle_frame(frame_data.get_frame(dst, 0));                              // Write only control frame (header + payload) to dst.

                    SetBuffer(0, s); // Configure SocketAsyncEventArgs buffer to send only the control frame.
                    return true;     // Indicate control frame is prepared for sending.
                }
#endregion

#region Receiving
                // Current state of the WebSocket frame receiver state machine.
                // Tracks the progress of parsing incoming WebSocket frames.
                Network.WebSocket.State state = Network.WebSocket.State.HANDSHAKE;

                /// <summary>
                /// Current WebSocket opcode being processed.
                /// </summary>
                Network.WebSocket.OPCode OPcode;

                // Remaining bytes expected in the current WebSocket frame payload.
                int frame_bytes_left, // Remaining bytes in the current WebSocket frame payload.
                    BYTE,             // Temporary storage for the current byte being processed.
                    xor0,             // XOR masking key byte 0.
                    xor1,             // XOR masking key byte 1.
                    xor2,             // XOR masking key byte 2.
                    xor3;             // XOR masking key byte 3.

                // Holds the current control frame data during processing, used for operations like PING, PONG, and CLOSE.
                volatile ControlFrameData? frame_data;

                // Lock state for frame data, indicating if the frame is ready, in standby, or in use.
                // Used for managing access to <see cref="frame_data"/> in a thread-safe manner.
                volatile int frame_lock;

                // Allocates a new control frame data object with the specified WebSocket opcode.
                // This method ensures thread-safe allocation and management of control frame resources.
                // <param name="OPcode">The WebSocket opcode for the control frame to be allocated.</param>
                protected void allocate_frame_data(Network.WebSocket.OPCode OPcode)
                {
                    if( Interlocked.CompareExchange(ref frame_lock, FRAME_STANDBY, FRAME_READY) != FRAME_READY ) // Check if frame lock is in READY state, if not, proceed.
                    {
                        Interlocked.Exchange(ref frame_lock, FRAME_STANDBY); // Set frame lock to STANDBY state, indicating frame allocation is in progress.

                        frame_data = frames.Value!.get(); // Get a new ControlFrameData object from the thread-local pool.
                    }

                    frame_data.buffer_bytes = 0;      // Reset buffer byte count for the new frame data.
                    frame_data.OPcode       = OPcode; // Set the WebSocket opcode for the new frame data.
                }

                // Recycles a control frame data object back to the thread-local pool for reuse.
                // This method ensures efficient resource management by returning frame objects when they are no longer needed.
                // <param name="frame">The ControlFrameData object to recycle.</param>
                protected void recycle_frame(ControlFrameData? frame)
                {
                    if( frame == null ) return; // If frame is null, nothing to recycle, just return.

                    Interlocked.CompareExchange(ref frame_data, null, frame); // Atomically compare and exchange frame_data with null if it matches the frame to be recycled.

                    frames.Value!.put(frame); // Return the frame object to the thread-local pool for reuse.
                }

                // Marks the current frame data as ready for transmission and triggers the transmission process.
                // This method is used after preparing a control frame to signal that it is ready to be sent.
                protected void frame_ready()
                {
                    Interlocked.Exchange(ref frame_lock, FRAME_READY); // Set frame lock to READY state, indicating frame is ready for transmission.
                    onNewBytesToTransmitArrive(null);                  // Trigger transmission process to send the ready frame.
                }

                // Checks if the frame is in a ready state and atomically resets the frame lock.
                // This method is used to check and claim a ready frame for processing in a thread-safe manner.
                // <returns><c>true</c> if the frame was in a ready state and successfully reset; otherwise, <c>false</c>.</returns>
                protected bool catch_ready_frame() => Interlocked.CompareExchange(ref frame_lock, 0, FRAME_READY) == FRAME_READY;

                // Constant representing the frame lock state when a frame is in standby or being allocated.
                protected const int FRAME_STANDBY = 1;

                // Constant representing the frame lock state when a frame is ready for transmission.
                protected const int FRAME_READY = 2;

                // Helper class to manage control frame data for WebSocket communication.
                // Includes buffer for payload, SHA-1 for handshake, and methods for frame preparation.
                protected class ControlFrameData{
                    /// <summary>
                    /// WebSocket operation code for this control frame (e.g., CLOSE, PING, PONG).
                    /// </summary>
                    public Network.WebSocket.OPCode OPcode;

                    /// <summary>
                    /// Length of the payload data currently stored in the buffer.
                    /// For control frames, the payload must be <= 125 bytes and cannot be fragmented.
                    /// </summary>
                    public int buffer_bytes;

                    // Buffer to store the payload data of the control frame.
                    // Maximum size is 125 bytes as per WebSocket specification for control frames.
                    public readonly byte[] buffer = new byte[125];

                    // SHA-1 cryptographic hash algorithm instance used for WebSocket handshake key generation.
                    public readonly SHA1 sha = SHA1.Create();

                    // Constructs the WebSocket handshake response and writes it into the destination buffer.
                    // <param name="src">Source buffer containing the HTTP Upgrade request header, specifically looking for 'Sec-WebSocket-Key'.</param>
                    // <param name="dst">Destination buffer where the WebSocket handshake response will be written.</param>
                    // <param name="pos">Starting position in the source buffer to begin searching for 'Sec-WebSocket-Key'.</param>
                    // <param name="max">Maximum index to search in the source buffer.</param>
                    // <returns>The total length of the WebSocket handshake response written into the destination buffer.</returns>
                    public int put_UPGRAGE_WEBSOCKET_responce_into(byte[] src, byte[] dst, int pos, int max)
                    {
                        var len = 0; // Initialize length counter.

                        // Extract 'Sec-WebSocket-Key' from the HTTP Upgrade request header.
                        for( int b; pos < max && (b = src[pos]) != '\r'; pos++, len++ ) // Iterate until carriage return '\r' is found.
                            buffer[len] = (byte)b;                                      // Copy 'Sec-WebSocket-Key' value to buffer.

                        GUID.CopyTo(buffer, len); // Append WebSocket GUID to the key in the buffer for SHA-1 hashing.

                        sha.TryComputeHash(new ReadOnlySpan<byte>(buffer, 0, len + GUID.Length), buffer, out len); // Compute SHA-1 hash.

                        UPGRAGE_WEBSOCKET.CopyTo((Span<byte>)dst); // Copy the pre-defined WebSocket upgrade response header template to dst.

                        len = base64(buffer, 0, len, dst, UPGRAGE_WEBSOCKET.Length); // Base64 encode the SHA-1 hash and append to dst.

                        rnrn.CopyTo(dst, len); // Append "\r\n\r\n" to dst to complete HTTP headers.

                        return len + rnrn.Length; // Return total length of the handshake response.
                    }

                    // Static GUID (Globally Unique Identifier) used in WebSocket handshake process for key derivation.
                    static readonly byte[] GUID = Encoding.ASCII.GetBytes("258EAFA5-E914-47DA-95CA-C5AB0DC85B11");

                    // Static byte array representing the carriage return and newline sequence "\r\n\r\n", used to terminate HTTP headers.
                    static readonly byte[] rnrn = Encoding.ASCII.GetBytes("\r\n\r\n");

                    // Static byte array containing the HTTP headers for a WebSocket upgrade response.
                    // Includes placeholders for dynamic content like 'Sec-WebSocket-Accept'.
                    static readonly byte[] UPGRAGE_WEBSOCKET = Encoding.ASCII.GetBytes(
                                                                                       "HTTP/1.1 101 Switching Protocols\r\n" +
                                                                                       "Server: AdHoc\r\n"                    +
                                                                                       "Connection: Upgrade\r\n"              +
                                                                                       "Upgrade: websocket\r\n"               +
                                                                                       "Sec-WebSocket-Accept: "
                                                                                      );

                    // Encodes a portion of the source byte array into Base64 format and writes it to the destination byte array.
                    // <param name="src">Source byte array to be encoded.</param>
                    // <param name="off">Starting offset in the source array.</param>
                    // <param name="end">Ending offset in the source array (exclusive).</param>
                    // <param name="dst">Destination byte array to write the Base64 encoded string.</param>
                    // <param name="dst_pos">Starting position in the destination array to begin writing.</param>
                    // <returns>The new position in the destination array after writing the Base64 encoded string.</returns>
                    private int base64(byte[] src, int off, int end, byte[] dst, int dst_pos)
                    {
                        for( var max = off + (end - off) / 3 * 3; off < max; ) // Process in chunks of 3 bytes.
                        {
                            var bits = (src[off++] & 0xff) << 16 | (src[off++] & 0xff) << 8 | (src[off++] & 0xff); // Combine 3 bytes into a 24-bit integer.
                            dst[dst_pos++] = base64_[(bits >> 18) & 0x3f];                                         // First 6 bits.
                            dst[dst_pos++] = base64_[(bits >> 12) & 0x3f];                                         // Second 6 bits.
                            dst[dst_pos++] = base64_[(bits >> 6)  & 0x3f];                                         // Third 6 bits.
                            dst[dst_pos++] = base64_[bits         & 0x3f];                                         // Last 6 bits.
                        }

                        if( off == end ) return dst_pos;  // No remaining bytes.
                        var b = src[off++] & 0xff;        // Get the remaining byte.
                        dst[dst_pos++] = base64_[b >> 2]; // First 6 bits of the byte.

                        if( off == end ) // Only 1 byte remaining.
                        {
                            dst[dst_pos++] = base64_[(b << 4) & 0x3f]; // Next 6 bits from padding.
                            dst[dst_pos++] = (byte)'=';                // Padding character.
                            dst[dst_pos++] = (byte)'=';                // Padding character.
                        }
                        else // 2 bytes remaining.
                        {
                            dst[dst_pos++] = base64_[(b << 4) & 0x3f | ((b = src[off] & 0xff) >> 4)];        // Combine bits from both bytes.
                            dst[dst_pos++] = base64_[(b                                       << 2) & 0x3f]; // Last 6 bits and padding.
                            dst[dst_pos++] = (byte)'=';                                                      // Padding character.
                        }

                        return dst_pos; // Return new position in destination buffer.
                    }

                    // Static byte array representing the Base64 encoding character set.
                    static readonly byte[] base64_ = Encoding.ASCII.GetBytes("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/");

                    // Constructs the WebSocket control frame header and payload, writing it to the destination buffer.
                    // <param name="dst">Destination buffer where the WebSocket frame will be written.</param>
                    // <param name="dst_byte">Starting position in the destination buffer to begin writing.</param>
                    // <returns>Returns the current <see cref="ControlFrameData"/> instance for method chaining.</returns>
                    public ControlFrameData get_frame(byte[] dst, int dst_byte)
                    {
                        dst[dst_byte++] = (byte)((int)Network.WebSocket.Mask.FIN | (int)OPcode); // Write FIN bit and Opcode.
                        dst[dst_byte++] = (byte)buffer_bytes;                                    // Write payload length.

                        if( buffer_bytes > 0 )                                  // If there is payload data.
                            Array.Copy(buffer, 0, dst, dst_byte, buffer_bytes); // Copy payload data to destination buffer.

                        return this; // Return current instance for chaining.
                    }

                    // Appends data from a source byte array to the control frame's internal buffer.
                    // <param name="src">Source byte array to copy data from.</param>
                    // <param name="start">Starting index in the source array.</param>
                    // <param name="end">Ending index in the source array (exclusive).</param>
                    internal void put_data(byte[] src, int start, int end)
                    {
                        var bytes = end - start;                             // Calculate number of bytes to copy.
                        Array.Copy(src, start, buffer, buffer_bytes, bytes); // Copy bytes to buffer.
                        buffer_bytes += bytes;                               // Update buffer byte count.
                    }
                }


                // Thread-local storage for a pool of <see cref="ControlFrameData"/> objects.
                // Used to reuse control frame data objects and reduce memory allocation overhead.
                protected static readonly ThreadLocal<Pool<ControlFrameData>> frames = new(() => new Pool<ControlFrameData>(() => new ControlFrameData()));

                // Static byte array representing the Boyer-Moore search pattern for "Sec-WebSocket-Key: ".
                // Used for efficient searching in HTTP headers during WebSocket handshake.
                private static uint[] Sec_Websocket_Key_ = AdHoc.boyer_moore_pattern("Sec-Websocket-Key: ");

                /// <summary>
                /// Parses the HTTP header of a WebSocket handshake request to extract the 'Sec-WebSocket-Key' and construct the upgrade response.
                /// </summary>
                /// <param name="bytes">Byte array containing the HTTP header to parse.</param>
                /// <param name="max">Maximum index to parse in the byte array.</param>
                public virtual void parsing_HTTP_header(byte[] bytes, int max)
                {
                    if( 0 < Count ) return; // If transmit buffer already has data, handshake might be already processed, return.

                    var pos = AdHoc.boyer_moore_ASCII_Case_insensitive(bytes, Sec_Websocket_Key_); // Search for 'Sec-WebSocket-Key:' header.
                    if( pos == -1 ) return;                                                        // If header not found, return.

                    var pool   = frames.Value!; // Get the thread-local pool of ControlFrameData objects.
                    var helper = pool.get();    // Get a ControlFrameData object from the pool.

                    SetBuffer(0, helper.put_UPGRAGE_WEBSOCKET_responce_into(bytes, Buffer!, pos, max)); // Construct and set WebSocket upgrade response in buffer.

                    pool.put(helper); // Return the ControlFrameData object back to the pool.
                }

                /// <inheritdoc/>
                protected override void receive(byte[] src, int src_bytes)
                {
                    for( int start = 0, index = 0;; ) // Main state machine loop for WebSocket frame parsing.
                        switch( state )
                        {
                            case Network.WebSocket.State.HANDSHAKE: // State: Waiting for WebSocket handshake to complete.

                                if(
                                    src[src_bytes - 4] == (byte)'\r' && // Check if the last 4 bytes are \r\n\r\n, indicating end of HTTP headers.
                                    src[src_bytes - 3] == (byte)'\n' &&
                                    src[src_bytes - 2] == (byte)'\r' &&
                                    src[src_bytes - 1] == (byte)'\n' )
                                {
                                    parsing_HTTP_header(src, src_bytes); // Parse HTTP headers to generate WebSocket handshake response.

                                    if( Count == 0 ) // Check if handshake response was generated (Sec-WebSocket-Key found).
                                    {
                                        host.onFailure(this, new Exception("Sec-WebSocket-Key in the header not found, Unexpected handshake :" + Encoding.ASCII.GetString(src, 0, src_bytes))); // Handle handshake failure.
                                        Close_and_dispose();                                                                                                                                    // Close and dispose channel on handshake failure.
                                        return;
                                    }

                                    state = Network.WebSocket.State.NEW_FRAME;                     // Transition to NEW_FRAME state after successful handshake.
                                    Interlocked.Exchange(ref transmit_lock, ext!.SendAsync(this) ? // Trigger transmission of handshake response.
                                                                                1 :                // If async, set transmit_lock to 1 (in use).
                                                                                0);                // If sync, set transmit_lock to 0 (idle).

                                    host.onEvent(this, (int)Network.WebSocket.Event.EXT_INT_CONNECT); // Notify host of successful WebSocket connection.
                                    return;
                                }

                                var n = src_bytes - 1;                   // Start from the last byte and move backwards.
                                for( ; -1 < n && src[n] != '\n'; n-- ) ; // Find last newline character '\n' to process complete lines.
                                if( n == -1 )                            // No newline found in received data.
                                {
                                    host.onFailure(this, new Exception("No \\r\\n found in the received header, likely due to insufficient buffer size or invalid data. Unexpected handshake: " + Encoding.ASCII.GetString(src, 0, src_bytes))); // Handle incomplete header.
                                    Close_and_dispose();                                                                                                                                                                                         // Close and dispose channel due to incomplete header.
                                    return;
                                }

                                parsing_HTTP_header(src, n); // Parse HTTP headers up to the last newline.

                                Array.Copy(src, n, src, 0, src_bytes -= n + 1); // Shift unprocessed data (after newline) to the beginning of buffer.
                                receive_mate.SetBuffer(src_bytes, src.Length);  // Adjust receive buffer offset and count for next receive operation.

                                return;
                            case Network.WebSocket.State.NEW_FRAME: // State: Ready to parse a new WebSocket frame header.

                                if( !get_byte(Network.WebSocket.State.NEW_FRAME, ref index, src_bytes) )           // Get the first byte of the frame header.
                                    return;                                                                        // Not enough bytes, return and wait for more.
                                OPcode = (Network.WebSocket.OPCode)(BYTE & (int)Network.WebSocket.Mask.OPCODE);    // Extract opcode from the first byte.
                                goto case Network.WebSocket.State.PAYLOAD_LENGTH_BYTE;                             // Move to payload length byte parsing state.
                            case Network.WebSocket.State.PAYLOAD_LENGTH_BYTE:                                      // State: Parsing payload length byte.
                                if( !get_byte(Network.WebSocket.State.PAYLOAD_LENGTH_BYTE, ref index, src_bytes) ) // Get the payload length byte.
                                    return;                                                                        // Not enough bytes, return and wait for more.

                                if( (BYTE & (int)Network.WebSocket.Mask.FIN) == 0 ) // Check for Mask bit, server MUST NOT receive masked frames.
                                {
                                    host.onFailure(this, new Exception("Frames sent from client to server have MASK bit set to 1")); // Handle masked frame error.
                                    Close();                                                                                         // Close connection due to invalid masking.
                                    return;
                                }

                                xor0 = 0;                                                               // Initialize extended payload length bytes counter.
                                if( 125 < (frame_bytes_left = BYTE & (int)Network.WebSocket.Mask.LEN) ) // Extract payload length from byte.
                                {
                                    xor0 = frame_bytes_left == 126 ? // Check for extended payload length (16-bit or 64-bit).
                                               2 :
                                               8;
                                    frame_bytes_left = 0; // Reset frame_bytes_left, as it will be calculated from extended bytes.
                                }

                                goto case Network.WebSocket.State.PAYLOAD_LENGTH_BYTES;                                // Move to extended payload length bytes parsing state.
                            case Network.WebSocket.State.PAYLOAD_LENGTH_BYTES:                                         // State: Parsing extended payload length bytes (if any).
                                for( ; 0 < xor0; xor0-- )                                                              // Loop for extended payload length bytes (2 or 8 bytes).
                                    if( get_byte(Network.WebSocket.State.PAYLOAD_LENGTH_BYTES, ref index, src_bytes) ) // Get each extended length byte.
                                        frame_bytes_left = (frame_bytes_left << 8) | BYTE;                             // Reconstruct payload length from bytes.
                                    else
                                        return;                                                    // Not enough bytes for extended length, return and wait for more.
                                goto case Network.WebSocket.State.XOR0;                            // Move to XOR key byte 0 parsing state.
                            case Network.WebSocket.State.XOR0:                                     // State: Parsing XOR key byte 0.
                                if( get_byte(Network.WebSocket.State.XOR0, ref index, src_bytes) ) // Get XOR key byte 0.
                                    xor0 = BYTE;                                                   // Store XOR key byte 0.
                                else
                                    return;                                                        // Not enough bytes, return and wait for more.
                                goto case Network.WebSocket.State.XOR1;                            // Move to XOR key byte 1 parsing state.
                            case Network.WebSocket.State.XOR1:                                     // State: Parsing XOR key byte 1.
                                if( get_byte(Network.WebSocket.State.XOR1, ref index, src_bytes) ) // Get XOR key byte 1.
                                    xor1 = BYTE;                                                   // Store XOR key byte 1.
                                else
                                    return;                                                        // Not enough bytes, return and wait for more.
                                goto case Network.WebSocket.State.XOR2;                            // Move to XOR key byte 2 parsing state.
                            case Network.WebSocket.State.XOR2:                                     // State: Parsing XOR key byte 2.
                                if( get_byte(Network.WebSocket.State.XOR2, ref index, src_bytes) ) // Get XOR key byte 2.
                                    xor2 = BYTE;                                                   // Store XOR key byte 2.
                                else
                                    return;                                                        // Not enough bytes, return and wait for more.
                                goto case Network.WebSocket.State.XOR3;                            // Move to XOR key byte 3 parsing state.
                            case Network.WebSocket.State.XOR3:                                     // State: Parsing XOR key byte 3.
                                if( get_byte(Network.WebSocket.State.XOR3, ref index, src_bytes) ) // Get XOR key byte 3.
                                    xor3 = BYTE;                                                   // Store XOR key byte 3.
                                else
                                    return; // Not enough bytes, return and wait for more.

                                switch( OPcode ) // Handle opcode-specific logic after header parsing.
                                {
                                    case Network.WebSocket.OPCode.PING:                     // Opcode: PING.
                                        allocate_frame_data(Network.WebSocket.OPCode.PONG); // Prepare PONG response frame.

                                        if( frame_bytes_left == 0 ) // Empty PING frame.
                                        {
                                            host.onEvent(this, (int)Network.WebSocket.Event.PING); // Notify host of PING event.
                                            frame_ready();                                         // Mark PONG frame as ready for sending.
                                            state = Network.WebSocket.State.NEW_FRAME;             // Reset state to NEW_FRAME for next frame.
                                            continue;                                              // Continue processing next frame immediately.
                                        }

                                        break;

                                    case Network.WebSocket.OPCode.CLOSE: // Opcode: CLOSE.

                                        if( sent_closing_frame ) // Check if we already sent a CLOSE frame (confirmation).
                                        {
                                            host.onEvent(this, (int)Network.WebSocket.Event.CLOSE); // Notify host of CLOSE event.
                                            Close();                                                // Gracefully close the connection (confirmation received).
                                            return;
                                        }

                                        allocate_frame_data(Network.WebSocket.OPCode.CLOSE); // Prepare CLOSE confirmation frame.

                                        if( frame_bytes_left == 0 ) // Empty CLOSE frame.
                                        {
                                            host.onEvent(this, (int)Network.WebSocket.Event.CLOSE); // Notify host of CLOSE event.
                                            frame_ready();                                          // Mark CLOSE confirmation frame as ready for sending.
                                            state = Network.WebSocket.State.NEW_FRAME;              // Reset state to NEW_FRAME for next frame.
                                            continue;                                               // Continue processing next frame immediately.
                                        }

                                        break;
                                    case Network.WebSocket.OPCode.PONG: // Opcode: PONG.
                                        // Unsolicited PONG frames are valid heartbeats, no response needed.
                                        host.onEvent(this, (int)Network.WebSocket.Event.PONG); // Notify host of PONG event.
                                        state = frame_bytes_left == 0 ?                        // Determine next state based on payload length.
                                                    Network.WebSocket.State.NEW_FRAME :        // If payload is empty, go to NEW_FRAME.
                                                    Network.WebSocket.State.DISCARD;           // If payload exists, discard it.
                                        continue;                                              // Continue processing next frame immediately.
                                    default:                                                   // Default case for other opcodes (BINARY_FRAME, TEXT_FRAME, etc.).
                                        if( frame_bytes_left == 0 )                            // Empty data frame.
                                        {
                                            host.onEvent(this, (int)Network.WebSocket.Event.EMPTY_FRAME); // Notify host of EMPTY_FRAME event.
                                            state = Network.WebSocket.State.NEW_FRAME;                    // Reset state to NEW_FRAME for next frame.
                                            continue;                                                     // Continue processing next frame immediately.
                                        }

                                        break;
                                }

                                start = index;                                                                        // Mark start of payload data for processing.
                                goto case Network.WebSocket.State.DATA0;                                              // Move to DATA0 state to start payload processing.
                            case Network.WebSocket.State.DATA0:                                                       // State: Processing payload data byte 0.
                                if( decode_and_continue(start, ref index, src_bytes) )                                // Process data bytes with XOR masking and check for continuation.
                                    continue;                                                                         // If frame processing continues, proceed to next iteration.
                                return;                                                                               // If not enough bytes or frame processing paused, return and wait for more data.
                            case Network.WebSocket.State.DATA1:                                                       // State: Processing payload data byte 1.
                                if( need_more_bytes(Network.WebSocket.State.DATA1, ref start, ref index, src_bytes) ) // Check if more bytes needed.
                                    return;                                                                           // If more bytes needed, return and wait for more data.
                                if( decode_byte_and_continue(xor1, ref start, ref index) )                            // Decode byte with XOR mask 1 and check for continuation.
                                    continue;                                                                         // If frame processing continues, proceed to next iteration.

                                goto case Network.WebSocket.State.DATA2;                                              // Move to DATA2 state.
                            case Network.WebSocket.State.DATA2:                                                       // State: Processing payload data byte 2.
                                if( need_more_bytes(Network.WebSocket.State.DATA2, ref start, ref index, src_bytes) ) // Check if more bytes needed.
                                    return;                                                                           // If more bytes needed, return and wait for more data.
                                if( decode_byte_and_continue(xor2, ref start, ref index) ) continue;                  // Decode byte with XOR mask 2 and check for continuation.

                                goto case Network.WebSocket.State.DATA3;                                              // Move to DATA3 state.
                            case Network.WebSocket.State.DATA3:                                                       // State: Processing payload data byte 3.
                                if( need_more_bytes(Network.WebSocket.State.DATA3, ref start, ref index, src_bytes) ) // Check if more bytes needed.
                                    return;                                                                           // If more bytes needed, return and wait for more data.
                                if( decode_byte_and_continue(xor3, ref start, ref index) ) continue;                  // Decode byte with XOR mask 3 and check for continuation.

                                if( decode_and_continue(start, ref index, src_bytes) ) // Process remaining data bytes with XOR masking and check for continuation.
                                    continue;                                          // If frame processing continues, proceed to next iteration.
                                return;                                                // If not enough bytes or frame processing paused, return and wait for more data.

                            case Network.WebSocket.State.DISCARD:                          // State: Discarding remaining payload bytes.
                                var bytes = Math.Min(src_bytes - start, frame_bytes_left); // Calculate bytes to discard.
                                index += bytes;                                            // Discard bytes by advancing index.
                                if( (frame_bytes_left -= bytes) == 0 )                     // Check if all remaining bytes are discarded.
                                {
                                    state = Network.WebSocket.State.NEW_FRAME; // Reset state to NEW_FRAME after discarding all bytes.
                                    continue;                                  // Continue processing next frame immediately.
                                }

                                state = Network.WebSocket.State.DISCARD; // Remain in DISCARD state if more bytes need discarding.
                                return;                                  // Return and wait for more data for discarding.
                        }
                }

                // Decodes a block of payload data using XOR masking and continues processing until all data is processed or more bytes are needed.
                // <param name="start">Starting index of the data block in the receive buffer.</param>
                // <param name="index">Current index in the receive buffer, updated during processing.</param>
                // <param name="max">Maximum index in the receive buffer (exclusive).</param>
                // <returns><c>true</c> if frame processing continues (more bytes in the frame); <c>false</c> if frame processing is paused (need more bytes).</returns>
                bool decode_and_continue(int start, ref int index, int max)
                {
                    for( ;; ) // Loop to process data bytes in blocks of 4 (XOR key length).
                    {
                        if( need_more_bytes(Network.WebSocket.State.DATA0, ref start, ref index, max) ) return false; // Check for byte 0 and return if needed.
                        if( decode_byte_and_continue(xor0, ref start, ref index) ) return true;                       // Decode byte 0 and continue if frame processing continues.
                        if( need_more_bytes(Network.WebSocket.State.DATA1, ref start, ref index, max) ) return false; // Check for byte 1 and return if needed.
                        if( decode_byte_and_continue(xor1, ref start, ref index) ) return true;                       // Decode byte 1 and continue if frame processing continues.
                        if( need_more_bytes(Network.WebSocket.State.DATA2, ref start, ref index, max) ) return false; // Check for byte 2 and return if needed.
                        if( decode_byte_and_continue(xor2, ref start, ref index) ) return true;                       // Decode byte 2 and continue if frame processing continues.
                        if( need_more_bytes(Network.WebSocket.State.DATA3, ref start, ref index, max) ) return false; // Check for byte 3 and return if needed.
                        if( decode_byte_and_continue(xor3, ref start, ref index) ) return true;                       // Decode byte 3 and continue if frame processing continues.
                    }
                }

                // Checks if more bytes are needed to continue processing the WebSocket frame.
                // If not enough bytes are available, it updates the receiver state and returns <c>true</c>.
                // <param name="state_if_no_more_bytes">State to set if more bytes are needed.</param>
                // <param name="start">Starting index of the current data block in the receive buffer.</param>
                // <param name="index">Current index in the receive buffer.</param>
                // <param name="max">Maximum index in the receive buffer (exclusive).</param>
                // <returns><c>true</c> if more bytes are needed; <c>false</c> if enough bytes are available.</returns>
                bool need_more_bytes(Network.WebSocket.State state_if_no_more_bytes, ref int start, ref int index, int max)
                {
                    if( index < max ) return false; // Enough bytes available, return false.

                    var src = receive_mate.Buffer!; // Get receive buffer.
                    switch( OPcode )                // Handle opcode-specific actions when more bytes are needed.
                    {
                        case Network.WebSocket.OPCode.PING:          // Opcode: PING.
                        case Network.WebSocket.OPCode.CLOSE:         // Opcode: CLOSE.
                            frame_data!.put_data(src, start, index); // Append received data to control frame buffer.
                            break;
                        default: // Default case for other opcodes (BINARY_FRAME, TEXT_FRAME, etc.).

                            receiver!.Write(src, start, index - start); // Write received payload data to receiver destination.
                            break;
                    }

                    state = frame_bytes_left == 0 ?                 // Determine next state based on remaining payload bytes.
                                Network.WebSocket.State.NEW_FRAME : // If no more payload bytes expected, go to NEW_FRAME.
                                state_if_no_more_bytes;             // Otherwise, remain in the current data processing state.
                    return true;                                    // Indicate that more bytes are needed.
                }

                // Decodes a single byte using the provided XOR key, writes the decoded byte to the receiver, and checks if frame processing should continue.
                // <param name="XOR">XOR key byte for decoding.</param>
                // <param name="start">Starting index of the current data block in the receive buffer.</param>
                // <param name="index">Current index in the receive buffer.</param>
                // <returns><c>true</c> if frame processing continues (more bytes in the frame); <c>false</c> if frame processing is paused (end of frame payload).</returns>
                bool decode_byte_and_continue(int XOR, ref int start, ref int index)
                {
                    var src = receive_mate.Buffer!; // Get receive buffer.

                    src[index] = (byte)(src[index++] ^ XOR);   // Decode byte using XOR key and update in buffer.
                    if( 0 < --frame_bytes_left ) return false; // Decrement remaining byte count and return false if more bytes are expected.

                    state = Network.WebSocket.State.NEW_FRAME; // Reset state to NEW_FRAME as payload processing is complete.

                    switch( OPcode ) // Handle opcode-specific actions after payload byte decoding.
                    {
                        case Network.WebSocket.OPCode.PING:          // Opcode: PING.
                        case Network.WebSocket.OPCode.CLOSE:         // Opcode: CLOSE.
                            frame_data!.put_data(src, start, index); // Append decoded payload data to control frame buffer.
                            host.onEvent(this, (int)OPcode);         // Notify host of PING or CLOSE event.
                            frame_ready();                           // Mark control frame as ready for sending (PONG or CLOSE confirmation).
                            return true;                             // Indicate frame processing continues (next frame expected).
                        default:                                     // Default case for other opcodes (BINARY_FRAME, TEXT_FRAME, etc.).

                            receiver!.Write(src, start, index - start); // Write decoded payload data to receiver destination.
                            return true;                                // Indicate frame processing continues (next frame expected).
                    }
                }

                // Gets a single byte from the receive buffer and updates the state if not enough bytes are available.
                // <param name="state_if_no_more_bytes">State to set if no more bytes are available.</param>
                // <param name="index">Current index in the receive buffer.</param>
                // <param name="max">Maximum index in the receive buffer (exclusive).</param>
                // <returns><c>true</c> if a byte was successfully retrieved; <c>false</c> if no more bytes are available.</returns>
                bool get_byte(Network.WebSocket.State state_if_no_more_bytes, ref int index, int max)
                {
                    if( index == max ) // Check if index reached the end of available bytes.
                    {
                        state = state_if_no_more_bytes; // Set state to indicate waiting for more bytes.
                        return false;                   // Indicate no byte retrieved.
                    }

                    BYTE = receive_mate.Buffer![index++]; // Retrieve byte from buffer and increment index.
                    return true;                          // Indicate byte retrieved successfully.
                }
#endregion

                /// <summary>
                /// Represents a WebSocket client implementation, extending <see cref="TCP{SRC, DST}.Client"/> for WebSocket protocol support.
                /// Manages WebSocket connections, data transmission, and reception using .NET's <see cref="ClientWebSocket"/>.
                /// </summary>
                public class Client : TCP<SRC, DST>.Client{

#region > WebSocket Client code
#endregion > Network.TCP.WebSocket.Client

                    // .NET <see cref="ClientWebSocket"/> instance used for managing the WebSocket connection.
                    private ClientWebSocket ws;

                    /// <summary>
                    /// Factory function to create a new <see cref="ClientWebSocket"/> instance.
                    /// Allows for dependency injection, especially useful for testing and mocking.
                    /// </summary>
                    public Func<ClientWebSocket> newClientWebSocket = () => new ClientWebSocket();

                    // Lock variable to synchronize transmissions, preventing concurrent writes to the WebSocket.
                    private volatile int transmit_lock = 1;

                    /// <summary>
                    /// Size of the buffer used for data transmission and reception in bytes.
                    /// </summary>
                    public readonly int bufferSize;

                    // Cancellation token source to manage the lifecycle of the WebSocket connection and asynchronous operations.
                    // Allows for graceful cancellation of ongoing operations and connection closure.
                    private readonly CancellationTokenSource cts = new CancellationTokenSource();

                    // Flag to prevent multiple concurrent connection attempts.
                    // Ensures that only one connection process is active at any time.
                    private volatile bool isConnecting;

                    /// <summary>
                    /// URI of the server to which the client is currently connected.
                    /// </summary>
                    public Uri server { get; private set; }

                    /// <inheritdoc/>
                    public Client(string name, Func<TCP<SRC, DST>, Channel> new_channel, int bufferSize)
                        : base(name, new_channel, bufferSize)
                    {
                        this.bufferSize = bufferSize; // Initialize buffer size for WebSocket client.
                    }

                    /// <summary>
                    /// Initiates a WebSocket connection to the specified server URI with a default timeout of 5 seconds.
                    /// </summary>
                    /// <param name="server">The URI of the WebSocket server to connect to.</param>
                    /// <param name="onConnected">Action to be executed upon successful connection. Provides the transmitter for sending data.</param>
                    /// <param name="onConnectingFailure">Action to be executed if the connection fails. Provides the exception that caused the failure.</param>
                    public void Connect(Uri server, Action<SRC> onConnected, Action<Exception> onConnectingFailure) => Connect(server, onConnected, onConnectingFailure, TimeSpan.FromSeconds(5));

                    // Asynchronously connects to the specified WebSocket server URI with a configurable timeout.
                    // Handles connection lifecycle, data transmission, reception, and callbacks for success and failure.
                    // <param name="server">The URI of the WebSocket server to connect to.</param>
                    // <param name="onConnected">Action to be executed upon successful connection. Provides the transmitter for sending data.</param>
                    // <param name="onConnectingFailure">Action to be executed if the connection fails. Provides the exception that caused the failure.</param>
                    // <param name="connectingTimout">Timeout duration for the connection attempt.</param>
                    public async void Connect(Uri server, Action<SRC> onConnected, Action<Exception> onConnectingFailure, TimeSpan connectingTimout)
                    {
                        if( isConnecting || ws?.State == WebSocketState.Open ) // Check if already connecting or connected.
                        {
                            onConnectingFailure(new InvalidOperationException("Connection already in progress or established")); // Report error if already connecting/connected.
                            return;                                                                                              // Exit if connection already in progress or established.
                        }

                        isConnecting  = true;                 // Set connecting flag to prevent concurrent connection attempts.
                        this.server   = server;               // Store server URI.
                        transmit_lock = 1;                    // Initialize transmit lock to locked state.
                        ws            = newClientWebSocket(); // Create a new ClientWebSocket instance using the factory.

                        var transmit_buffer = new byte[bufferSize]; // Allocate transmit buffer from buffer pool.
                        var receive_buffer  = new byte[bufferSize]; // Allocate receive buffer from buffer pool.

                        ws.Options.SetBuffer(bufferSize, bufferSize, receive_buffer); // Set WebSocket buffer options.

                        async void transmitting(AdHoc.BytesSrc src) // Local function for asynchronous data transmission.
                        {
                            if( Interlocked.Exchange(ref transmit_lock, 1) == 1 ) // Check and lock transmit lock, return if already locked.
                                return;                                           // Already transmitting, return.

                            for( int len; 0 < (len = channels.transmitter!.Read(transmit_buffer, 0, transmit_buffer.Length)); )                      // Read data from transmitter.
                                await ws.SendAsync(new ReadOnlyMemory<byte>(transmit_buffer, 0, len), WebSocketMessageType.Binary, true, cts.Token); // Send data over WebSocket.

                            transmit_lock = 0;                            // Release transmit lock after transmission completion.
                            channels.on_all_packs_sent?.Invoke(channels); // Invoke 'on_all_packs_sent' event.
                        }

                        async Task receiving() // Local async function for receiving WebSocket data.
                        {
                            while( !cts.Token.IsCancellationRequested && ws.State == WebSocketState.Open ) // Loop while not cancelled and connection is open.
                                try
                                {
                                    var result = await ws.ReceiveAsync(new ArraySegment<byte>(receive_buffer), cts.Token); // Asynchronously receive data.

                                    if( result.MessageType == WebSocketMessageType.Close ) // Check for close message type.
                                    {
                                        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Bye", cts.Token); // Gracefully close WebSocket.
                                        return;                                                                    // Exit receive loop on close message.
                                    }

                                    channels.receiver!.Write(receive_buffer, 0, result.Count); // Write received data to receiver.
                                }
                                catch( OperationCanceledException ) when( cts.Token.IsCancellationRequested )
                                { /* Ignore cancellation exceptions */
                                }
                                catch( Exception ) // Handle other exceptions during receive.
                                {
                                    onEvent(channels, (int)Network.Channel.Event.EXT_INT_DISCONNECT); // Notify host of disconnect event.
                                    break;                                                            // Exit receive loop on exception.
                                }
                        }

                        void CloseWebSocket() // Local function to gracefully close WebSocket.
                        {
                            if( ws?.State == WebSocketState.Open )                                                                      // Check if WebSocket is open.
                                try { ws.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, CancellationToken.None).Wait(); } // Gracefully close WebSocket.
                                catch( Exception )
                                { /* Ignore exceptions during close operation */
                                }
                        }

                        channels.on_disposed = _ => // Set 'on_disposed' event handler for channel.
                                               {
                                                   cts.Cancel();     // Cancel all pending operations.
                                                   CloseWebSocket(); // Close WebSocket connection.
                                               };

                        try
                        {
                            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token); // Create linked cancellation token source for connection timeout.
                            connectCts.CancelAfter(connectingTimout);                                          // Set connection timeout.

                            await ws.ConnectAsync(server, connectCts.Token); // Asynchronously connect to WebSocket server.

                            onConnected(channels.transmitter!);                                      // Invoke 'onConnected' callback on successful connection.
                            transmit_lock = 0;                                                       // Release transmit lock after connection.
                            channels.transmitter!.subscribeOnNewBytesToTransmitArrive(transmitting); // Subscribe transmitter to 'transmitting' function.

                            _ = Task.Run(receiving); // Start receiving data in a separate task.
                        }
                        catch( Exception ex ) { onConnectingFailure(ex); } // Handle connection exceptions and invoke 'onConnectingFailure' callback.

                        toString = new StringBuilder(50) // Build string representation of client.
                                   .Append("Client ")
                                   .Append(name)
                                   .Append(" -> ")
                                   .Append(server)
                                   .ToString();
                        isConnecting = false; // Reset connecting flag after connection attempt.
                    }

                    // String representation of the WebSocket client, including name and server URI.
                    private string toString;

                    /// <inheritdoc/>
                    public override string ToString() => toString;

                    /// <summary>
                    /// Asynchronously disconnects the WebSocket client and releases resources.
                    /// </summary>
                    /// <returns>A <see cref="Task"/> representing the asynchronous disconnect operation.</returns>
                    public async Task DisconnectAsync()
                    {
                        if( cts.IsCancellationRequested ) return; // Return if already cancelled.

                        cts.Cancel(); // Cancel all pending operations.

                        if( ws?.State == WebSocketState.Open ) // Check if WebSocket is open before closing.
                            try
                            {
                                using var closeTimeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));             // Set timeout for close operation.
                                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, closeTimeoutCts.Token); // Gracefully close WebSocket with timeout.
                            }
                            catch( Exception )
                            { /* Ignore exceptions during close operation */
                            }
                            finally
                            {
                                ws.Dispose(); // Dispose of WebSocket resources.
                                ws = null;    // Set WebSocket instance to null.
                            }
                    }
                }
            }

            /// <summary>
            /// Represents a TCP server implementation, extending <see cref="TCP{SRC, DST}"/> to act as a network server.
            /// Manages incoming TCP connections, dispatches channels, and handles server lifecycle.
            /// </summary>
            public class Server : TCP<SRC, DST>{

#region> Server code
#endregion> Network.TCP.Server

                /// <inheritdoc/>
                public Server(string                       name,
                              Func<TCP<SRC, DST>, Channel> new_channel,
                              int                          bufferSize,
                              int                          Backlog,
                              Func<IPEndPoint, Socket>?    socketBuilder,
                              params IPEndPoint[]          ips) : base(name, new_channel, bufferSize)
                {
                    Task.Run(async () => // Start a background task for maintenance loop.
                             {
                                 while( true ) // Infinite loop for continuous maintenance.
                                 {
                                     await maintenance_lock.WaitAsync(); // Wait for maintenance lock (semaphore).

                                     try
                                     {
                                         StartMaintenance();                                           // Set maintenance state to running.
                                         var waitTime = await MaintenanceAsync(DateTime.UtcNow.Ticks); // Perform maintenance and get wait time.
                                         if( RestartMaintenance() ) continue;                          // Check if maintenance restart is needed.

                                         await Task.Delay((int)waitTime); // Wait for calculated maintenance timeout.
                                     }
                                     catch( Exception ex ) // Handle exceptions during maintenance.
                                     {
                                         onFailure(this, ex); // Invoke failure handler for maintenance exceptions.
                                     }
                                     finally
                                     {
                                         maintenance_lock.Release(); // Release maintenance lock (semaphore).
                                     }
                                 }
                             });


                    bind(Backlog, socketBuilder, ips); // Bind server sockets to provided endpoints.
                }

                /// <summary>
                /// List of TCP listeners (sockets) managed by this server.
                /// Each socket is bound to a specific IPEndPoint and listens for incoming connections.
                /// </summary>
                public readonly List<Socket> tcp_listeners = new();

                /// <summary>
                /// Binds the server to listen on the specified IP endpoints.
                /// Creates and configures listening sockets for each endpoint and starts accepting incoming connections asynchronously.
                /// </summary>
                /// <param name="Backlog">The maximum length of the pending connections queue.</param>
                /// <param name="socketBuilder">Optional function to build custom sockets, if null, default sockets will be created.</param>
                /// <param name="ips">Array of IPEndPoints to bind the server to.</param>
                public void bind(int Backlog, Func<IPEndPoint, Socket>? socketBuilder, params IPEndPoint[] ips)
                {
                    var sb = new StringBuilder(50) // StringBuilder for server description.
                             .Append("Server ")
                             .Append(name);

                    socketBuilder ??= ip => new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp); // Default socket builder if none provided.
                    foreach( var ip in ips )                                                                   // Iterate through provided IP endpoints.
                    {
                        sb.Append('\n') // Append endpoint info to server description.
                          .Append("\t\t -> ")
                          .Append(ip);
                        var tcp_listener = socketBuilder(ip);            // Create listening socket using builder.
                        tcp_listeners.Add(tcp_listener);                 // Add socket to listener list.
                        tcp_listener.Bind(ip);                           // Bind socket to the IP endpoint.
                        tcp_listener.Listen(Backlog);                    // Start listening for incoming connections.
                        var on_accept_args = new SocketAsyncEventArgs(); // Create SocketAsyncEventArgs for accept operation.

                        EventHandler<SocketAsyncEventArgs> on_accept_handler = (_, _) => // Event handler for accept completion.
                                                                               {
                                                                                   do
                                                                                   {
                                                                                       if( on_accept_args.SocketError == SocketError.Success )          // Check for successful accept.
                                                                                           allocate().receiver_connected(on_accept_args.AcceptSocket!); // Allocate channel and start receiving.

                                                                                       on_accept_args.AcceptSocket = null; // Clear accepted socket for reuse.
                                                                                   }
                                                                                   while( !tcp_listener.AcceptAsync(on_accept_args) ); // Continue accepting connections asynchronously.
                                                                               };
                        on_accept_args.Completed += on_accept_handler;       // Attach completion handler for accept operation.
                        if( !tcp_listener.AcceptAsync(on_accept_args) )      // Start asynchronous accept operation.
                            on_accept_handler(tcp_listener, on_accept_args); // If synchronous completion, handle immediately.
                    }

                    toString = sb.ToString(); // Build server description string.
                }

#region Maintenance
                // Semaphore to control concurrent access to maintenance operations.
                // Ensures that only one maintenance operation runs at a time, preventing race conditions and data corruption.
                private readonly SemaphoreSlim maintenance_lock = new SemaphoreSlim(1, 1);

                // State variable to track the maintenance process state:
                // <list type="bullet">
                //     <item>0 - idle (no maintenance running)</item>
                //     <item>1 - maintenance is running</item>
                //     <item>2 - maintenance triggered and should re-run if idle</item>
                // </list>
                //
                private volatile int maintenance_state = 0;

                // Sets the maintenance state to "running" (1), indicating that maintenance has started.
                protected void StartMaintenance() { maintenance_state = 1; }

                // Resets the maintenance state to idle (0) and determines if a restart of maintenance is needed.
                // <returns><c>true</c> if maintenance was running and a restart is needed; otherwise, <c>false</c>.</returns>
                protected bool RestartMaintenance() { return 1 < Interlocked.Exchange(ref maintenance_state, 0); }

                // Checks if maintenance is currently running. If idle, sets the state to "re-run" (2).
                // <returns><c>true</c> if maintenance is already running or was just set to re-run; otherwise, <c>false</c>.</returns>
                protected bool MaintenanceRunning() { return 0 < Interlocked.Exchange(ref maintenance_state, 2); }

                // ManualResetEventSlim to signal when maintenance is needed.
                // Used to trigger maintenance operations externally.
                private readonly ManualResetEventSlim maintenanceEvent = new ManualResetEventSlim(false);

                /// <inheritdoc/>
                public override void TriggerMaintenance()
                {
                    if( MaintenanceRunning() ) return; // If maintenance is already running, exit.

                    _ = TriggerMaintenanceAsync(); // Trigger maintenance asynchronously.
                }

                /// <inheritdoc/>
                public override async Task TriggerMaintenanceAsync()
                {
                    await maintenance_lock.WaitAsync(); // Wait for maintenance lock.

                    try
                    {
                        maintenanceEvent.Set(); // Signal maintenance event to trigger maintenance loop.
                    }
                    finally
                    {
                        maintenance_lock.Release(); // Release maintenance lock.
                    }
                }

                // Asynchronously performs maintenance tasks, including channel-specific maintenance, and calculates the next maintenance interval.
                // <param name="time">Current time in ticks.</param>
                // <returns>A <see cref="Task{TResult}"/> representing the asynchronous operation, with the result being the next maintenance interval in milliseconds.</returns>
                protected virtual async Task<int> MaintenanceAsync(long time)
                {
                    while( true ) // Continuous maintenance loop.
                    {
                        var timeout = maintenance_duty_cycle; // Default timeout from duty cycle.

                        for( var channel = channels; channel != null; channel = channel.next ) // Iterate through all channels.
                        {
                            if( channel.is_active ) // Check if channel is active.
                            {
                                if( channel.ready_for_maintenance() ) // Check if channel is ready for maintenance.
                                {
                                    timeout = Math.Min(channel.maintenance(), timeout); // Perform channel maintenance and update timeout.
                                    channel.maintenance_completed();                    // Mark channel maintenance as completed.
                                }
                                else
                                {
                                    timeout = 0; // Set timeout to 0 to request immediate re-run if channel not ready.
                                }
                            }
                        }

                        if( timeout > 0 ) { return (int)timeout; } // If timeout > 0, return calculated timeout.

                        await Task.Delay(100); // Wait briefly before re-checking for maintenance needs.
                    }
                }

                /// <summary>
                /// Minimum duration in milliseconds between maintenance operations.
                /// Controls how often maintenance tasks are performed to balance performance and resource management.
                /// </summary>
                public long maintenance_duty_cycle = 5000;
#endregion

                // String representation of the TCP server, including name and listening endpoints.
                private string toString;

                /// <inheritdoc/>
                public override string ToString() => toString;

                /// <summary>
                /// Shuts down the server gracefully, closing all listening sockets and active channels.
                /// </summary>
                public void shutdown()
                {
                    tcp_listeners.ForEach(socket => socket.Close());                       // Close all listening sockets.
                    for( var channel = channels; channel != null; channel = channel.next ) // Iterate through all channels.
                        if( channel.is_active )                                            // Check if channel is active.
                            channel.Close();                                               // Close active channels.
                }
            }

            /// <summary>
            /// Represents a TCP client implementation, extending <see cref="TCP{SRC, DST}"/> to act as a network client.
            /// Manages outgoing TCP connections and client-specific functionalities.
            /// </summary>
            public class Client : TCP<SRC, DST>{
                // SocketAsyncEventArgs instance used for managing asynchronous connection attempts.
                private readonly SocketAsyncEventArgs onConnecting = new();

                /// <inheritdoc/>
                public Client(string name, Func<TCP<SRC, DST>, Channel> new_channel, int bufferSize) : base(name, new_channel, bufferSize) => onConnecting.Completed += (_, _) => OnConnected(); // Attach completion handler for connect operation.

                // Handles the completion event of an asynchronous connection operation.
                // Called when a connection attempt initiated via <see cref="Socket.ConnectAsync(SocketType, ProtocolType, SocketAsyncEventArgs)"/> completes.
                private void OnConnected()
                {
                    if( onConnecting.SocketError != SocketError.Success || onConnecting.LastOperation != SocketAsyncOperation.Connect ) // Check for successful connection.
                        return;                                                                                                         // Return if connection failed.
                    channels.transmiter_connected(onConnecting.ConnectSocket);                                                          // Initialize transmitter on successful connection.
                    onConnected!(channels.transmitter!);                                                                                // Invoke 'onConnected' callback.
                }

                // Action to be executed upon successful connection. Provides the transmitter for sending data.
                private Action<SRC>? onConnected;

                /// <summary>
                /// Initiates a TCP connection to the specified server endpoint with a default timeout of 5 seconds.
                /// </summary>
                /// <param name="server">The IPEndPoint of the server to connect to.</param>
                /// <param name="onConnected">Action to be executed upon successful connection. Provides the transmitter for sending data.</param>
                /// <param name="onConnectingFailure">Action to be executed if the connection fails. Provides the exception that caused the failure.</param>
                public void Connect(IPEndPoint server, Action<SRC> onConnected, Action<Exception> onConnectingFailure) => Connect(server, onConnected, onConnectingFailure, TimeSpan.FromSeconds(5));

                // Connects to the specified server endpoint with a configurable timeout and callbacks for success and failure.
                // <param name="server">The IPEndPoint of the server to connect to.</param>
                // <param name="onConnected">Action to be executed upon successful connection. Provides the transmitter for sending data.</param>
                // <param name="onConnectingFailure">Action to be executed if the connection fails. Provides the exception that caused the failure.</param>
                // <param name="connectingTimout">Timeout duration for the connection attempt.</param>
                public void Connect(IPEndPoint server, Action<SRC> onConnected, Action<Exception> onConnectingFailure, TimeSpan connectingTimout)
                {
                    toString = new StringBuilder(50) // Build string representation of client.
                               .Append("Client ")
                               .Append(name)
                               .Append(" -> ")
                               .Append(server)
                               .ToString();

                    this.onConnected = onConnected; // Store 'onConnected' callback.

                    onConnecting.RemoteEndPoint = server; // Set remote endpoint for connection.

                    if( Socket.ConnectAsync(SocketType.Stream, ProtocolType.Tcp, onConnecting) ) // Start asynchronous connection attempt.
                        Task.Delay(connectingTimout)                                             // Start timeout timer.
                            .ContinueWith(                                                       // Continuation task after timeout.
                                          _ =>
                                          {
                                              if( channels.ConnectSocket is not { Connected: true } )                                               // Check if connection failed due to timeout.
                                                  onConnectingFailure(new Exception($"Connection to the {server} in {connectingTimout}, timeout")); // Invoke 'onConnectingFailure' callback for timeout.
                                          });
                    else
                        OnConnected(); // If ConnectAsync completes synchronously, handle connection immediately.
                }

                /// <summary>
                /// Disconnects the client by closing the active channel's socket connection.
                /// </summary>
                public void Disconnect()
                {
                    if( channels.ext == null || !channels.ext.Connected ) // Check if socket is valid and connected.
                        return;                                           // Return if not connected or socket is null.
                    channels.Close();                                     // Close the channel to disconnect.
                }

                // String representation of the TCP client, including name and server endpoint.
                private string toString;

                /// <inheritdoc/>
                public override string ToString() => toString;
            }
        }

        /// <summary>
        /// Implements a data wire to connect a BytesSrc to a BytesDst.
        /// This class facilitates direct data flow from a source to a destination, typically within the same process or system.
        /// </summary>
        class Wire{
            // Buffer used for transferring data between the source and destination.
            protected readonly byte[] buffer;

            // Source of bytes for the data wire.
            protected AdHoc.BytesSrc? src;

            // Subscriber action for handling new bytes from the source.
            protected Action<AdHoc.BytesSrc>? subscriber;

            /// <summary>
            /// Initializes a new instance of the <see cref="Wire"/> class.
            /// </summary>
            /// <param name="src">The source of bytes (BytesSrc).</param>
            /// <param name="dst">The destination for bytes (BytesDst).</param>
            /// <param name="buffer_size">The size of the buffer to use for data transfer.</param>
            public Wire(AdHoc.BytesSrc src, AdHoc.BytesDst dst, int buffer_size)
            {
                buffer = new byte[buffer_size]; // Allocate buffer for data transfer.
                connect(src, dst);              // Connect the source and destination.
            }

            /// <summary>
            /// Connects a new source and destination for the data wire.
            /// Unsubscribes from any previous source and subscribes to the new source to start data flow.
            /// </summary>
            /// <param name="src">The new source of bytes (BytesSrc).</param>
            /// <param name="dst">The new destination for bytes (BytesDst).</param>
            public void connect(AdHoc.BytesSrc src, AdHoc.BytesDst dst)
            {
                subscriber = (this.src = src).subscribeOnNewBytesToTransmitArrive(     // Subscribe to new source and get subscriber action.
                                                                                  _ => // Subscriber action implementation.
                                                                                  {
                                                                                      for( int len; 0 < (len = src.Read(buffer, 0, buffer.Length)); ) // Read data from source.
                                                                                          dst.Write(buffer, 0, len);                                  // Write data to destination.
                                                                                  });
            }
        }

        /// <summary>
        /// Placeholder class for UDP-based network communication.
        /// Currently suggests using TCP implementation over UDP via WireGuard for robust and secure UDP-based networking.
        /// </summary>
        class UDP{
            // use TCP implementation over UDP Wireguard https://www.wireguard.com/
        }
    }
}