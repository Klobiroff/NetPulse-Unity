using NetPulse.Common;
using NUnit.Framework;
using UnityEngine;

namespace NetPulse.Tests
{
    public sealed class UnityTypeSerializerTests
    {
        [OneTimeSetUp]
        public void Register() => UnityTypeSerializers.Register();

        [Test]
        public void Codes_MatchSpec()
        {
            Assert.AreEqual(100, UnityTypeSerializers.Vector2Code);
            Assert.AreEqual(101, UnityTypeSerializers.Vector3Code);
            Assert.AreEqual(102, UnityTypeSerializers.QuaternionCode);
            Assert.AreEqual(103, UnityTypeSerializers.ColorCode);
            Assert.AreEqual(104, UnityTypeSerializers.Vector2IntCode);
            Assert.AreEqual(105, UnityTypeSerializers.Vector3IntCode);
            for (byte code = 100; code <= 105; code++)
            {
                Assert.IsTrue(NetTypeRegistry.IsRegistered(code));
            }
        }

        [Test]
        public void Vector2_RoundTrips() => AssertRoundTrip(new Vector2(1.5f, -2.25f));

        [Test]
        public void Vector3_RoundTrips() => AssertRoundTrip(new Vector3(1f, 2f, 3f));

        [Test]
        public void Quaternion_RoundTrips() => AssertRoundTrip(new Quaternion(0.1f, 0.2f, 0.3f, 0.9f));

        [Test]
        public void Color_RoundTrips() => AssertRoundTrip(new Color(0.1f, 0.2f, 0.3f, 0.4f));

        [Test]
        public void Vector2Int_RoundTrips() => AssertRoundTrip(new Vector2Int(-3, 7));

        [Test]
        public void Vector3Int_RoundTrips() => AssertRoundTrip(new Vector3Int(4, -5, 6));

        [Test]
        public void InsideParameterDictionary_RoundTrips()
        {
            var parameters = new ParameterDictionary { [1] = new Vector3(1f, 2f, 3f), [2] = "text" };
            var writer = new NetWriter();
            parameters.WriteTo(writer);

            var result = ParameterDictionary.ReadFrom(new NetReader(writer.ToArray()));

            Assert.AreEqual(new Vector3(1f, 2f, 3f), result.Get<Vector3>(1));
            Assert.AreEqual("text", result.Get<string>(2));
        }

        [Test]
        public void InsidePropertyTable_RoundTrips()
        {
            var table = new PropertyTable();
            table["color"] = Color.red;
            table["pos"] = new Vector2(3f, 4f);
            var writer = new NetWriter();
            writer.WriteObject(table);

            var result = (PropertyTable)new NetReader(writer.ToArray()).ReadObject();

            Assert.AreEqual(Color.red, result.Get<Color>("color"));
            Assert.AreEqual(new Vector2(3f, 4f), result.Get<Vector2>("pos"));
        }

        [Test]
        public void NetStream_RoundTripsUnityAndPrimitiveValues()
        {
            var writer = new NetWriter();
            var write = new NetStream(writer);
            var position = new Vector3(1f, 2f, 3f);
            var health = 87;
            var name = "hero";
            write.Serialize(ref position);
            write.Serialize(ref health);
            write.Serialize(ref name);

            var read = new NetStream(new NetReader(writer.ToArray()));
            var readPosition = Vector3.zero;
            var readHealth = 0;
            var readName = string.Empty;
            read.Serialize(ref readPosition);
            read.Serialize(ref readHealth);
            read.Serialize(ref readName);

            Assert.IsTrue(write.IsWriting);
            Assert.IsTrue(read.IsReading);
            Assert.AreEqual(position, readPosition);
            Assert.AreEqual(health, readHealth);
            Assert.AreEqual(name, readName);
        }

        private static void AssertRoundTrip<T>(T value)
        {
            var writer = new NetWriter();
            writer.WriteObject(value);
            var result = new NetReader(writer.ToArray()).ReadObject();
            Assert.IsInstanceOf<T>(result);
            Assert.AreEqual(value, (T)result);
        }
    }
}
