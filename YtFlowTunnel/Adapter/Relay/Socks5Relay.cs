﻿using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using YtFlow.Tunnel.Adapter.Destination;
using YtFlow.Tunnel.Adapter.Local;
using YtFlow.Tunnel.Adapter.Remote;
using YtFlow.Tunnel.DNS;

namespace YtFlow.Tunnel.Adapter.Relay
{
    internal class Socks5Relay : DirectRelay
    {
        private static readonly byte[] ServerChoicePayload = new byte[] { 5, 0 };
        private static readonly byte[] DummyResponsePayload = new byte[] { 5, 0, 0, 1, 0, 0, 0, 0, 0, 0 };
        private static readonly byte[] UdpResponseHeaderPrefix = new byte[] { 0, 0, 0 };
        private static readonly ArgumentException BadGreetingException = new ArgumentException("Bad socks5 greeting message");
        private static readonly ArgumentException RequestTooShortException = new ArgumentException("Sock5 request is too short");
        private static readonly ArgumentException BadRequestException = new ArgumentException("Bad socks5 request message");
        private static readonly NotImplementedException UnknownTypeException = new NotImplementedException("Unknown socks5 request type");

        private byte[] preparedUdpResponseHeader;

        public Socks5Relay (IRemoteAdapter remoteAdapter) : base(remoteAdapter)
        {
        }

        public static Destination.Destination ParseDestinationFromRequest (ReadOnlySpan<byte> payload)
        {
            if (payload.Length < 8)
            {
                throw RequestTooShortException;
            }
            if (payload[0] != 5)
            {
                throw BadRequestException;
            }
            TransportProtocol protocol;
            switch (payload[1])
            {
                case 1:
                    protocol = TransportProtocol.Tcp;
                    break;
                case 3:
                    protocol = TransportProtocol.Udp;
                    break;
                default:
                    throw UnknownTypeException;
            }
            if (Adapter.Destination.Destination.TryParseSocks5StyleAddress(payload.Slice(3), out Destination.Destination destination, protocol) == 0)
            {
                throw RequestTooShortException;
            }

            // Some SOCKS5 clients (e.g. curl) can resolve IP addresses locally.
            // In this case, we got a fake IP address and need to
            // convert it back to the corresponding domain name.
            switch (destination.Host)
            {
                case Ipv4Host ipv4:
                    destination = new Destination.Destination(DnsProxyServer.TryLookup(ipv4.Data), destination.Port, protocol);
                    break;
            }
            return destination;
        }

        public static int ParseDestinationFromUdpPayload (ReadOnlySpan<byte> payload, out Destination.Destination destination)
        {
            if (payload.Length < 9)
            {
                destination = default;
                return 0;
            }
            if (payload[2] != 0)
            {
                // FRAG is not supported
            }
            var len = 3;
            len += Adapter.Destination.Destination.TryParseSocks5StyleAddress(payload.Slice(3), out destination, TransportProtocol.Udp);
            if (len == 0)
            {
                destination = default;
                return 0;
            }
            return len;
        }

        public int FillDestinationIntoSocks5UdpPayload (Span<byte> data)
        {
            if (preparedUdpResponseHeader != null)
            {
                preparedUdpResponseHeader.CopyTo(data);
                return preparedUdpResponseHeader.Length;
            }

            int len = 0;
            UdpResponseHeaderPrefix.CopyTo(data);
            len += UdpResponseHeaderPrefix.Length;
            // TODO: Fill destination with domain name as host?
            len += Destination.FillSocks5StyleAddress(data.Slice(len));
            preparedUdpResponseHeader = new byte[len];
            data.Slice(0, len).CopyTo(preparedUdpResponseHeader);
            return len;
        }

        public async override ValueTask Init (ChannelReader<byte[]> outboundChan, ILocalAdapter localAdapter, CancellationToken cancellationToken = default)
        {
            this.localAdapter = localAdapter;

            var greeting = await outboundChan.ReadAsync().ConfigureAwait(false);
            if (greeting.Length < 3 || greeting[0] != 5 || greeting[2] != 0)
            {
                throw BadGreetingException;
            }
            await WritePacketToLocal(ServerChoicePayload);

            var request = await outboundChan.ReadAsync().ConfigureAwait(false);
            Destination = ParseDestinationFromRequest(request);
            if (Destination.TransportProtocol == TransportProtocol.Udp)
            {
                throw UnknownTypeException;
            }
            await WritePacketToLocal(DummyResponsePayload);

            await base.Init(outboundChan, localAdapter, cancellationToken).ConfigureAwait(false);
        }
    }
}
