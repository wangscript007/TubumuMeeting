﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Tubumu.Core.Extensions;
using TubumuMeeting.Mediasoup;
using TubumuMeeting.Mediasoup.Extensions;

namespace TubumuMeeting.Meeting.Server
{
    [Authorize]
    public partial class MeetingHub : Hub<IPeer>
    {
        private readonly ILogger<MeetingHub> _logger;
        private readonly IHubContext<MeetingHub, IPeer> _hubContext;
        private readonly Scheduler _scheduler;

        private string UserId => Context.User.Identity.Name;

        public MeetingHub(ILogger<MeetingHub> logger, IHubContext<MeetingHub, IPeer> hubContext, Scheduler scheduler)
        {
            _logger = logger;
            _hubContext = hubContext;
            _scheduler = scheduler;
        }

        public override Task OnConnectedAsync()
        {
            Leave();

            return base.OnConnectedAsync();
        }

        public override Task OnDisconnectedAsync(Exception exception)
        {
            Leave();

            return base.OnDisconnectedAsync(exception);
        }

        #region Private

        private void Leave()
        {
            var leaveResult = _scheduler.Leave(UserId);
            if (leaveResult != null)
            {
                foreach (var otherPeer in leaveResult.OtherPeers)
                {
                    // Message: peerLeave
                    SendMessage(otherPeer.PeerId, "peerLeave", new { PeerId = leaveResult.SelfPeer.PeerId });
                }
            }
        }

        #endregion
    }

    public partial class MeetingHub
    {
        public MeetingMessage GetRouterRtpCapabilities()
        {
            var rtpCapabilities = _scheduler.DefaultRtpCapabilities;
            return new MeetingMessage { Code = 200, Message = "GetRouterRtpCapabilities 成功", Data = rtpCapabilities };
        }

        public async Task<MeetingMessage> Join(JoinRequest joinRequest)
        {
            if (!await _scheduler.Join(UserId, joinRequest))
            {
                return new MeetingMessage { Code = 400, Message = "Join 失败" };
            }

            return new MeetingMessage { Code = 200, Message = "Join 成功" };
        }

        public async Task<MeetingMessage> CreateWebRtcTransport(CreateWebRtcTransportRequest createWebRtcTransportRequest)
        {
            var transport = await _scheduler.CreateWebRtcTransportAsync(UserId, createWebRtcTransportRequest);
            transport.On("sctpstatechange", sctpState =>
            {
                _logger.LogDebug($"WebRtcTransport \"sctpstatechange\" event [sctpState:{sctpState}]");
            });

            transport.On("dtlsstatechange", value =>
            {
                var dtlsState = (DtlsState)value!;
                if (dtlsState == DtlsState.Failed || dtlsState == DtlsState.Closed)
                {
                    _logger.LogWarning($"WebRtcTransport dtlsstatechange event [dtlsState:{value}]");
                }
            });

            // NOTE: For testing.
            //await transport.EnableTraceEventAsync(new[] { TransportTraceEventType.Probation, TransportTraceEventType.BWE });
            //await transport.EnableTraceEventAsync(new[] { TransportTraceEventType.BWE });

            var peerId = UserId;
            transport.On("trace", trace =>
            {
                var traceData = (TransportTraceEventData)trace!;
                _logger.LogDebug($"transport \"trace\" event [transportId:{transport.TransportId}, trace:{traceData.Type.GetEnumStringValue()}]");

                if (traceData.Type == TransportTraceEventType.BWE && traceData.Direction == TraceEventDirection.Out)
                {
                    // Message: downlinkBwe
                    SendMessage(peerId, "downlinkBwe", new
                    {
                        DesiredBitrate = traceData.Info["desiredBitrate"],
                        EffectiveDesiredBitrate = traceData.Info["effectiveDesiredBitrate"],
                        AvailableBitrate = traceData.Info["availableBitrate"]
                    });
                }
            });

            return new MeetingMessage
            {
                Code = 200,
                Message = $"CreateWebRtcTransport 成功({(createWebRtcTransportRequest.Producing ? "Producing" : "Consuming")})",
                Data = new CreateWebRtcTransportResult
                {
                    TransportId = transport.TransportId,
                    IceParameters = transport.IceParameters,
                    IceCandidates = transport.IceCandidates,
                    DtlsParameters = transport.DtlsParameters,
                    SctpParameters = transport.SctpParameters,
                }
            };
        }

        public async Task<MeetingMessage> ConnectWebRtcTransport(ConnectWebRtcTransportRequest connectWebRtcTransportRequest)
        {
            try
            {
                if (!await _scheduler.ConnectWebRtcTransportAsync(UserId, connectWebRtcTransportRequest))
                {
                    return new MeetingMessage { Code = 400, Message = $"ConnectWebRtcTransport 失败: TransportId: {connectWebRtcTransportRequest.TransportId}" };
                }
            }
            catch (Exception ex)
            {
                return new MeetingMessage { Code = 400, Message = $"ConnectWebRtcTransport 失败: TransportId: {connectWebRtcTransportRequest.TransportId}, {ex.Message}" };
            }

            return new MeetingMessage { Code = 200, Message = "ConnectWebRtcTransport 成功" };
        }

        public async Task<MeetingMessage> JoinRoom(JoinRoomRequest joinRoomRequest)
        {
            var joinRoomResult = await _scheduler.JoinRoomAsync(UserId, joinRoomRequest);

            var selfPeerInfo = new PeerInfo
            {
                RoomId = joinRoomRequest.RoomId,
                PeerId = joinRoomResult.SelfPeer.PeerId,
                DisplayName = joinRoomResult.SelfPeer.DisplayName,
                Sources = joinRoomResult.SelfPeer.Sources,
                AppData = joinRoomResult.SelfPeer.AppData,
            };

            var peerInfos = new List<PeerInfo>();
            foreach (var peer in joinRoomResult.PeersInRoom)
            {
                var peerInfo = new PeerInfo
                {
                    RoomId = joinRoomRequest.RoomId,
                    PeerId = peer.PeerId,
                    DisplayName = peer.DisplayName,
                    Sources = peer.Sources,
                    AppData = peer.AppData,
                };
                peerInfos.Add(peerInfo);

                // 将自身的信息告知给房间内的其他人
                if (peer.PeerId != joinRoomResult.SelfPeer.PeerId)
                {
                    // Message: peerJoinRoom
                    SendMessage(peer.PeerId, "peerJoinRoom", selfPeerInfo);
                }
            }

            var data = new
            {
                RoomId = joinRoomRequest.RoomId,
                Peers = peerInfos,
            };
            return new MeetingMessage { Code = 200, Message = "JoinRoom 成功", Data = data };
        }

        public MeetingMessage LeaveRoom(LeaveRoomRequest leaveRoomRequest)
        {
            var leaveRoomResult = _scheduler.LeaveRoom(UserId, leaveRoomRequest.RoomId);

            foreach (var otherPeer in leaveRoomResult.OtherPeers)
            {
                // Message: peerLeaveRoom
                SendMessage(otherPeer.PeerId, "peerLeaveRoom", new
                {
                    RoomId = leaveRoomRequest.RoomId,
                    PeerId = UserId
                });
            }

            return new MeetingMessage { Code = 200, Message = "LeaveRoom 成功" };
        }

        public MeetingMessage Pull(PullRequest consumeRequest)
        {
            var consumeResult = _scheduler.Pull(UserId, consumeRequest);
            var consumerPeer = consumeResult.ConsumePeer;
            var producerPeer = consumeResult.ProducePeer;
            var roomId = consumeRequest.RoomId;

            foreach (var producer in consumeResult.ExistsProducers)
            {
                // 本 Peer 消费其他 Peer
                CreateConsumer(consumerPeer, producerPeer, producer, roomId).ContinueWithOnFaultedHandleLog(_logger);
            }

            if (!consumeResult.ProduceSources.IsNullOrEmpty())
            {
                // Message: produceSources
                SendMessage(consumeResult.ProducePeer.PeerId, "produceSources", new
                {
                    RoomId = consumeResult.RoomId,
                    ProduceSources = consumeResult.ProduceSources
                });
            }

            return new MeetingMessage { Code = 200, Message = "Pull 成功" };
        }

        public async Task<MeetingMessage> Produce(ProduceRequest produceRequest)
        {
            var peerId = UserId;
            ProduceResult produceResult;
            try
            {
                produceResult = await _scheduler.ProduceAsync(peerId, produceRequest);
            }
            catch (Exception ex)
            {
                return new MeetingMessage
                {
                    Code = 400,
                    Message = $"Produce 失败:{ex.Message}",
                };
            }

            var producerPeer = produceResult.ProducerPeer;
            var producer = produceResult.Producer;

            foreach (var item in produceResult.PullPaddingConsumerPeerWithRoomIds)
            {
                var consumerPeer = item.ConsumerPeer;
                var roomId = item.RoomId;

                // NOTE: For Testing
                //if (consumerPeer.PeerId == "1") continue;
                // 其他 Peer 消费本 Peer
                CreateConsumer(consumerPeer, producerPeer, producer, roomId).ContinueWithOnFaultedHandleLog(_logger);
            }

            // NOTE: For Testing
            //CreateConsumer(producerPeer, producerPeer, producer, "1").ContinueWithOnFaultedHandleLog(_logger);

            // Set Producer events.
            producer.On("score", score =>
            {
                var data = (ProducerScore[])score!;
                // Message: producerScore
                SendMessage(peerId, "producerScore", new { ProducerId = producer.ProducerId, Score = data });

            });
            producer.On("videoorientationchange", videoOrientation =>
            {
                var data = (ProducerVideoOrientation)videoOrientation!;
                _logger.LogDebug($"producer.On() | producer \"videoorientationchange\" event [producerId:\"{producer.ProducerId}\", videoOrientation:\"{videoOrientation}\"]");
            });

            return new MeetingMessage
            {
                Code = 200,
                Message = "Produce 成功",
                Data = new { Id = producer.ProducerId }
            };
        }

        public async Task<MeetingMessage> CloseProducer(string producerId)
        {
            if (!await _scheduler.CloseProducerAsync(UserId, producerId))
            {
                return new MeetingMessage { Code = 400, Message = "CloseProducer 失败" };
            }

            return new MeetingMessage { Code = 200, Message = "CloseProducer 成功" };
        }

        public async Task<MeetingMessage> PauseProducer(string producerId)
        {
            if (!await _scheduler.PauseProducerAsync(UserId, producerId))
            {
                return new MeetingMessage { Code = 400, Message = "CloseProducer 失败" };
            }

            return new MeetingMessage { Code = 200, Message = "PauseProducer 成功" };
        }

        public async Task<MeetingMessage> ResumeProducer(string producerId)
        {
            if (!await _scheduler.ResumeProducerAsync(UserId, producerId))
            {
                return new MeetingMessage { Code = 400, Message = "CloseProducer 失败" };
            }

            return new MeetingMessage { Code = 200, Message = "ResumeProducer 成功" };
        }

        public async Task<MeetingMessage> CloseConsumer(string consumerId)
        {
            if (!await _scheduler.CloseConsumerAsync(UserId, consumerId))
            {
                return new MeetingMessage { Code = 400, Message = "CloseConsumer 失败" };
            }

            return new MeetingMessage { Code = 200, Message = "CloseConsumer 成功" };
        }

        public async Task<MeetingMessage> PauseConsumer(string consumerId)
        {
            if (!await _scheduler.PauseConsumerAsync(UserId, consumerId))
            {
                return new MeetingMessage { Code = 400, Message = "PauseConsumer 失败" };
            }

            return new MeetingMessage { Code = 200, Message = "PauseConsumer 成功" };
        }

        public async Task<MeetingMessage> ResumeConsumer(string consumerId)
        {
            try
            {
                var consumer = await _scheduler.ResumeConsumerAsync(UserId, consumerId);
                if (consumer == null)
                {
                    return new MeetingMessage { Code = 400, Message = "ResumeConsumer 失败" };
                }

                // Message: consumerScore
                SendMessage(UserId, "consumerScore", new { ConsumerId = consumer.ConsumerId, Score = consumer.Score });
            }
            catch (Exception ex)
            {
                return new MeetingMessage
                {
                    Code = 400,
                    Message = $"ResumeConsumer 失败:{ex.Message}",
                };
            }

            return new MeetingMessage { Code = 200, Message = "ResumeConsumer 成功" };
        }

        public async Task<MeetingMessage> SetConsumerPreferedLayers(SetConsumerPreferedLayersRequest setConsumerPreferedLayersRequest)
        {
            if (!await _scheduler.SetConsumerPreferedLayersAsync(UserId, setConsumerPreferedLayersRequest))
            {
                return new MeetingMessage { Code = 400, Message = "SetConsumerPreferedLayers 失败" };
            }

            return new MeetingMessage { Code = 200, Message = "SetConsumerPreferedLayers 成功" };
        }

        public async Task<MeetingMessage> SetConsumerPriority(SetConsumerPriorityRequest setConsumerPriorityRequest)
        {
            if (!await _scheduler.SetConsumerPriorityAsync(UserId, setConsumerPriorityRequest))
            {
                return new MeetingMessage { Code = 400, Message = "SetConsumerPreferedLayers 失败" };
            }

            return new MeetingMessage { Code = 200, Message = "SetConsumerPriority 成功" };
        }

        public async Task<MeetingMessage> RequestConsumerKeyFrame(string consumerId)
        {
            if (!await _scheduler.RequestConsumerKeyFrameAsync(UserId, consumerId))
            {
                return new MeetingMessage { Code = 400, Message = "RequestConsumerKeyFrame 失败" };
            }

            return new MeetingMessage { Code = 200, Message = "RequestConsumerKeyFrame 成功" };
        }

        public async Task<MeetingMessage> GetTransportStats(string transportId)
        {
            var data = await _scheduler.GetTransportStatsAsync(UserId, transportId);
            return new MeetingMessage { Code = 200, Message = "GetTransportStats 成功", Data = data };
        }

        public async Task<MeetingMessage> GetProducerStats(string producerId)
        {
            var data = await _scheduler.GetProducerStatsAsync(UserId, producerId);
            return new MeetingMessage { Code = 200, Message = "GetProducerStats 成功", Data = data };
        }

        public async Task<MeetingMessage> GetConsumerStats(string consumerId)
        {
            var data = await _scheduler.GetConsumerStatsAsync(UserId, consumerId);
            return new MeetingMessage { Code = 200, Message = "GetConsumerStats 成功", Data = data };
        }

        public async Task<MeetingMessage> RestartIce(string transportId)
        {
            var iceParameters = await _scheduler.RestartIceAsync(UserId, transportId);
            return new MeetingMessage { Code = 200, Message = "RestartIce 成功", Data = iceParameters };
        }

        #region Private Methods

        private async Task CreateConsumer(Peer consumerPeer, Peer producerPeer, Producer producer, string roomId)
        {
            _logger.LogDebug($"CreateConsumer() | [consumerPeer:\"{consumerPeer.PeerId}\", producerPeer:\"{producerPeer.PeerId}\", producer:\"{producer.ProducerId}\"]");

            // Create the Consumer in paused mode.
            Consumer consumer;

            try
            {
                consumer = await _scheduler.ConsumeAsync(producerPeer.PeerId, consumerPeer.PeerId, producer, roomId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"CreateConsumer() | [error:\"{ex}\"]");
                return;
            }

            consumer.On("score", (score) =>
            {
                var data = (ConsumerScore)score!;
                // Message: consumerScore
                SendMessage(consumerPeer.PeerId, "consumerScore", new { ConsumerId = consumer.ConsumerId, Score = data });
            });

            // Set Consumer events.
            consumer.On("transportclose", _ =>
            {
            });

            consumer.On("producerclose", _ =>
            {
                // Message: consumerClosed
                SendMessage(consumerPeer.PeerId, "consumerClosed", new { ConsumerId = consumer.ConsumerId });
            });

            consumer.On("producerpause", _ =>
            {
                // Message: consumerPaused
                SendMessage(consumerPeer.PeerId, "consumerPaused", new { ConsumerId = consumer.ConsumerId });
            });

            consumer.On("producerresume", _ =>
            {
                // Message: consumerResumed
                SendMessage(consumerPeer.PeerId, "consumerResumed", new { ConsumerId = consumer.ConsumerId });
            });

            consumer.On("layerschange", layers =>
            {
                var data = (ConsumerLayers?)layers;

                // Message: consumerLayersChanged
                SendMessage(consumerPeer.PeerId, "consumerLayersChanged", new { ConsumerId = consumer.ConsumerId });
            });

            // NOTE: For testing.
            // await consumer.enableTraceEvent([ 'rtp', 'keyframe', 'nack', 'pli', 'fir' ]);
            // await consumer.enableTraceEvent([ 'pli', 'fir' ]);
            // await consumer.enableTraceEvent([ 'keyframe' ]);

            consumer.On("trace", trace =>
            {
                _logger.LogDebug($"consumer \"trace\" event [producerId:{consumer.ConsumerId}, trace:{trace}]");
            });

            // Send a request to the remote Peer with Consumer parameters.
            // Message: newConsumer
            SendMessage(consumerPeer.PeerId, "newConsumer", new ConsumeInfo
            {
                RoomId = roomId,
                ProducerPeerId = producerPeer.PeerId,
                Kind = consumer.Kind,
                ProducerId = producer.ProducerId,
                ConsumerId = consumer.ConsumerId,
                RtpParameters = consumer.RtpParameters,
                Type = consumer.Type,
                ProducerAppData = producer.AppData,
                ProducerPaused = consumer.ProducerPaused,
            });
        }

        private void SendMessage(string peerId, string type, object data)
        {
            if (type == "consumerLayersChanged" || type == "consumerScore" || type == "producerScore") return;
            var client = _hubContext.Clients.User(peerId);
            client.Notify(new MeetingNotification
            {
                Type = type,
                Data = data
            }).ContinueWithOnFaultedHandleLog(_logger);
        }

        #endregion
    }
}
