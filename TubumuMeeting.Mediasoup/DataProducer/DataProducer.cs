﻿using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Tubumu.Core.Extensions;
using TubumuMeeting.Mediasoup.Extensions;

namespace TubumuMeeting.Mediasoup
{
    public class DataProducerInternalData
    {
        /// <summary>
        /// Router id.
        /// </summary>
        public string RouterId { get; }

        /// <summary>
        /// Transport id.
        /// </summary>
        public string TransportId { get; }

        /// <summary>
        /// DataProducer id.
        /// </summary>
        public string DataProducerId { get; }

        public DataProducerInternalData(string routerId, string transportId, string dataProducerId)
        {
            RouterId = routerId;
            TransportId = transportId;
            DataProducerId = dataProducerId;
        }
    }

    public class DataProducer : EventEmitter
    {
        /// <summary>
        /// Logger.
        /// </summary>
        private readonly ILogger<DataProducer> _logger;

        // TODO: (alby) Closed 的使用及线程安全。
        /// <summary>
        /// Whether the DataProducer is closed.
        /// </summary>
        private bool _closed;

        /// <summary>
        /// Internal data.
        /// </summary>
        private DataProducerInternalData _internal;

        /// <summary>
        /// DataProducer id.
        /// </summary>
        public string DataProducerId => _internal.DataProducerId;

        #region Producer data.

        /// <summary>
        /// SCTP stream parameters.
        /// </summary>
        public SctpStreamParameters? SctpStreamParameters { get; }

        /// <summary>
        /// DataChannel label.
        /// </summary>
        public string Label { get; }

        /// <summary>
        /// DataChannel protocol.
        /// </summary>
        public string Protocol { get; }

        #endregion Producer data.

        /// <summary>
        /// Channel instance.
        /// </summary>
        private readonly Channel _channel;

        /// <summary>
        /// PayloadChannel instance.
        /// </summary>
        private readonly PayloadChannel _payloadChannel;

        /// <summary>
        /// App custom data.
        /// </summary>
        public Dictionary<string, object>? AppData { get; private set; }

        /// <summary>
        /// Observer instance.
        /// </summary>
        public EventEmitter Observer { get; } = new EventEmitter();

        /// <summary>
        /// <para>Events:</para>
        /// <para>@emits transportclose</para>
        /// <para>@emits @close</para>
        /// <para>Observer events:</para>
        /// <para>@emits close</para>
        /// </summary>
        /// <param name="loggerFactory"></param>
        /// <param name="dataProducerInternalData"></param>
        /// <param name="sctpStreamParameters"></param>
        /// <param name="label"></param>
        /// <param name="protocol"></param>
        /// <param name="channel"></param>
        /// <param name="payloadChannel"></param>
        /// <param name="appData"></param>
        public DataProducer(ILoggerFactory loggerFactory,
            DataProducerInternalData dataProducerInternalData,
            SctpStreamParameters sctpStreamParameters,
            string label,
            string protocol,
            Channel channel,
            PayloadChannel payloadChannel,
            Dictionary<string, object>? appData
            )
        {
            _logger = loggerFactory.CreateLogger<DataProducer>();

            // Internal
            _internal = dataProducerInternalData;

            // Data
            SctpStreamParameters = sctpStreamParameters;
            Label = label;
            Protocol = protocol;

            _channel = channel;
            _payloadChannel = payloadChannel;
            AppData = appData;

            HandleWorkerNotifications();
        }

        /// <summary>
        /// Close the DataProducer.
        /// </summary>
        public void Close()
        {
            if (_closed)
            {
                return;
            }

            _logger.LogDebug($"Close() | DataProducer:{DataProducerId}");

            _closed = true;

            // Remove notification subscriptions.
            //_channel.MessageEvent -= OnChannelMessage;

            // Fire and forget
            _channel.RequestAsync(MethodId.DATA_PRODUCER_CLOSE, _internal).ContinueWithOnFaultedHandleLog(_logger);

            Emit("close");

            // Emit observer event.
            Observer.Emit("close");
        }

        /// <summary>
        /// Transport was closed.
        /// </summary>
        public void TransportClosed()
        {
            if (_closed)
            {
                return;
            }

            _logger.LogDebug($"TransportClosed() | DataProducer:{DataProducerId}");

            _closed = true;

            // Remove notification subscriptions.
            //_channel.MessageEvent -= OnChannelMessage;

            Emit("transportclose");

            // Emit observer event.
            Observer.Emit("close");
        }

        /// <summary>
        /// Dump DataProducer.
        /// </summary>
        public Task<string?> DumpAsync()
        {
            _logger.LogDebug($"DumpAsync() | DataProducer:{DataProducerId}");

            return _channel.RequestAsync(MethodId.DATA_PRODUCER_DUMP, _internal);
        }

        /// <summary>
        /// Get DataProducer stats. Return: DataProducerStat[]
        /// </summary>
        public Task<string?> GetStatsAsync()
        {
            _logger.LogDebug($"GetStatsAsync() | DataProducer:{DataProducerId}");

            return _channel.RequestAsync(MethodId.DATA_PRODUCER_GET_STATS, _internal);
        }

        /// <summary>
        /// Send data (just valid for DataProducers created on a DirectTransport).
        /// </summary>
        /// <param name="message"></param>
        /// <param name="ppid"></param>
        /// <returns></returns>
        public Task SendAsync(string message, int? ppid)
        {
            _logger.LogDebug($"SendAsync() | DataProducer:{DataProducerId}");

            /*
             * +-------------------------------+----------+
             * | Value                         | SCTP     |
             * |                               | PPID     |
             * +-------------------------------+----------+
             * | WebRTC String                 | 51       |
             * | WebRTC Binary Partial         | 52       |
             * | (Deprecated)                  |          |
             * | WebRTC Binary                 | 53       |
             * | WebRTC String Partial         | 54       |
             * | (Deprecated)                  |          |
             * | WebRTC String Empty           | 56       |
             * | WebRTC Binary Empty           | 57       |
             * +-------------------------------+----------+
             */

            if (ppid == null)
            {
                ppid = !message.IsNullOrEmpty() ? 51 : 56;
            }

            // Ensure we honor PPIDs.
            if (ppid == 56)
            {
                message = " ";
            }

            var notifyData = new NotifyData { PPID = ppid.Value };

            _payloadChannel.Notify("dataProducer.send", _internal, notifyData, Encoding.UTF8.GetBytes(message));

            return Task.CompletedTask;
        }

        /// <summary>
        /// Send data (just valid for DataProducers created on a DirectTransport).
        /// </summary>
        /// <param name="message"></param>
        /// <param name="ppid"></param>
        /// <returns></returns>
        public Task SendAsync(byte[] message, int? ppid)
        {
            _logger.LogDebug($"SendAsync() | DataProducer:{DataProducerId}");

            if (ppid == null)
            {
                ppid = !message.IsNullOrEmpty() ? 53 : 57;
            }

            // Ensure we honor PPIDs.
            if (ppid == 57)
            {
                message = new byte[1];
            }

            var notifyData = new NotifyData { PPID = ppid.Value };

            _payloadChannel.Notify("dataProducer.send", _internal, notifyData, message);

            return Task.CompletedTask;
        }

        #region Event Handlers

        private void HandleWorkerNotifications()
        {
            // No need to subscribe to any event.
        }

        #endregion Event Handlers
    }
}
