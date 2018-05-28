using System.Collections.Generic;
using System.Runtime.Serialization;

namespace Confluent.Kafka.Statistics
{
    public class TopicsStatistics
    {
        [DataMember(Name = "topic")] public string Topic { get; set; }
        [DataMember(Name = "metadata_age")] public int MetadataAge { get; set; }
        [DataMember(Name = "partitions")] public Dictionary<string, PartitionStatistics> Partitions { get; set; }
    }
}
