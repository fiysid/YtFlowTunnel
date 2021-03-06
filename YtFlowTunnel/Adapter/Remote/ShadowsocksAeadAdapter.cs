﻿using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;
using YtCrypto;
using YtFlow.Tunnel.Adapter.Destination;
using YtFlow.Tunnel.Adapter.Factory;
using YtFlow.Tunnel.Adapter.Local;

namespace YtFlow.Tunnel.Adapter.Remote
{
    internal class ShadowsocksAeadAdapter : ShadowsocksAdapter
    {
        private const int TAG_SIZE = 16;
        private const int SIZE_MASK = 0x3fff;
        private const int READ_SIZE_SIZE = TAG_SIZE + 2;
        protected override int sendBufferLen => SIZE_MASK;
        private readonly byte[] sizeBuf = new byte[READ_SIZE_SIZE];
        private int sizeToRead = 0;

        public ShadowsocksAeadAdapter (string server, string serviceName, ICryptor cryptor) : base(server, serviceName, cryptor)
        {

        }

        public static unsafe int RealEncrypt (ReadOnlySpan<byte> data, Span<byte> tag, Span<byte> outData, ICryptor cryptor)
        {
            fixed (byte* dataPtr = &data.GetPinnableReference(), tagPtr = &tag.GetPinnableReference(), outDataPtr = &outData.GetPinnableReference())
            {
                // Reserve for iv
                var outLen = cryptor.EncryptAuth((ulong)dataPtr, (uint)data.Length, (ulong)tagPtr, (uint)tag.Length, (ulong)outDataPtr, (uint)outData.Length);
                if (outLen < 0)
                {
                    throw new AeadOperationException(outLen);
                }
                return outLen;
            }
        }

        /// <summary>
        /// Encrypt Shadowsocks request.
        /// </summary>
        /// <param name="data">Input data.</param>
        /// <param name="outData">Output data. Must have enough capacity to hold encrypted data, tags and additional data.</param>
        /// <param name="cryptor">The cryptor to used. A null value indicates that the connection-wide cryptor should be used.</param>
        /// <returns>The length of <paramref name="outData"/> used.</returns>
        public override uint Encrypt (ReadOnlySpan<byte> data, Span<byte> outData, ICryptor cryptor = null)
        {
            if (cryptor == null)
            {
                cryptor = this.cryptor;
            }
            if (data.Length == 0)
            {
                // First chunk, fill IV/salt only
                return (uint)RealEncrypt(Array.Empty<byte>(), Array.Empty<byte>(), outData, cryptor);
            }
            Span<byte> lenData = stackalloc byte[2];
            lenData[0] = (byte)(data.Length >> 8);
            lenData[1] = (byte)(data.Length & 0xFF);
            var encryptedLenSize = RealEncrypt(lenData, outData.Slice(2, TAG_SIZE), outData.Slice(0, 2), cryptor) + TAG_SIZE;
            var dataLenSize = RealEncrypt(data, outData.Slice((int)encryptedLenSize + data.Length, TAG_SIZE), outData.Slice((int)encryptedLenSize, data.Length), cryptor) + TAG_SIZE;
            return (uint)(encryptedLenSize + dataLenSize);
        }

        public override uint EncryptAll (ReadOnlySpan<byte> data, Span<byte> outData, ICryptor cryptor = null)
        {
            var len = data.Length;
            return (uint)RealEncrypt(data, outData.Slice(len, TAG_SIZE), outData.Slice(0, len), cryptor) + TAG_SIZE;
        }

        // For ShadowsocksAdapter to receive IV
        public override uint Decrypt (ReadOnlySpan<byte> data, Span<byte> outData, ICryptor cryptor = null)
        {
            return (uint)Decrypt(data, Array.Empty<byte>(), Array.Empty<byte>(), cryptor);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static unsafe int Decrypt (ReadOnlySpan<byte> data, ReadOnlySpan<byte> tag, Span<byte> outData, ICryptor cryptor)
        {
            fixed (byte* dataPtr = &data.GetPinnableReference(), tagPtr = &tag.GetPinnableReference(), outDataPtr = &outData.GetPinnableReference())
            {
                var outLen = cryptor.DecryptAuth((ulong)dataPtr, (uint)data.Length, (ulong)tagPtr, (uint)tag.Length, (ulong)outDataPtr, (uint)outData.Length);
                if (outLen < 0)
                {
                    throw new AeadOperationException(outLen);
                }
                return outLen;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int DecryptSize (ReadOnlySpan<byte> data, ReadOnlySpan<byte> tag, ICryptor cryptor)
        {
            Span<byte> decSizeBuf = stackalloc byte[2];
            var decResult = Decrypt(data, tag, decSizeBuf, cryptor);
            if (decResult != 2)
            {
                throw new AeadOperationException(decResult);
            }
            return decSizeBuf[0] << 8 | decSizeBuf[1];
        }

        private async ValueTask<bool> ReadExact (byte[] buffer, int offset, int desiredLength, CancellationToken cancellationToken)
        {
            int targetOffset = offset + desiredLength;
            if (targetOffset > buffer.Length)
            {
                throw new ArgumentOutOfRangeException($"{nameof(offset)} + {nameof(desiredLength)} must not exceed buffer length.", nameof(desiredLength));
            }
            do
            {
                var readLen = targetOffset - offset;
                var chunk = await tcpInputStream.ReadAsync(buffer.AsBuffer(offset, readLen), (uint)readLen, InputStreamOptions.Partial)
                    .AsTask(cancellationToken).ConfigureAwait(false);
                var chunkLen = (int)chunk.Length;
                if (chunkLen == 0)
                {
                    return false;
                }
                offset += chunkLen;
            } while (offset < targetOffset);
            return true;
        }

        public override async ValueTask<int> GetRecvBufSizeHint (int preferredSize, CancellationToken cancellationToken = default)
        {
            if (!receiveIvTask.IsCompleted)
            {
                await receiveIvTask.ConfigureAwait(false);
            }
            if (!await ReadExact(sizeBuf, 0, READ_SIZE_SIZE, cancellationToken).ConfigureAwait(false))
            {
                return 0;
            }
            sizeToRead = DecryptSize(sizeBuf.AsSpan(0, 2), sizeBuf.AsSpan(2, TAG_SIZE), cryptor);
            return sizeToRead + TAG_SIZE;
        }

        public override async ValueTask<int> StartRecv (ArraySegment<byte> outBuf, CancellationToken cancellationToken = default)
        {
            if (!await ReadExact(outBuf.Array, outBuf.Offset, sizeToRead + TAG_SIZE, cancellationToken).ConfigureAwait(false))
            {
                return 0;
            }
            return Decrypt(outBuf.AsSpan(0, sizeToRead), outBuf.AsSpan(sizeToRead, TAG_SIZE), outBuf.AsSpan(0, sizeToRead), cryptor);
        }

        public unsafe override Task StartRecvPacket (ILocalAdapter localAdapter, CancellationToken cancellationToken = default)
        {
            var outDataBuffer = new byte[sendBufferLen + 66];
            var tcs = new TaskCompletionSource<object>();
            void packetHandler (DatagramSocket sender, DatagramSocketMessageReceivedEventArgs e)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                try
                {
                    var cryptor = ShadowsocksFactory.GlobalCryptorFactory.CreateCryptor();
                    var buffer = e.GetDataReader().DetachBuffer();
                    var ptr = ((IBufferByteAccess)buffer).GetBuffer();
                    var ivLen = (int)cryptor.IvLen;
                    if (buffer.Length < ivLen + TAG_SIZE + 7)
                    {
                        return;
                    }

                    int decDataLen;
                    try
                    {
                        decDataLen = Decrypt(
                            new ReadOnlySpan<byte>(ptr.ToPointer(), (int)(buffer.Length - TAG_SIZE)),
                            new ReadOnlySpan<byte>((void*)(ptr.ToInt64() + buffer.Length - TAG_SIZE), TAG_SIZE),
                            outDataBuffer, cryptor);
                    }
                    catch (AeadOperationException ex)
                    {
                        if (DebugLogger.LogNeeded())
                        {
                            DebugLogger.Log($"Error decrypting a UDP packet from {localAdapter.Destination}: {ex}");
                        }
                        throw;
                    }
                    // TODO: support IPv6/domain name address type
                    if (decDataLen < 7 || outDataBuffer[0] != 1)
                    {
                        return;
                    }

                    var headerLen = Destination.Destination.TryParseSocks5StyleAddress(outDataBuffer.AsSpan(0, decDataLen), out _, TransportProtocol.Udp);
                    if (headerLen <= 0)
                    {
                        return;
                    }
                    localAdapter.WritePacketToLocal(outDataBuffer.AsSpan(headerLen, decDataLen - headerLen), cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            }
            udpReceivedHandler = packetHandler;
            cancellationToken.Register(() =>
            {
                tcs.TrySetCanceled();
                var socket = udpClient;
                if (socket != null)
                {
                    socket.MessageReceived -= packetHandler;
                }
            });
            return tcs.Task;
        }

    }
}
