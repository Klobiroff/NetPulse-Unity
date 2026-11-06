using System;
using NetPulse.Common;

namespace NetPulse
{
    public sealed class Player
    {
        private readonly NetPulseClient _client;
        private readonly PropertyTable _properties = new();

        internal Player(NetPulseClient client, int actorId, string nickName, string userId, bool isLocal)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            ActorId = actorId;
            NickName = nickName ?? string.Empty;
            UserId = userId ?? string.Empty;
            IsLocal = isLocal;
        }

        public int ActorId { get; }

        public string NickName { get; internal set; }

        public string UserId { get; internal set; }

        public bool IsLocal { get; }

        public bool IsInactive { get; internal set; }

        public bool IsMasterClient => _client.CurrentRoom != null && _client.CurrentRoom.MasterClientId == ActorId;

        public PropertyTable Properties => _properties;

        public T GetProperty<T>(string key, T defaultValue = default) => _properties.Get(key, defaultValue);

        public void SetProperty(string key, object value)
        {
            var delta = new PropertyTable();
            delta[key] = value;
            SetProperties(delta);
        }

        public T Get<T>(PlayerKey<T> key, T defaultValue = default) => _properties.Get((key ?? throw new ArgumentNullException(nameof(key))).Name, defaultValue);

        public void Set<T>(PlayerKey<T> key, T value) => SetProperty((key ?? throw new ArgumentNullException(nameof(key))).Name, value);

        public NetOperation SetAsync<T>(PlayerKey<T> key, T value, T expected)
        {
            var name = (key ?? throw new ArgumentNullException(nameof(key))).Name;
            var delta = new PropertyTable();
            delta[name] = value;
            var guard = new PropertyTable();
            guard[name] = expected;
            return _client.SetPlayerProperties(this, delta, guard);
        }

        public IDisposable OnChanged<T>(PlayerKey<T> key, Action<Player, T> handler)
        {
            var name = (key ?? throw new ArgumentNullException(nameof(key))).Name;
            if (handler == null)
            {
                throw new ArgumentNullException(nameof(handler));
            }

            Action<Player, PropertyTable> listener = (player, changed) =>
            {
                if (player == this && changed != null && changed.ContainsKey(name))
                {
                    handler(player, changed.Get<T>(name));
                }
            };
            _client.Events.OnPlayerPropertiesChanged += listener;
            return new Unsubscriber(() => _client.Events.OnPlayerPropertiesChanged -= listener);
        }

        public void SetProperties(PropertyTable delta) => _client.SetPlayerProperties(this, delta);

        internal void ApplyProperties(PropertyTable delta) => _properties.Merge(delta);

        public override string ToString() => $"Player #{ActorId} '{NickName}'";
    }
}
