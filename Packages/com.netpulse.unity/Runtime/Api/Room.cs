using System.Collections.Generic;
using System.Linq;
using NetPulse.Common;

namespace NetPulse
{
    public sealed class Room
    {
        private readonly NetPulseClient _client;
        private readonly SortedDictionary<int, Player> _players = new();
        private readonly PropertyTable _properties = new();

        internal Room(NetPulseClient client, string name)
        {
            _client = client ?? throw new System.ArgumentNullException(nameof(client));
            Name = name;
        }

        public string Name { get; }

        public byte MaxPlayers { get; internal set; }

        public bool IsOpen { get; internal set; } = true;

        public bool IsVisible { get; internal set; } = true;

        public int MasterClientId { get; internal set; }

        public Player LocalPlayer { get; internal set; }

        public PropertyTable Properties => _properties;

        public IReadOnlyCollection<Player> Players => _players.Values;

        public int PlayerCount => _players.Count;

        public Player MasterClient => GetPlayer(MasterClientId);

        public Player GetPlayer(int actorId) => _players.TryGetValue(actorId, out var player) ? player : null;

        public bool TryGetPlayer(int actorId, out Player player) => _players.TryGetValue(actorId, out player);

        public Player GetNextPlayer(Player after) => _players.Values.FirstOrDefault(p => p.ActorId > after.ActorId) ?? _players.Values.FirstOrDefault();

        public T GetProperty<T>(string key, T defaultValue = default) => _properties.Get(key, defaultValue);

        public void SetProperty(string key, object value)
        {
            var delta = new PropertyTable();
            delta[key] = value;
            SetProperties(delta);
        }

        public void SetProperties(PropertyTable delta, PropertyTable expected = null) => _client.SetRoomProperties(delta, expected);

        internal void AddPlayer(Player player) => _players[player.ActorId] = player;

        internal bool RemovePlayer(int actorId) => _players.Remove(actorId);

        internal void ApplyProperties(PropertyTable delta) => _properties.Merge(delta);

        public override string ToString() => $"Room '{Name}' ({PlayerCount}/{MaxPlayers})";
    }
}
