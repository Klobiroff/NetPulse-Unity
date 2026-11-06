using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace NetPulse.Editor
{
    public sealed class NetPulseHubWindow : EditorWindow
    {
        private const double RefreshInterval = 0.25d;

        private static readonly string[] Tabs = { "Overview", "Traffic" };
        private static readonly string[] Columns = { "Category", "Name", "In B/s", "Out B/s", "In total", "Out total", "Msgs in/out" };
        private static readonly float[] ColumnWidths = { 90f, 200f, 64f, 64f, 76f, 76f, 80f };

        private readonly List<NetStatEntry> _rows = new();

        private int _tab;
        private int _categoryFilter;
        private string _nameFilter = string.Empty;
        private int _sortColumn = 2;
        private bool _sortDescending = true;
        private Vector2 _trafficScroll;
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
            _tab = GUILayout.Toolbar(_tab, Tabs);
            if (_tab == 1)
            {
                DrawTrafficTab();
                return;
            }

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            DrawStatus();
            DrawControls();
            DrawRoom();
            DrawTraffic();
            DrawRegistry();
            EditorGUILayout.EndScrollView();
        }

        private void DrawTrafficTab()
        {
            var client = Application.isPlaying ? Pulse.ClientOrNull : null;
            if (client == null)
            {
                EditorGUILayout.HelpBox("Enter Play Mode and connect to see the traffic profiler.", MessageType.Info);
                return;
            }

            var stats = client.Stats;
            DrawTrafficHeader(stats);
            if (!stats.Enabled)
            {
                EditorGUILayout.HelpBox("Statistics collection is off. Enable it above or set NetPulseSettings > Collect Stats.", MessageType.Warning);
                return;
            }

            RebuildRows(stats);
            DrawColumnHeaders();
            _trafficScroll = EditorGUILayout.BeginScrollView(_trafficScroll);
            foreach (var entry in _rows)
            {
                DrawRow(entry);
            }

            EditorGUILayout.EndScrollView();
        }

        private void DrawTrafficHeader(NetStats stats)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                stats.Enabled = GUILayout.Toggle(stats.Enabled, "Collect", GUILayout.Width(64f));
                if (GUILayout.Button("Reset", GUILayout.Width(60f)))
                {
                    stats.Reset();
                }

                GUILayout.FlexibleSpace();
                GUILayout.Label($"In {stats.BytesInPerSecond:F0} B/s   Out {stats.BytesOutPerSecond:F0} B/s");
            }

            GUILayout.Label($"Session: in {stats.TotalBytesIn / 1024f:F1} KB / {stats.TotalMessagesIn} msgs, out {stats.TotalBytesOut / 1024f:F1} KB / {stats.TotalMessagesOut} msgs");
            using (new EditorGUILayout.HorizontalScope())
            {
                var names = new List<string> { "All categories" };
                names.AddRange(Enum.GetNames(typeof(StatCategory)));
                _categoryFilter = EditorGUILayout.Popup(_categoryFilter, names.ToArray(), GUILayout.Width(130f));
                _nameFilter = EditorGUILayout.TextField(_nameFilter);
            }
        }

        private void RebuildRows(NetStats stats)
        {
            _rows.Clear();
            foreach (var entry in stats.Entries)
            {
                if (_categoryFilter > 0 && (int)entry.Category != _categoryFilter - 1)
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(_nameFilter) && entry.Name.IndexOf(_nameFilter, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                _rows.Add(entry);
            }

            _rows.Sort(CompareRows);
        }

        private int CompareRows(NetStatEntry a, NetStatEntry b)
        {
            var result = _sortColumn switch
            {
                0 => a.Category.CompareTo(b.Category),
                1 => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase),
                2 => a.BytesInPerSecond.CompareTo(b.BytesInPerSecond),
                3 => a.BytesOutPerSecond.CompareTo(b.BytesOutPerSecond),
                4 => a.BytesIn.CompareTo(b.BytesIn),
                5 => a.BytesOut.CompareTo(b.BytesOut),
                _ => (a.MessagesIn + a.MessagesOut).CompareTo(b.MessagesIn + b.MessagesOut)
            };
            if (result == 0)
            {
                result = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            }

            return _sortDescending ? -result : result;
        }

        private void DrawColumnHeaders()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                for (var i = 0; i < Columns.Length; i++)
                {
                    var label = i == _sortColumn ? Columns[i] + (_sortDescending ? " v" : " ^") : Columns[i];
                    if (!GUILayout.Button(label, EditorStyles.toolbarButton, GUILayout.Width(ColumnWidths[i])))
                    {
                        continue;
                    }

                    _sortDescending = i == _sortColumn ? !_sortDescending : i > 1;
                    _sortColumn = i;
                }
            }
        }

        private static void DrawRow(NetStatEntry entry)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label(entry.Category.ToString(), GUILayout.Width(ColumnWidths[0]));
                GUILayout.Label(entry.Name, GUILayout.Width(ColumnWidths[1]));
                GUILayout.Label(entry.BytesInPerSecond.ToString("F0"), GUILayout.Width(ColumnWidths[2]));
                GUILayout.Label(entry.BytesOutPerSecond.ToString("F0"), GUILayout.Width(ColumnWidths[3]));
                GUILayout.Label(entry.BytesIn.ToString(), GUILayout.Width(ColumnWidths[4]));
                GUILayout.Label(entry.BytesOut.ToString(), GUILayout.Width(ColumnWidths[5]));
                GUILayout.Label($"{entry.MessagesIn}/{entry.MessagesOut}", GUILayout.Width(ColumnWidths[6]));
            }
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
