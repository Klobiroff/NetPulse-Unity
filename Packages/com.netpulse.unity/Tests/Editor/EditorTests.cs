using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace NetPulse.Tests.Editor
{
    public sealed class NetworkPrefabRegistryTests
    {
        private GameObject _first;
        private GameObject _second;
        private NetworkPrefabRegistry _registry;

        [SetUp]
        public void SetUp()
        {
            _first = new GameObject("Player");
            _second = new GameObject("Enemy");
            _registry = ScriptableObject.CreateInstance<NetworkPrefabRegistry>();
            _registry.SetEntries(
                new List<NetworkPrefabEntry>
                {
                    new(1, "guid-a", _first),
                    new(7, "guid-b", _second)
                },
                8);
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(_first);
            Object.DestroyImmediate(_second);
            Object.DestroyImmediate(_registry);
        }

        [Test]
        public void Lookup_ByIdAndReference()
        {
            Assert.IsTrue(_registry.TryGetPrefab(7, out var prefab));
            Assert.AreSame(_second, prefab);
            Assert.IsTrue(_registry.TryGetId(_first, out var id));
            Assert.AreEqual(1, id);
        }

        [Test]
        public void Lookup_ByName()
        {
            Assert.IsTrue(_registry.TryGetPrefab("Enemy", out var prefab));
            Assert.AreSame(_second, prefab);
            Assert.IsFalse(_registry.TryGetPrefab("Missing", out _));
        }

        [Test]
        public void Lookup_UnknownReturnsFalse()
        {
            Assert.IsFalse(_registry.TryGetPrefab(99, out _));
            Assert.IsFalse(_registry.TryGetId(null, out _));
        }

        [Test]
        public void NextId_IsPreserved() => Assert.AreEqual(8, _registry.NextId);
    }

    public sealed class NetPulseSettingsTests
    {
        [Test]
        public void Defaults_MatchSpec()
        {
            var settings = ScriptableObject.CreateInstance<NetPulseSettings>();
            try
            {
                Assert.AreEqual(5055, settings.Port);
                Assert.AreEqual("NetPulse", settings.ConnectionKey);
                Assert.Greater(settings.SendRate, 0);
                Assert.Greater(settings.SerializationRate, 0);
            }
            finally
            {
                Object.DestroyImmediate(settings);
            }
        }

        [Test]
        public void ConnectionOptions_CopySettings()
        {
            var settings = ScriptableObject.CreateInstance<NetPulseSettings>();
            try
            {
                settings.Host = "example.org";
                settings.AppId = "demo";
                var options = ConnectionOptions.From(settings, "user", "nick");
                Assert.AreEqual("example.org", options.Host);
                Assert.AreEqual("demo", options.AppId);
                Assert.AreEqual("user", options.UserId);
                Assert.AreEqual("nick", options.NickName);
            }
            finally
            {
                Object.DestroyImmediate(settings);
            }
        }
    }
}
