using System.Collections.Generic;
using System.Runtime.Serialization;

namespace Confluent.Kafka.Statistics
{
    public class StatisticsMessage
    {
        [DataMember(Name = "ts")] public ulong Ts { get; set; }
        [DataMember(Name = "time")] public int Time { get; set; }
        [DataMember(Name = "replyq")] public int ReplyQ { get; set; }
        [DataMember(Name = "msg_cnt")] public int MsgCnt { get; set; }
        [DataMember(Name = "msg_size")] public long MsgSize { get; set; }
        [DataMember(Name = "msg_max")] public int MsgMax { get; set; }
        [DataMember(Name = "msg_size_max")] public long MsgSizeMax { get; set; }
        [DataMember(Name = "topics")] public Dictionary<string, TopicsStatistics> Topics { get; set; }
        [DataMember(Name = "brokers")] public Dictionary<string, BrokerStatistics> Brokers { get; set; }
    }
}
