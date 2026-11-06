using System.Xml.Linq;
using NUnit.Framework;

namespace NetPulse.Tests.Editor
{
    public sealed partial class LinkXmlGeneratorTests
    {
        private const string TestAssembly = "NetPulse.Tests.Editor";

        [Test]
        public void Generate_ContainsRpcSyncedAndSerializableTypes()
        {
            var xml = NetPulse.Editor.LinkXmlGenerator.Generate(name => name == TestAssembly, false);
            var document = XDocument.Parse(xml);

            var behaviour = FindType(document, "NetPulse.Tests.Editor.LinkXmlGeneratorTests/LinkTestBehaviour");
            Assert.IsNotNull(behaviour);
            Assert.IsTrue(HasAttribute(behaviour, "field", "name", "_hp"));
            Assert.IsTrue(HasAttribute(behaviour, "method", "signature", "System.Void Hit(System.Int32,NetPulse.NetRpcInfo)"));
            Assert.IsTrue(HasAttribute(behaviour, "method", "signature", "System.Void OnSerialize(NetPulse.NetStream)"));
            Assert.IsNotNull(FindType(document, "NetPulse.Tests.Editor.LinkXmlGeneratorTests/LinkTestSerializable"));
        }

        [Test]
        public void Generate_PreservesNetSerializableTypesAndGeneratedRpcDispatch()
        {
            var xml = NetPulse.Editor.LinkXmlGenerator.Generate(name => name == TestAssembly, false);
            var document = XDocument.Parse(xml);

            var behaviour = FindType(document, "NetPulse.Tests.Editor.LinkXmlGeneratorTests/LinkTestBehaviour");
            Assert.IsTrue(HasAttribute(behaviour, "method", "name", "InvokeGeneratedRpc"));
            var netType = FindType(document, "NetPulse.Tests.Editor.LinkXmlGeneratorTests/LinkTestNetType");
            Assert.IsNotNull(netType);
            Assert.IsTrue(HasAttribute(netType, "method", "name", "NetWrite"));
        }

        [Test]
        public void Generate_SkipsPackageAssembliesUnlessRequested()
        {
            var without = XDocument.Parse(NetPulse.Editor.LinkXmlGenerator.Generate(_ => true, false));
            Assert.IsNull(FindAssembly(without, "NetPulse.Runtime"));
            Assert.IsNull(FindType(without, "NetPulse.NetworkTransform"));

            var with = XDocument.Parse(NetPulse.Editor.LinkXmlGenerator.Generate(_ => true, true));
            Assert.AreEqual("all", FindAssembly(with, "NetPulse.Runtime").Attribute("preserve").Value);
            Assert.IsNull(FindType(with, "NetPulse.NetworkTransform"));
        }

        [Test]
        public void Generate_ExcludesAssembliesRejectedByFilter()
        {
            var document = XDocument.Parse(NetPulse.Editor.LinkXmlGenerator.Generate(_ => false, false));
            Assert.IsNull(FindAssembly(document, TestAssembly));
        }

        private static XElement FindAssembly(XDocument document, string name)
        {
            foreach (var element in document.Root.Elements("assembly"))
            {
                if ((string)element.Attribute("fullname") == name)
                {
                    return element;
                }
            }

            return null;
        }

        private static XElement FindType(XDocument document, string fullName)
        {
            foreach (var type in document.Root.Descendants("type"))
            {
                if ((string)type.Attribute("fullname") == fullName)
                {
                    return type;
                }
            }

            return null;
        }

        private static bool HasAttribute(XElement type, string element, string attribute, string value)
        {
            foreach (var child in type.Elements(element))
            {
                if ((string)child.Attribute(attribute) == value)
                {
                    return true;
                }
            }

            return false;
        }

        public sealed partial class LinkTestBehaviour : NetworkBehaviour
        {
            [Synced] private int _hp;

            public override void OnSerialize(NetStream stream)
            {
                stream.Serialize(ref _hp);
            }

            [NetRpc]
            private void Hit(int damage, NetRpcInfo info)
            {
                _hp -= damage;
            }
        }

        [NetSerializable]
        public partial struct LinkTestNetType
        {
            public int Value;
        }

        public sealed class LinkTestSerializable : INetSerializable
        {
            public void Serialize(NetStream stream)
            {
            }
        }
    }
}
