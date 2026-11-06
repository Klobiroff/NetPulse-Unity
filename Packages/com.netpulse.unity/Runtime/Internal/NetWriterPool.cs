using System.Collections.Generic;
using NetPulse.Common;

namespace NetPulse
{
    internal static class NetWriterPool
    {
        private const int MaxPooled = 16;
        private const int MaxPooledLength = 16 * 1024;

        private static readonly Stack<NetWriter> Pool = new();

        public static NetWriter Rent()
        {
            if (Pool.Count == 0)
            {
                return new NetWriter(128);
            }

            var writer = Pool.Pop();
            writer.Reset();
            return writer;
        }

        public static void Return(NetWriter writer)
        {
            if (writer == null || Pool.Count >= MaxPooled || writer.Length > MaxPooledLength)
            {
                return;
            }

            writer.Reset();
            Pool.Push(writer);
        }
    }
}
