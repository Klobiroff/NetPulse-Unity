using NetPulse.Common;

namespace NetPulse
{
    public sealed class Player
    {
        private readonly NetPulseClient _client;
        private readonly PropertyTable _properties = new();

        internal Player(NetPulseClient client, int actorId, string nickName, string userId, bool isLocal)
        {
            _client = client ?? throw new System.ArgumentNullException(nameof(client));
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

        public void SetProperties(PropertyTable delta) => _client.SetPlayerProperties(this, delta);

        internal void ApplyProperties(PropertyTable delta) => _properties.Merge(delta);

        public override string ToString() => $"Player #{ActorId} '{NickName}'";
    }
}
