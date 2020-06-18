// Copyright 2018 Confluent Inc.
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
// http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
//
// Refer to LICENSE for more information.

using System;
using System.Text;


namespace Confluent.Kafka
{
    /// <summary>
    ///     Deserializers for use with <see cref="Consumer{TKey,TValue}" />.
    /// </summary>
    public static class Deserializers
    {
        /// <summary>
        ///     String (UTF8 encoded) deserializer.
        /// </summary>
        public static IDeserializer<string> Utf8 = new Utf8Deserializer();
        
        private class Utf8Deserializer : IDeserializer<string>
        {
            public string Deserialize(ReadOnlySpan<byte> data, bool isNull)
            {
                if (isNull)
                {
                    return null;
                }

                return Encoding.UTF8.GetString(data);
            }

            public void Deserialize(ReadOnlySpan<byte> data, bool isNull, out string result)
            {
                if (isNull)
                {
                    result = null;
                    return;
                }

                result = Encoding.UTF8.GetString(data);
            }
        }

        /// <summary>
        ///     Null value deserializer.
        /// </summary>
        public static IDeserializer<Null> Null = new NullDeserializer();

        private class NullDeserializer : IDeserializer<Null>
        {
            public Null Deserialize(ReadOnlySpan<byte> data, bool isNull)
            {
                if (!isNull)
                {
                    throw new ArgumentException("Deserializer<Null> may only be used to deserialize data that is null.");
                }

                return null;
            }

            public void Deserialize(ReadOnlySpan<byte> data, bool isNull, out Null result)
            {
                if (!isNull)
                {
                    throw new ArgumentException("Deserializer<Null> may only be used to deserialize data that is null.");
                }

                result = null;
            }
        }

        /// <summary>
        ///     Deserializer that deserializes any value to null.
        /// </summary>
        public static IDeserializer<Ignore> Ignore = new IgnoreDeserializer();

        private class IgnoreDeserializer : IDeserializer<Ignore>
        {
            public Ignore Deserialize(ReadOnlySpan<byte> data, bool isNull)
                => null;

            public void Deserialize(ReadOnlySpan<byte> data, bool isNull, out Ignore result)
            {
                result = null;
            }
        }

        /// <summary>
        ///     System.Int64 (big endian encoded, network byte ordered) deserializer.
        /// </summary>
        public static IDeserializer<long> Int64 = new Int64Deserializer();

        private class Int64Deserializer : IDeserializer<long>
        {
            public long Deserialize(ReadOnlySpan<byte> data, bool isNull)
            {
                if (isNull)
                {
                    throw new ArgumentNullException($"Null data encountered deserializing Int64 value.");
                }

                if (data.Length != 8)
                {
                    throw new ArgumentException($"Deserializer<Long> encountered data of length {data.Length}. Expecting data length to be 8.");
                }

                // network byte order -> big endian -> most significant byte in the smallest address.
                long result = ((long)data[0]) << 56 |
                    ((long)(data[1])) << 48 |
                    ((long)(data[2])) << 40 |
                    ((long)(data[3])) << 32 |
                    ((long)(data[4])) << 24 |
                    ((long)(data[5])) << 16 |
                    ((long)(data[6])) << 8 |
                    (data[7]);
                return result;
            }

            public void Deserialize(ReadOnlySpan<byte> data, bool isNull, out long result)
            {
                if (isNull)
                {
                    throw new ArgumentNullException($"Null data encountered deserializing Int64 value.");
                }

                if (data.Length != 8)
                {
                    throw new ArgumentException($"Deserializer<Long> encountered data of length {data.Length}. Expecting data length to be 8.");
                }

                // network byte order -> big endian -> most significant byte in the smallest address.
                result = ((long) data[0]) << 56 |
                         ((long) (data[1])) << 48 |
                         ((long) (data[2])) << 40 |
                         ((long) (data[3])) << 32 |
                         ((long) (data[4])) << 24 |
                         ((long) (data[5])) << 16 |
                         ((long) (data[6])) << 8 |
                         (data[7]);
            }
        }

        /// <summary>
        ///     System.Int32 (big endian encoded, network byte ordered) deserializer.
        /// </summary>
        public static IDeserializer<int> Int32 = new Int32Deserializer();

        private class Int32Deserializer : IDeserializer<int>
        {
            public int Deserialize(ReadOnlySpan<byte> data, bool isNull)
            {
                if (isNull)
                {
                    throw new ArgumentNullException($"Null data encountered deserializing Int32 value");
                }

                if (data.Length != 4)
                {
                    throw new ArgumentException($"Deserializer<Int32> encountered data of length {data.Length}. Expecting data length to be 4.");
                }

                // network byte order -> big endian -> most significant byte in the smallest address.
                return
                    (((int)data[0]) << 24) |
                    (((int)data[1]) << 16) |
                    (((int)data[2]) << 8) |
                    (int)data[3];
            }

            public void Deserialize(ReadOnlySpan<byte> data, bool isNull, out int result)
            {
                if (isNull)
                {
                    throw new ArgumentNullException($"Null data encountered deserializing Int32 value");
                }

                if (data.Length != 4)
                {
                    throw new ArgumentException($"Deserializer<Int32> encountered data of length {data.Length}. Expecting data length to be 4.");
                }

                // network byte order -> big endian -> most significant byte in the smallest address.
                result =
                    (((int)data[0]) << 24) |
                    (((int)data[1]) << 16) |
                    (((int)data[2]) << 8) |
                    (int)data[3];
            }
        }

        /// <summary>
        ///     System.Single (big endian encoded, network byte ordered) deserializer.
        /// </summary>
        public static IDeserializer<Single> Single = new SingleDeserializer();

        private class SingleDeserializer : IDeserializer<float>
        {
            public float Deserialize(ReadOnlySpan<byte> data, bool isNull)
            {
                if (isNull)
                {
                    throw new ArgumentNullException($"Null data encountered deserializing float value.");
                }

                if (data.Length != 4)
                {
                    throw new ArgumentException($"Deserializer<float> encountered data of length {data.Length}. Expecting data length to be 4.");
                }

                // network byte order -> big endian -> most significant byte in the smallest address.
                if (BitConverter.IsLittleEndian)
                {
                    unsafe
                    {
                        float result = default(float);
                        byte* p = (byte*) (&result);
                        *p++ = data[3];
                        *p++ = data[2];
                        *p++ = data[1];
                        *p = data[0];
                        return result;
                    }
                }
                else
                {
                    return BitConverter.ToSingle(data);
                }
            }

            public void Deserialize(ReadOnlySpan<byte> data, bool isNull, out float result)
            {
                if (isNull)
                {
                    throw new ArgumentNullException($"Null data encountered deserializing float value.");
                }

                if (data.Length != 4)
                {
                    throw new ArgumentException($"Deserializer<float> encountered data of length {data.Length}. Expecting data length to be 4.");
                }

                // network byte order -> big endian -> most significant byte in the smallest address.
                if (BitConverter.IsLittleEndian)
                {
                    unsafe
                    {
                        fixed (void* pp = &result)
                        {
                            var p = (byte*) pp;
                            *p = data[3];
                            *(p + 1) = data[2];
                            *(p + 2) = data[1];
                            *(p + 3) = data[0];
                        }
                    }
                }
                else
                {
                    result = BitConverter.ToSingle(data);
                }
            }
        }

        /// <summary>
        ///     System.Double (big endian encoded, network byte ordered) deserializer.
        /// </summary>
        public static IDeserializer<Double> Double = new DoubleDeserializer();

        private class DoubleDeserializer : IDeserializer<double>
        {
            public double Deserialize(ReadOnlySpan<byte> data, bool isNull)
            {
                if (isNull)
                {
                    throw new ArgumentNullException($"Null data encountered deserializing double value.");
                }

                if (data.Length != 8)
                {
                    throw new ArgumentException($"Deserializer<double> encountered data of length {data.Length}. Expecting data length to be 8.");
                }

                // network byte order -> big endian -> most significant byte in the smallest address.
                if (BitConverter.IsLittleEndian)
                {
                    unsafe
                    {
                        double result = default(double);
                        byte* p = (byte*)(&result);
                        *p++ = data[7];
                        *p++ = data[6];
                        *p++ = data[5];
                        *p++ = data[4];
                        *p++ = data[3];
                        *p++ = data[2];
                        *p++ = data[1];
                        *p++ = data[0];
                        return result;
                    }
                }
                else
                {
                    return BitConverter.ToDouble(data);
                }
            }

            public void Deserialize(ReadOnlySpan<byte> data, bool isNull, out double result)
            {
                if (isNull)
                {
                    throw new ArgumentNullException($"Null data encountered deserializing double value.");
                }

                if (data.Length != 8)
                {
                    throw new ArgumentException($"Deserializer<double> encountered data of length {data.Length}. Expecting data length to be 8.");
                }

                // network byte order -> big endian -> most significant byte in the smallest address.
                if (BitConverter.IsLittleEndian)
                {
                    unsafe
                    {
                        
                        // double result = default(double);
                        fixed (void* pp = &result)
                        {
                            var p = (byte*) pp;
                            *p = data[7];
                            *(p+1) = data[6];
                            *(p+2) = data[5];
                            *(p+3) = data[4];
                            *(p+4) = data[3];
                            *(p+5) = data[2];
                            *(p+6) = data[1];
                            *(p+7) = data[0];
                        }
                    }
                }
                else
                {
                    result = BitConverter.ToDouble(data);
                }
            }
        }

        /// <summary>
        ///     System.Byte[] (nullable) deserializer.
        /// </summary>
        /// <remarks>
        ///     Byte ordering is original order.
        /// </remarks>
        public static IDeserializer<byte[]> ByteArray = new ByteArrayDeserializer();

        private class ByteArrayDeserializer : IDeserializer<byte[]>
        {
            public byte[] Deserialize(ReadOnlySpan<byte> data, bool isNull)
            {
                if (isNull) { return null; }
                return data.ToArray();
            }

            public void Deserialize(ReadOnlySpan<byte> data, bool isNull, out byte[] result)
            {
                if (isNull)
                {
                    result = null; return; 
                }
                
                result = data.ToArray();
            }
        }
    }
}
