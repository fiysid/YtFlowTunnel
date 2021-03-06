﻿using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Windows.Networking;
using Windows.Networking.Connectivity;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;
using YtFlow.Tunnel.Adapter.Destination;
using YtFlow.Tunnel.Adapter.Local;

namespace YtFlow.Tunnel.Adapter.Remote
{
    internal sealed class HttpAdapter : IRemoteAdapter
    {
        private static readonly byte[] HEADER1 = Encoding.UTF8.GetBytes("CONNECT ");
        private static readonly byte[] HEADER2 = Encoding.UTF8.GetBytes(" HTTP/1.1\r\n\r\n");
        private static readonly NotSupportedException UdpNotSupportedException = new NotSupportedException("UDP destination is not supported");
        private static readonly ArrayPool<byte> sendArrayPool = ArrayPool<byte>.Create();
        private readonly string server;
        private readonly string port;
        private const int HEAD_BUFFER_LEN = 100;
        private readonly StreamSocket socket = new StreamSocket();
        private IInputStream inputStream;
        private IOutputStream outputStream;
        public bool RemoteDisconnected { get; set; } = false;

        public HttpAdapter (string server, string port)
        {
            this.server = server;
            this.port = port;
        }

        private static int CountDigit (ushort port)
        {
            int ret = 0;
            while (port != 0)
            {
                ret++;
                port /= 10;
            }
            return ret;
        }

        public async ValueTask Init (ChannelReader<byte[]> outboundChan, ILocalAdapter localAdapter, CancellationToken cancellationToken = default)
        {
            if (localAdapter.Destination.TransportProtocol == TransportProtocol.Udp)
            {
                throw UdpNotSupportedException;
            }
            var dev = NetworkInformation.GetInternetConnectionProfile().NetworkAdapter;
            var connectTask = socket.ConnectAsync(new HostName(server), port.ToString(), SocketProtectionLevel.PlainSocket, dev).AsTask(cancellationToken).ConfigureAwait(false);

            var destination = localAdapter.Destination;
            var dstPort = destination.Port;
            var dstPortStrSize = CountDigit(dstPort);
            var dstHostStrSize = destination.Host.Size;
            int headerLen = HEADER1.Length + dstHostStrSize + 1 + dstPortStrSize + HEADER2.Length;
            var firstSeg = sendArrayPool.Rent(headerLen);
            try
            {
                HEADER1.CopyTo(firstSeg, 0);
                destination.Host.CopyTo(firstSeg.AsSpan(HEADER1.Length));
                firstSeg[HEADER1.Length + dstHostStrSize] = (byte)':';
                while (dstPortStrSize-- > 0)
                {
                    firstSeg[HEADER1.Length + dstHostStrSize + 1 + dstPortStrSize] = (byte)(dstPort % 10 + '0');
                    dstPort /= 10;
                }
                HEADER2.CopyTo(firstSeg, headerLen - HEADER2.Length);

                // Connect and perform handshake
                await connectTask;
                inputStream = socket.InputStream;
                outputStream = socket.OutputStream;
                await outputStream.WriteAsync(firstSeg.AsBuffer(0, headerLen)).AsTask(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                sendArrayPool.Return(firstSeg);
            }
            var responseBuf = sendArrayPool.Rent(HEAD_BUFFER_LEN);
            try
            {
                var resBuf = await inputStream.ReadAsync(responseBuf.AsBuffer(), HEAD_BUFFER_LEN, InputStreamOptions.Partial).AsTask(cancellationToken).ConfigureAwait(false);
                uint responseLen = resBuf.Length;
                if (responseLen < 14)
                {
                    throw new InvalidOperationException("Remote response too short");
                }
                if ((responseBuf[9] == (byte)'2') && (responseBuf[10] == (byte)'0') && (responseBuf[11] == (byte)'0'))
                {
                    // 200 objk
                }
                else
                {
                    var code = 100 * (responseBuf[9] - '0') + 10 * (responseBuf[10] - '0') + responseBuf[11] - '0';
                    throw new InvalidOperationException("Remote status code: " + code.ToString());
                }
                bool foundHeader = false;
                for (int headerStart = 12; headerStart < responseLen - 3; headerStart++)
                {
                    if (responseBuf[headerStart] == '\r')
                    {
                        if (responseBuf[headerStart + 1] == '\n')
                        {
                            if (responseBuf[headerStart + 2] == '\r')
                            {
                                if (responseBuf[headerStart + 3] == '\n')
                                {
                                    foundHeader = true;
                                    break;
                                }
                            }
                        }
                    }
                }
                if (!foundHeader)
                {
                    throw new InvalidOperationException("Unrecognized remote header: " + Encoding.UTF8.GetString(responseBuf, 0, (int)responseLen));
                }
            }
            finally
            {
                sendArrayPool.Return(responseBuf);
            }
            // Initial data?
        }

        public async Task StartSend (ChannelReader<byte[]> outboundChan, CancellationToken cancellationToken = default)
        {
            while (await outboundChan.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var packetsToSend = new List<byte[]>();
                while (outboundChan.TryRead(out var segment))
                {
                    packetsToSend.Add(segment);
                }
                var pendingTasks = new Task[packetsToSend.Count];
                for (var index = 0; index < packetsToSend.Count; ++index)
                {
                    var segment = packetsToSend[index];
                    pendingTasks[index] = outputStream.WriteAsync(segment.AsBuffer()).AsTask(cancellationToken);
                }
                await Task.WhenAll(pendingTasks).ConfigureAwait(false);
            }
            await outputStream.FlushAsync().AsTask(cancellationToken).ConfigureAwait(false);
        }

        public ValueTask<int> GetRecvBufSizeHint (int preferredSize, CancellationToken cancellationToken = default) => new ValueTask<int>(preferredSize);

        public async ValueTask<int> StartRecv (ArraySegment<byte> outBuf, CancellationToken cancellationToken = default)
        {
            var recvBuf = await inputStream.ReadAsync(outBuf.Array.AsBuffer(outBuf.Offset, outBuf.Count), (uint)outBuf.Count, InputStreamOptions.Partial).AsTask(cancellationToken).ConfigureAwait(false);
            if (recvBuf.Length == 0)
            {
                return 0;
            }
            return (int)recvBuf.Length;
        }

        public void CheckShutdown ()
        {
            try
            {
                inputStream?.Dispose();
            }
            catch (ObjectDisposedException) { }
            try
            {
                outputStream?.Dispose();
            }
            catch (ObjectDisposedException) { }
            try
            {
                socket.Dispose();
            }
            catch (ObjectDisposedException) { }
        }

        public Task StartRecvPacket (ILocalAdapter localAdapter, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public void SendPacketToRemote (Memory<byte> data, Destination.Destination destination)
        {
        }
    }
}
