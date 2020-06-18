using System;

namespace Confluent.Kafka
{
    public readonly struct ConsumeResultMetadata
    {
        public ConsumeResultMetadata(bool hasResult, int partition, DateTime timestamp, long offset)
        {
            HasResult = hasResult;
            Partition = partition;
            Timestamp = timestamp;
            Offset = offset;
        }

        public readonly bool HasResult;
        public readonly int Partition;
        public readonly DateTime Timestamp;
        public readonly long Offset;
        
    }
}