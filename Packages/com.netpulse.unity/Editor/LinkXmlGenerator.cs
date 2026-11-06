using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Xml;
using UnityEditor;

namespace NetPulse.Editor
{
    public static class LinkXmlGenerator
    {
        public static readonly string[] PackageAssemblies =
        {
            "NetPulse.Runtime",
            "NetPulse.Common",
            "NetPulse.LiteNetLib"
        };

        private static readonly string[] BehaviourReflectedMethods =
        {
            "OnSerialize",
            "RegisterSyncVars",
            "InvokeGeneratedRpc"
        };

        private const BindingFlags AllMembers = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        public static string Generate(Func<string, bool> includeAssembly, bool includePackageAssemblies)
        {
            var methods = new List<MethodInfo>(TypeCache.GetMethodsWithAttribute<NetRpcAttribute>());
            var behaviours = new List<Type>(TypeCache.GetTypesDerivedFrom<NetworkBehaviour>());
            var serializables = new List<Type>(TypeCache.GetTypesDerivedFrom<INetSerializable>());
            var netTypes = new List<Type>(TypeCache.GetTypesWithAttribute<NetSerializableAttribute>());
            netTypes.AddRange(TypeCache.GetTypesDerivedFrom<INetMessage>());
            return Build(methods, behaviours, serializables, includeAssembly, includePackageAssemblies, netTypes);
        }

        internal static string Build(
            IEnumerable<MethodInfo> rpcMethods,
            IEnumerable<Type> behaviours,
            IEnumerable<Type> serializables,
            Func<string, bool> includeAssembly,
            bool includePackageAssemblies,
            IEnumerable<Type> netTypes = null)
        {
            var assemblies = new SortedDictionary<string, SortedDictionary<string, TypeEntry>>(StringComparer.Ordinal);
            foreach (var method in rpcMethods)
            {
                AddMethod(assemblies, method, includeAssembly);
            }

            foreach (var type in behaviours)
            {
                if (type.IsAbstract)
                {
                    continue;
                }

                AddBehaviour(assemblies, type, includeAssembly);
            }

            foreach (var type in serializables)
            {
                if (type.IsAbstract || type.IsInterface)
                {
                    continue;
                }

                AddType(assemblies, type, includeAssembly);
            }

            if (netTypes != null)
            {
                foreach (var type in netTypes)
                {
                    if (!type.IsAbstract && !type.IsInterface)
                    {
                        AddNetType(assemblies, type, includeAssembly);
                    }
                }
            }

            return Write(assemblies, includePackageAssemblies);
        }

        private static void AddBehaviour(SortedDictionary<string, SortedDictionary<string, TypeEntry>> assemblies, Type type, Func<string, bool> includeAssembly)
        {
            AddType(assemblies, type, includeAssembly);
            for (var current = type; current != null && current != typeof(NetworkBehaviour); current = current.BaseType)
            {
                foreach (var field in current.GetFields(AllMembers))
                {
                    if (field.IsDefined(typeof(SyncedAttribute), false))
                    {
                        GetEntry(assemblies, current, includeAssembly)?.Fields.Add(field.Name);
                    }
                }

                foreach (var method in current.GetMethods(AllMembers))
                {
                    if (Array.IndexOf(BehaviourReflectedMethods, method.Name) >= 0 || method.IsDefined(typeof(NetRpcAttribute), false))
                    {
                        AddMethod(assemblies, method, includeAssembly);
                    }
                }
            }
        }

        private static void AddNetType(SortedDictionary<string, SortedDictionary<string, TypeEntry>> assemblies, Type type, Func<string, bool> includeAssembly)
        {
            var entry = GetEntry(assemblies, type, includeAssembly);
            if (entry == null)
            {
                return;
            }

            foreach (var method in type.GetMethods(AllMembers))
            {
                if (method.Name == "NetWrite" || method.Name == "NetRead")
                {
                    entry.Methods.Add(Signature(method));
                }
            }
        }

        private static void AddMethod(SortedDictionary<string, SortedDictionary<string, TypeEntry>> assemblies, MethodInfo method, Func<string, bool> includeAssembly)
        {
            if (method.DeclaringType == null || method.IsGenericMethodDefinition)
            {
                return;
            }

            var entry = GetEntry(assemblies, method.DeclaringType, includeAssembly);
            entry?.Methods.Add(Signature(method));
        }

        private static void AddType(SortedDictionary<string, SortedDictionary<string, TypeEntry>> assemblies, Type type, Func<string, bool> includeAssembly) =>
            GetEntry(assemblies, type, includeAssembly);

        private static TypeEntry GetEntry(SortedDictionary<string, SortedDictionary<string, TypeEntry>> assemblies, Type type, Func<string, bool> includeAssembly)
        {
            if (type.IsGenericType && !type.IsGenericTypeDefinition)
            {
                type = type.GetGenericTypeDefinition();
            }

            var assemblyName = type.Assembly.GetName().Name;
            if (Array.IndexOf(PackageAssemblies, assemblyName) >= 0 || !includeAssembly(assemblyName))
            {
                return null;
            }

            if (!assemblies.TryGetValue(assemblyName, out var types))
            {
                types = new SortedDictionary<string, TypeEntry>(StringComparer.Ordinal);
                assemblies[assemblyName] = types;
            }

            var name = TypeName(type);
            if (!types.TryGetValue(name, out var entry))
            {
                entry = new TypeEntry();
                types[name] = entry;
            }

            return entry;
        }

        private static string Write(SortedDictionary<string, SortedDictionary<string, TypeEntry>> assemblies, bool includePackageAssemblies)
        {
            var builder = new StringBuilder();
            var settings = new XmlWriterSettings { Indent = true, OmitXmlDeclaration = true };
            using (var writer = XmlWriter.Create(builder, settings))
            {
                writer.WriteStartElement("linker");
                if (includePackageAssemblies)
                {
                    foreach (var name in PackageAssemblies)
                    {
                        writer.WriteStartElement("assembly");
                        writer.WriteAttributeString("fullname", name);
                        writer.WriteAttributeString("preserve", "all");
                        writer.WriteEndElement();
                    }
                }

                foreach (var assembly in assemblies)
                {
                    writer.WriteStartElement("assembly");
                    writer.WriteAttributeString("fullname", assembly.Key);
                    foreach (var type in assembly.Value)
                    {
                        writer.WriteStartElement("type");
                        writer.WriteAttributeString("fullname", type.Key);
                        foreach (var field in type.Value.Fields)
                        {
                            writer.WriteStartElement("field");
                            writer.WriteAttributeString("name", field);
                            writer.WriteEndElement();
                        }

                        foreach (var method in type.Value.Methods)
                        {
                            writer.WriteStartElement("method");
                            writer.WriteAttributeString(method.Contains(" ") ? "signature" : "name", method);
                            writer.WriteEndElement();
                        }

                        writer.WriteEndElement();
                    }

                    writer.WriteEndElement();
                }

                writer.WriteEndElement();
            }

            return builder.ToString();
        }

        private static string TypeName(Type type) => type.FullName?.Replace('+', '/');

        private static string Signature(MethodInfo method)
        {
            var parameters = method.GetParameters();
            var names = new string[parameters.Length];
            for (var i = 0; i < parameters.Length; i++)
            {
                var type = parameters[i].ParameterType;
                if (type.IsGenericType || type.IsGenericParameter || type.ContainsGenericParameters || type.IsByRef)
                {
                    return method.Name;
                }

                names[i] = TypeName(type);
            }

            var returnType = method.ReturnType;
            if (returnType.IsGenericType || returnType.IsGenericParameter || returnType.IsByRef)
            {
                return method.Name;
            }

            return $"{TypeName(returnType)} {method.Name}({string.Join(",", names)})";
        }

        private sealed class TypeEntry
        {
            public SortedSet<string> Fields { get; } = new(StringComparer.Ordinal);

            public SortedSet<string> Methods { get; } = new(StringComparer.Ordinal);
        }
    }
}
