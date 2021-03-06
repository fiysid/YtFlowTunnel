﻿using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using YtFlow.Tunnel.Adapter.Local;
using YtFlow.Tunnel.Adapter.Remote;

namespace YtFlow.Tunnel.Adapter.Relay
{
    internal class DirectRelay : ILocalAdapter, IRemoteAdapter
    {
        protected ILocalAdapter localAdapter;
        protected IRemoteAdapter remoteAdapter;

        public DirectRelay (IRemoteAdapter remoteAdapter)
        {
            this.remoteAdapter = remoteAdapter;
        }

        #region LocalAdapter
        public virtual Destination.Destination Destination
        {
            get => localAdapter.Destination;
            set => localAdapter.Destination = value;
        }

        public ValueTask WritePacketToLocal (Span<byte> data, CancellationToken cancellationToken = default)
        {
            return localAdapter.WritePacketToLocal(data, cancellationToken);
        }
        #endregion

        #region RemoteAdapter
        public bool RemoteDisconnected { get => remoteAdapter.RemoteDisconnected; set => remoteAdapter.RemoteDisconnected = value; }

        public virtual ValueTask Init (ChannelReader<byte[]> outboundChan, ILocalAdapter localAdapter, CancellationToken cancellationToken = default)
        {
            this.localAdapter = localAdapter;
            return remoteAdapter.Init(outboundChan, this, cancellationToken);
        }

        public ValueTask<int> StartRecv (ArraySegment<byte> outBuf, CancellationToken cancellationToken = default)
        {
            return remoteAdapter.StartRecv(outBuf, cancellationToken);
        }

        public Task StartSend (ChannelReader<byte[]> outboundChan, CancellationToken cancellationToken = default)
        {
            return remoteAdapter.StartSend(outboundChan, cancellationToken);
        }

        public Task StartRecvPacket (ILocalAdapter localAdapter, CancellationToken cancellationToken = default)
        {
            return remoteAdapter.StartRecvPacket(localAdapter, cancellationToken);
        }

        public void SendPacketToRemote (Memory<byte> data, Destination.Destination destination)
        {
            remoteAdapter.SendPacketToRemote(data, destination);
        }

        public ValueTask<int> GetRecvBufSizeHint (int preferredSize, CancellationToken cancellationToken = default) => remoteAdapter.GetRecvBufSizeHint(preferredSize, cancellationToken);

        public void CheckShutdown ()
        {
            remoteAdapter.CheckShutdown();
        }
        #endregion
    }
}
