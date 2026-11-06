using System;
using System.Collections.Generic;

namespace NetPulse
{
    public enum StatCategory
    {
        NetworkObject,
        Rpc,
        Message,
        StateSync,
        SyncVar,
        System
    }

    public sealed class NetStatEntry
    {
        internal NetStatEntry(StatCategory category, string name)
        {
            Category = category;
            Name = name;
        }

        public StatCategory Category { get; }

        public string Name { get; }

        public long BytesIn { get; internal set; }

        public long BytesOut { get; internal set; }

        public long MessagesIn { get; internal set; }

        public long MessagesOut { get; internal set; }

        public float BytesInPerSecond { get; internal set; }

        public float BytesOutPerSecond { get; internal set; }

        internal long WindowIn { get; set; }

        internal long WindowOut { get; set; }
    }

    public sealed class NetStats
    {
        private const double WindowSeconds = 1d;
        private const int MaxPendingTags = 4;

        private readonly Dictionary<StatKey, NetStatEntry> _map = new();
        private readonly List<NetStatEntry> _entries = new();
        private readonly Dictionary<int, string> _systemNames = new();
        private readonly StatKey[] _tags = new StatKey[MaxPendingTags];
        private int _tagCount;
        private int _packetBytes;
        private bool _packetClaimed;
        private int _packetLabel;
        private double _windowStart = -1d;
        private long _windowIn;
        private long _windowOut;

        public bool Enabled { get; set; }

        public IReadOnlyList<NetStatEntry> Entries => _entries;

        public long TotalBytesIn { get; private set; }

        public long TotalBytesOut { get; private set; }

        public long TotalMessagesIn { get; private set; }

        public long TotalMessagesOut { get; private set; }

        public float BytesInPerSecond { get; private set; }

        public float BytesOutPerSecond { get; private set; }

        public void Reset()
        {
            _entries.Clear();
            _map.Clear();
            _tagCount = 0;
            TotalBytesIn = 0L;
            TotalBytesOut = 0L;
            TotalMessagesIn = 0L;
            TotalMessagesOut = 0L;
            BytesInPerSecond = 0f;
            BytesOutPerSecond = 0f;
            _windowIn = 0L;
            _windowOut = 0L;
            _windowStart = -1d;
        }

        internal void Update(double now)
        {
            if (!Enabled)
            {
                return;
            }

            if (_windowStart < 0d)
            {
                _windowStart = now;
                return;
            }

            var elapsed = now - _windowStart;
            if (elapsed < WindowSeconds)
            {
                return;
            }

            BytesInPerSecond = (float)(_windowIn / elapsed);
            BytesOutPerSecond = (float)(_windowOut / elapsed);
            _windowIn = 0L;
            _windowOut = 0L;
            foreach (var entry in _entries)
            {
                entry.BytesInPerSecond = (float)(entry.WindowIn / elapsed);
                entry.BytesOutPerSecond = (float)(entry.WindowOut / elapsed);
                entry.WindowIn = 0L;
                entry.WindowOut = 0L;
            }

            _windowStart = now;
        }

        internal void Tag(StatCategory category, string name)
        {
            if (!Enabled || _tagCount >= MaxPendingTags)
            {
                return;
            }

            _tags[_tagCount++] = new StatKey(category, name);
        }

        internal void CommitOut(int bytes, int systemLabel)
        {
            if (!Enabled)
            {
                _tagCount = 0;
                return;
            }

            TotalBytesOut += bytes;
            TotalMessagesOut++;
            _windowOut += bytes;
            if (_tagCount == 0)
            {
                AddOut(new StatKey(StatCategory.System, SystemName(systemLabel)), bytes);
                return;
            }

            for (var i = 0; i < _tagCount; i++)
            {
                AddOut(_tags[i], bytes);
            }

            _tagCount = 0;
        }

        internal void BeginIn(int bytes)
        {
            if (!Enabled)
            {
                return;
            }

            _packetBytes = bytes;
            _packetClaimed = false;
            _packetLabel = 0;
            TotalBytesIn += bytes;
            TotalMessagesIn++;
            _windowIn += bytes;
        }

        internal int PacketBytes => _packetBytes;

        internal void DiscardTags() => _tagCount = 0;

        internal void ClaimIn() => _packetClaimed = true;

        internal void ReplayIn(int bytes) => _packetBytes = bytes;

        internal void LabelIn(int systemLabel) => _packetLabel = systemLabel;

        internal void RecordIn(StatCategory category, string name)
        {
            if (!Enabled)
            {
                return;
            }

            _packetClaimed = true;
            var entry = GetEntry(new StatKey(category, name));
            entry.BytesIn += _packetBytes;
            entry.MessagesIn++;
            entry.WindowIn += _packetBytes;
        }

        internal void EndIn()
        {
            if (!Enabled || _packetClaimed)
            {
                return;
            }

            var entry = GetEntry(new StatKey(StatCategory.System, SystemName(_packetLabel)));
            entry.BytesIn += _packetBytes;
            entry.MessagesIn++;
            entry.WindowIn += _packetBytes;
        }

        internal static int Label(byte kind, byte code) => (kind << 8) | code;

        private void AddOut(StatKey key, int bytes)
        {
            var entry = GetEntry(key);
            entry.BytesOut += bytes;
            entry.MessagesOut++;
            entry.WindowOut += bytes;
        }

        private NetStatEntry GetEntry(StatKey key)
        {
            if (_map.TryGetValue(key, out var entry))
            {
                return entry;
            }

            entry = new NetStatEntry(key.Category, key.Name);
            _map[key] = entry;
            _entries.Add(entry);
            return entry;
        }

        private string SystemName(int label)
        {
            if (_systemNames.TryGetValue(label, out var name))
            {
                return name;
            }

            var kind = label >> 8;
            var code = label & 0xFF;
            name = kind switch
            {
                1 => $"Request op {code}",
                2 => $"Response op {code}",
                3 => $"Event {code}",
                4 => "Ping",
                5 => "Pong",
                _ => "Other"
            };
            _systemNames[label] = name;
            return name;
        }

        private readonly struct StatKey : IEquatable<StatKey>
        {
            public StatKey(StatCategory category, string name)
            {
                Category = category;
                Name = name ?? string.Empty;
            }

            public StatCategory Category { get; }

            public string Name { get; }

            public bool Equals(StatKey other) => Category == other.Category && string.Equals(Name, other.Name, StringComparison.Ordinal);

            public override bool Equals(object obj) => obj is StatKey other && Equals(other);

            public override int GetHashCode() => ((int)Category * 397) ^ Name.GetHashCode();
        }
    }
}
