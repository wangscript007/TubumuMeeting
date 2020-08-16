﻿using System.Collections.Generic;

namespace TubumuMeeting.Mediasoup
{
    public class JoinRoomResult
    {
        public Peer SelfPeer { get; set; }

        public string[] RoomSources { get; set; }

        public Dictionary<string, object> RoomAppData { get; set; }

        public PeerWithRoomAppData[] PeersInRoom { get; set; }
    }
}
