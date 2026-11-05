using System;
using System.Collections.Generic;
using NetPulse.Common;

namespace NetPulse.Tests
{
    public sealed class FakeTransport : INetTransport
    {
        private readonly Queue<byte[]> _sent = new();
        private readonly NetWriter _writer = new(256);

        public event Action Connected;

        public event Action<string> Disconnected;

        public event Action<ArraySegment<byte>, DeliveryMode> DataReceived;

        public bool IsConnected { get; private set; }

        public int RoundTripTimeMs => 30;

        public string Host { get; private set; }

        public int Port { get; private set; }

        public int PendingCount => _sent.Count;

        public void Connect(string host, int port, string connectionKey)
        {
            Host = host;
            Port = port;
        }

        public void Send(ArraySegment<byte> data, DeliveryMode mode)
        {
            var copy = new byte[data.Count];
            Buffer.BlockCopy(data.Array, data.Offset, copy, 0, data.Count);
            _sent.Enqueue(copy);
        }

        public void Poll()
        {
        }

        public void Disconnect() => IsConnected = false;

        public void SimulateConnected()
        {
            IsConnected = true;
            Connected?.Invoke();
        }

        public void SimulateDisconnected(string reason)
        {
            IsConnected = false;
            Disconnected?.Invoke(reason);
        }

        public OperationRequest NextRequest()
        {
            while (_sent.Count > 0)
            {
                var reader = new NetReader(_sent.Dequeue());
                if (Protocol.ReadKind(reader) == MessageKind.OperationRequest)
                {
                    return Protocol.ReadOperationRequest(reader);
                }
            }

            throw new InvalidOperationException("No pending request");
        }

        public List<OperationRequest> DrainRequests()
        {
            var result = new List<OperationRequest>();
            while (_sent.Count > 0)
            {
                var reader = new NetReader(_sent.Dequeue());
                if (Protocol.ReadKind(reader) == MessageKind.OperationRequest)
                {
                    result.Add(Protocol.ReadOperationRequest(reader));
                }
            }

            return result;
        }

        public void Respond(OperationRequest request, short returnCode, ParameterDictionary parameters = null, string message = null)
        {
            var response = new OperationResponse
            {
                OpCode = request.OpCode,
                RequestId = request.RequestId,
                ReturnCode = returnCode,
                DebugMessage = message,
                Parameters = parameters ?? new ParameterDictionary()
            };
            DataReceived?.Invoke(Protocol.Serialize(_writer, response), DeliveryMode.ReliableOrdered);
        }

        public void SendEvent(byte code, ParameterDictionary parameters)
        {
            var data = new EventData { Code = code, Parameters = parameters ?? new ParameterDictionary() };
            DataReceived?.Invoke(Protocol.Serialize(_writer, data), DeliveryMode.ReliableOrdered);
        }
    }
}
