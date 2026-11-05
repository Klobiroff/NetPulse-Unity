namespace NetPulse
{
    public sealed class ConnectionOptions
    {
        public string Host { get; set; } = "127.0.0.1";

        public int Port { get; set; } = 5055;

        public string ConnectionKey { get; set; } = "NetPulse";

        public string AppId { get; set; } = string.Empty;

        public string AppVersion { get; set; } = "1.0";

        public string UserId { get; set; }

        public string NickName { get; set; }

        public string AuthToken { get; set; }

        public float RequestTimeout { get; set; } = 10f;

        public static ConnectionOptions From(NetPulseSettings settings, string userId, string nickName) => new()
        {
            Host = settings.Host,
            Port = settings.Port,
            ConnectionKey = settings.ConnectionKey,
            AppId = settings.AppId,
            AppVersion = settings.AppVersion,
            RequestTimeout = settings.RequestTimeout,
            UserId = userId,
            NickName = nickName
        };
    }
}
