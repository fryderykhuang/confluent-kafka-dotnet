namespace Confluent.Kafka.Statistics
{
    public struct WindowStats
    {
        public int Min { get; set; }
        public int Max { get; set; }
        public int Avg { get; set; }
        public int Sum { get; set; }
        public int Cnt { get; set; }
    }
}
