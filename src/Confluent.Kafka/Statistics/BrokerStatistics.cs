using System.Runtime.Serialization;

namespace Confluent.Kafka.Statistics
{
    public class BrokerStatistics
    {
        [DataMember(Name = "name")] public string Name { get; set; }
        [DataMember(Name = "nodeid")] public int NodeId { get; set; }
        [DataMember(Name = "state")] public string State { get; set; }
        [DataMember(Name = "stateage")] public long StateAge { get; set; }
        [DataMember(Name = "tx")] public long Tx { get; set; }
        [DataMember(Name = "txbytes")] public long TxBytes { get; set; }
        [DataMember(Name = "txerrs")] public long TxErrs { get; set; }
        [DataMember(Name = "txretries")] public long TxRetries { get; set; }
        [DataMember(Name = "req_timeouts")] public long ReqTimeouts { get; set; }
        [DataMember(Name = "rx")] public long Rx { get; set; }
        [DataMember(Name = "rxbytes")] public long RxBytes { get; set; }
        [DataMember(Name = "rxerrs")] public long RxErrs { get; set; }
        [DataMember(Name = "rxcorriderrs")] public long RxCorridErrs { get; set; }
        [DataMember(Name = "rxpartial")] public long RxPartial { get; set; }
    }
}
