using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using System.Text;
using System.Threading.Tasks;

namespace Confluent.Kafka.Statistics
{
    public class PartitionStatistics
    {
        [DataMember(Name = "partition")] public int Partition { get; set; }
        [DataMember(Name = "leader")] public int Leader { get; set; }
        [DataMember(Name = "desired")] public bool Desired { get; set; }
        [DataMember(Name = "unknown")] public bool Unknown { get; set; }
        [DataMember(Name = "msgq_cnt")] public int MsgQCnt { get; set; }
        [DataMember(Name = "msgq_bytes")] public long MsgQBytes { get; set; }
        [DataMember(Name = "xmit_msgq_cnt")] public int XmitMsgQCnt { get; set; }
        [DataMember(Name = "xmit_msgq_bytes")] public long XmitMsgQBytes { get; set; }
        [DataMember(Name = "fetchq_cnt")] public int FetchQCnt { get; set; }
        [DataMember(Name = "fetchq_size")] public long FetchQSize { get; set; }
        [DataMember(Name = "fetch_state")] public string FetchState { get; set; }
        [DataMember(Name = "query_offset")] public long QueryOffset { get; set; }
        [DataMember(Name = "next_offset")] public long NextOffset { get; set; }
        [DataMember(Name = "app_offset")] public long AppOffset { get; set; }
        [DataMember(Name = "stored_offset")] public long StoredOffset { get; set; }
        [DataMember(Name = "committed_offset")] public long CommittedOffset { get; set; }
        [DataMember(Name = "eof_offset")] public long EofOffset { get; set; }
        [DataMember(Name = "lo_offset")] public long LoOffset { get; set; }
        [DataMember(Name = "hi_offset")] public long HiOffset { get; set; }
        [DataMember(Name = "consumer_lag")] public long ConsumerLag { get; set; }
        [DataMember(Name = "txmsgs")] public long TxMsgs { get; set; }
        [DataMember(Name = "txbytes")] public long TxBytes { get; set; }
        [DataMember(Name = "msgs")] public long Msgs { get; set; }
        [DataMember(Name = "rx_ver_drops")] public long RxVerDrops { get; set; }
    }
}
