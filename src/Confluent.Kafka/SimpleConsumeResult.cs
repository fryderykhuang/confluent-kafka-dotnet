using System;
using System.Collections.Generic;

namespace Confluent.Kafka
{
    public struct SimpleConsumeResult<TKey, TValue> : IEquatable<SimpleConsumeResult<TKey, TValue>>
    {
        public bool Equals(SimpleConsumeResult<TKey, TValue> other)
        {
            return EqualityComparer<TKey>.Default.Equals(Key, other.Key) && EqualityComparer<TValue>.Default.Equals(Value, other.Value) && Timestamp.Equals(other.Timestamp) && Partition == other.Partition && Offset == other.Offset;
        }

        public override bool Equals(object obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            return obj is SimpleConsumeResult<TKey, TValue> && Equals((SimpleConsumeResult<TKey, TValue>) obj);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                var hashCode = EqualityComparer<TKey>.Default.GetHashCode(Key);
                hashCode = (hashCode * 397) ^ EqualityComparer<TValue>.Default.GetHashCode(Value);
                hashCode = (hashCode * 397) ^ Timestamp.GetHashCode();
                hashCode = (hashCode * 397) ^ Partition;
                hashCode = (hashCode * 397) ^ Offset.GetHashCode();
                return hashCode;
            }
        }

        public static bool operator ==(SimpleConsumeResult<TKey, TValue> left, SimpleConsumeResult<TKey, TValue> right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(SimpleConsumeResult<TKey, TValue> left, SimpleConsumeResult<TKey, TValue> right)
        {
            return !left.Equals(right);
        }

        public TKey Key { get; set; }
        public TValue Value { get; set; }
        public DateTime Timestamp { get; set; }
        public int Partition { get; set; }
        public long Offset { get; set; }
        public Headers Headers { get; set; }
    }
}