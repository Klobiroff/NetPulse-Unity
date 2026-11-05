using System.Text;
using UnityEditor;
using UnityEngine;

namespace NetPulse.Editor
{
    public sealed class NetPulseHubWindow : EditorWindow
    {
        private const double RefreshInterval = 0.25d;

        private Vector2 _scroll;
        private double _lastRefresh;
        private long _lastBytesIn;
        private long _lastBytesOut;
        private double _lastRateTime;
        private float _bytesInPerSecond;
        private float _bytesOutPerSecond;

        public static void Open()
        {
            var window = GetWindow<NetPulseHubWindow>("NetPulse Hub");
            window.minSize = new Vector2(320f, 360f);
            window.Show();
        }

        private void OnEnable() => EditorApplication.update += OnUpdate;

        private void OnDisable() => EditorApplication.update -= OnUpdate;

        private void OnUpdate()
        {
            if (!Application.isPlaying || EditorApplication.timeSinceStartup - _lastRefresh < RefreshInterval)
            {
                return;
            }

            _lastRefresh = EditorApplication.timeSinceStartup;
            UpdateRates();
            Repaint();
        }

        private void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            DrawStatus();
            DrawControls();
            DrawRoom();
            DrawTraffic();
            DrawRegistry();
            EditorGUILayout.EndScrollView();
        }

        private void UpdateRates()
        {
            var client = Pulse.ClientOrNull;
            if (client == null)
            {
                return;
            }

            var now = EditorApplication.timeSinceStartup;
            var elapsed = now - _lastRateTime;
            if (elapsed <= 0d)
            {
                return;
            }

            _bytesInPerSecond = (float)((client.BytesReceived - _lastBytesIn) / elapsed);
            _bytesOutPerSecond = (float)((client.BytesSent - _lastBytesOut) / elapsed);
            _lastBytesIn = client.BytesReceived;
            _lastBytesOut = client.BytesSent;
            _lastRateTime = now;
        }

        private void DrawStatus()
        {
            EditorGUILayout.LabelField("Connection", EditorStyles.boldLabel);
            if (!Application.isPlaying)
            {
                EditorGUILayout.HelpBox("Enter Play Mode to see live status.", MessageType.Info);
                var settings = NetPulseAssets.FindSettings();
                if (settings == null)
                {
                    EditorGUILayout.HelpBox("No NetPulseSettings asset yet.", MessageType.Warning);
                }

                return;
            }

            var client = Pulse.ClientOrNull;
            var state = client?.State ?? NetState.Disconnected;
            EditorGUILayout.LabelField("State", state.ToString());
            EditorGUILayout.LabelField("Mode", client != null && client.OfflineMode ? "Offline" : "Online");
            EditorGUILayout.LabelField("Ping", client == null ? "-" : client.RoundTripTimeMs + " ms");
            EditorGUILayout.LabelField("Server time", client == null ? "-" : client.ServerTime.ToString("F2"));
            EditorGUILayout.LabelField("User id", client?.UserId ?? "-");
            EditorGUILayout.LabelField("Nickname", client?.NickName ?? "-");
        }

        private void DrawControls()
        {
            if (!Application.isPlaying)
            {
                return;
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                var connected = Pulse.State != NetState.Disconnected;
                using (new EditorGUI.DisabledScope(connected))
                {
                    if (GUILayout.Button("Connect"))
                    {
                        Pulse.Connect();
                    }
                }

                using (new EditorGUI.DisabledScope(!connected))
                {
                    if (GUILayout.Button("Disconnect"))
                    {
                        Pulse.Disconnect();
                    }
                }
            }
        }

        private void DrawRoom()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Room", EditorStyles.boldLabel);
            var room = Application.isPlaying ? Pulse.CurrentRoom : null;
            if (room == null)
            {
                EditorGUILayout.LabelField("Not in a room");
                return;
            }

            EditorGUILayout.LabelField("Name", room.Name);
            EditorGUILayout.LabelField("Players", room.PlayerCount + " / " + (room.MaxPlayers == 0 ? "unlimited" : room.MaxPlayers.ToString()));
            EditorGUILayout.LabelField("Open / Visible", room.IsOpen + " / " + room.IsVisible);
            EditorGUILayout.LabelField("Network objects", Pulse.ObjectsOrNull?.Count.ToString() ?? "0");
            foreach (var player in room.Players)
            {
                var marker = new StringBuilder();
                if (player.IsLocal)
                {
                    marker.Append(" (you)");
                }

                if (player.IsMasterClient)
                {
                    marker.Append(" [master]");
                }

                if (player.IsInactive)
                {
                    marker.Append(" [inactive]");
                }

                EditorGUILayout.LabelField("#" + player.ActorId, player.NickName + marker);
            }
        }

        private void DrawTraffic()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Traffic", EditorStyles.boldLabel);
            var client = Application.isPlaying ? Pulse.ClientOrNull : null;
            if (client == null)
            {
                EditorGUILayout.LabelField("-");
                return;
            }

            EditorGUILayout.LabelField("Sent", $"{client.MessagesSent} msgs, {client.BytesSent / 1024f:F1} KB ({_bytesOutPerSecond / 1024f:F1} KB/s)");
            EditorGUILayout.LabelField("Received", $"{client.MessagesReceived} msgs, {client.BytesReceived / 1024f:F1} KB ({_bytesInPerSecond / 1024f:F1} KB/s)");
        }

        private void DrawRegistry()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Prefab registry", EditorStyles.boldLabel);
            var settings = NetPulseAssets.FindSettings();
            var registry = settings == null ? null : settings.PrefabRegistry;
            EditorGUILayout.LabelField("Registered prefabs", registry == null ? "none" : registry.Entries.Count.ToString());
            if (GUILayout.Button("Rebuild Prefab Registry"))
            {
                PrefabRegistryAutoBuild.RebuildMenu();
            }
        }
    }
}
