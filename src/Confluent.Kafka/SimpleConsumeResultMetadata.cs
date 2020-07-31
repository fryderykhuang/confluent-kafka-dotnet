using System;
using System.Collections.Generic;

namespace Confluent.Kafka
{
    public struct SimpleConsumeResultMetadata
    {
        // public TKey Key;// { get; set; }
        // public TValue Value;// { get; set; }
        public DateTime Timestamp;// { get; set; }
        public int Partition;// { get; set; }
        public long Offset;// { get; set; }
        public Headers Headers;// { get; set; }
    }
}