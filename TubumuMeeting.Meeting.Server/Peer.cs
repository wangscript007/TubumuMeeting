﻿using System;
using System.Collections.Generic;
using System.Linq;
using Tubumu.Core.Extensions;
using TubumuMeeting.Mediasoup;

namespace TubumuMeeting.Meeting.Server
{
    public partial class Peer : IEquatable<Peer>
    {
        public string PeerId { get; }

        public string DisplayName { get; set; }

        public bool Equals(Peer other)
        {
            return PeerId == other.PeerId;
        }

        public override int GetHashCode()
        {
            return PeerId.GetHashCode();
        }
    }

    public partial class Peer
    {
        public bool Closed { get; private set; }

        public RtpCapabilities? RtpCapabilities { get; set; }

        public SctpCapabilities? SctpCapabilities { get; set; }

        public Group? Group { get; set; }

        public Dictionary<string, Room> Rooms { get; } = new Dictionary<string, Room>();

        public Dictionary<string, Transport> Transports { get; } = new Dictionary<string, Transport>();

        public Dictionary<string, Producer> Producers { get; } = new Dictionary<string, Producer>();

        public Dictionary<string, Consumer> Consumers { get; } = new Dictionary<string, Consumer>();

        public Dictionary<string, DataProducer> DataProducers { get; } = new Dictionary<string, DataProducer>();

        public Dictionary<string, DataConsumer> DataConsumers { get; } = new Dictionary<string, DataConsumer>();

        public string[]? Sources { get; set; }

        public Dictionary<string, object>? AppData { get; set; }

        public Peer(string peerId, string displayName)
        {
            PeerId = peerId;
            DisplayName = displayName.IsNullOrWhiteSpace() ? "Guest" : displayName;
            Closed = false;
        }

        public void Close()
        {
            if (Closed)
            {
                return;
            }

            Closed = true;
            RtpCapabilities = null;
            SctpCapabilities = null;

            // Iterate and close all mediasoup Transport associated to this Peer, so all
            // its Producers and Consumers will also be closed.
            Transports.Values.ForEach(m => m.Close());
        }

        public Transport GetConsumerTransport()
        {
            return Transports.Values.Where(m => m.AppData != null && m.AppData.TryGetValue("Consuming", out var value) && (bool)value).FirstOrDefault();
        }
    }
}
