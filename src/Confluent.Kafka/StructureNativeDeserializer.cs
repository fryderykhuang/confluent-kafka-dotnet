using System;
using System.Runtime.CompilerServices;

namespace Confluent.Kafka
{
    public class StructureNativeDeserializer<T> : INativeDeserializer<T> where T : struct
    {
        public unsafe T Deserialize(IntPtr msgBuf, uint msgLen, IntPtr msgTopic)
        {
            return Unsafe.Read<T>(msgBuf.ToPointer());
        }
    }
}