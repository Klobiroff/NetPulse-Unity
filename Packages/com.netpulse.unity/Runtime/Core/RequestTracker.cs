using System;
using System.Collections.Generic;
using NetPulse.Common;

namespace NetPulse
{
    public static class NetPulseReturnCode
    {
        public const short Timeout = -100;
        public const short Disconnected = -101;
        public const short InvalidState = -102;
        public const short Cancelled = -103;
    }

    public sealed class RequestTracker
    {
        private readonly Dictionary<ushort, PendingRequest> _pending = new();
        private readonly List<ushort> _expired = new();
        private ushort _nextId;

        public int PendingCount => _pending.Count;

        public ushort Add(byte opCode, double deadline, Action<OperationResponse> onResponse)
        {
            var id = NextId();
            _pending[id] = new PendingRequest(opCode, deadline, onResponse ?? throw new ArgumentNullException(nameof(onResponse)));
            return id;
        }

        public bool TryComplete(OperationResponse response)
        {
            if (!_pending.TryGetValue(response.RequestId, out var request))
            {
                return false;
            }

            _pending.Remove(response.RequestId);
            request.OnResponse(response);
            return true;
        }

        public void Tick(double now)
        {
            if (_pending.Count == 0)
            {
                return;
            }

            _expired.Clear();
            foreach (var pair in _pending)
            {
                if (now >= pair.Value.Deadline)
                {
                    _expired.Add(pair.Key);
                }
            }

            foreach (var id in _expired)
            {
                if (_pending.TryGetValue(id, out var request))
                {
                    _pending.Remove(id);
                    request.OnResponse(Synthetic(request.OpCode, id, NetPulseReturnCode.Timeout, "Request timed out"));
                }
            }
        }

        public void FailAll(short returnCode, string message)
        {
            if (_pending.Count == 0)
            {
                return;
            }

            var snapshot = new List<KeyValuePair<ushort, PendingRequest>>(_pending);
            _pending.Clear();
            foreach (var pair in snapshot)
            {
                pair.Value.OnResponse(Synthetic(pair.Value.OpCode, pair.Key, returnCode, message));
            }
        }

        private static OperationResponse Synthetic(byte opCode, ushort id, short returnCode, string message) => new()
        {
            OpCode = opCode,
            RequestId = id,
            ReturnCode = returnCode,
            DebugMessage = message,
            Parameters = new ParameterDictionary()
        };

        private ushort NextId()
        {
            do
            {
                _nextId++;
            }
            while (_nextId == 0 || _pending.ContainsKey(_nextId));

            return _nextId;
        }

        private readonly struct PendingRequest
        {
            public PendingRequest(byte opCode, double deadline, Action<OperationResponse> onResponse)
            {
                OpCode = opCode;
                Deadline = deadline;
                OnResponse = onResponse;
            }

            public byte OpCode { get; }

            public double Deadline { get; }

            public Action<OperationResponse> OnResponse { get; }
        }
    }
}
