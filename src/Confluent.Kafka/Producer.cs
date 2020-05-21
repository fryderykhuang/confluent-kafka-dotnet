// Copyright 2016-2018 Confluent Inc.
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
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Confluent.Kafka.Impl;
using Confluent.Kafka.Internal;


namespace Confluent.Kafka
{
 
    /// <summary>
    ///     A high level producer with serialization capability.
    /// </summary>
    internal class Producer<TKey, TValue> : IProducer<TKey, TValue>, IClient
    {
        internal class Config
        {
            public IEnumerable<KeyValuePair<string, string>> config;
            public Action<Error> errorHandler;
            public Action<LogMessage> logHandler;
            public Action<string> statisticsHandler;
            internal InternalDeliveryReportReceivedDelegate deliveryReportReceivedHandler;
        }

        private ISerializer<TKey> keySerializer;
        private ISerializer<TValue> valueSerializer;
        private IAsyncSerializer<TKey> asyncKeySerializer;
        private IAsyncSerializer<TValue> asyncValueSerializer;

        private static readonly Dictionary<Type, object> defaultSerializers = new Dictionary<Type, object>
        {
            { typeof(Null), Serializers.Null },
            { typeof(int), Serializers.Int32 },
            { typeof(long), Serializers.Int64 },
            { typeof(string), Serializers.Utf8 },
            { typeof(float), Serializers.Single },
            { typeof(double), Serializers.Double },
            { typeof(byte[]), Serializers.ByteArray }
        };

        private int cancellationDelayMaxMs;
        private bool disposeHasBeenCalled = false;
        private object disposeHasBeenCalledLockObj = new object();

        private bool manualPoll = false;
        private bool enableDeliveryReports = true;
        private bool enableDeliveryReportKey = true;
        private bool enableDeliveryReportValue = true;
        private bool enableDeliveryReportTimestamp = true;
        private bool enableDeliveryReportHeaders = true;
        private bool enableDeliveryReportPersistedStatus = true;

        private SafeKafkaHandle ownedKafkaHandle;
        private Handle borrowedHandle;

        private SafeKafkaHandle KafkaHandle
            => ownedKafkaHandle != null 
                ? ownedKafkaHandle
                : borrowedHandle.LibrdkafkaHandle;

        private Task callbackTask;
        private CancellationTokenSource callbackCts;

        private int eventsServedCount = 0;
        private object pollSyncObj = new object();

        private Task StartPollTask(CancellationToken ct)
            => Task.Factory.StartNew(() =>
                {
                    try
                    {
                        while (true)
                        {
                            ct.ThrowIfCancellationRequested();
                            int eventsServedCount_ = ownedKafkaHandle.Poll((IntPtr)cancellationDelayMaxMs);
                            if (this.handlerException != null)
                            {
                                errorHandler?.Invoke(new Error(ErrorCode.Local_Application, handlerException.ToString()));
                                this.handlerException = null;
                            }

                            // note: lock {} is equivalent to Monitor.Enter then Monitor.Exit 
                            if (eventsServedCount_ > 0)
                            {
                                lock (pollSyncObj)
                                {
                                    this.eventsServedCount += eventsServedCount_;
                                    Monitor.Pulse(pollSyncObj);
                                }
                            }
                        }
                    }
                    catch (OperationCanceledException) {}
                }, ct, TaskCreationOptions.LongRunning, TaskScheduler.Default);


        // .NET Exceptions are not propagated through native code, so we need to
        // do this book keeping explicitly.
        private Exception handlerException = null;


        private Action<Error> errorHandler;
        private Librdkafka.ErrorDelegate errorCallbackDelegate;
        private void ErrorCallback(IntPtr rk, ErrorCode err, string reason, IntPtr opaque)
        {
            // Ensure registered handlers are never called as a side-effect of Dispose/Finalize (prevents deadlocks in common scenarios).
            if (ownedKafkaHandle.IsClosed) { return; }

            try
            {
            errorHandler?.Invoke(KafkaHandle.CreatePossiblyFatalError(err, reason));
        }
            catch (Exception)
            {
                // Eat any exception thrown by user log handler code.
            }
        }


        private Action<string> statisticsHandler;
        private Librdkafka.StatsDelegate statisticsCallbackDelegate;
        private int StatisticsCallback(IntPtr rk, IntPtr json, UIntPtr json_len, IntPtr opaque)
        {
            // Ensure registered handlers are never called as a side-effect of Dispose/Finalize (prevents deadlocks in common scenarios).
            if (ownedKafkaHandle.IsClosed) { return 0; }

            try
            {
            statisticsHandler?.Invoke(Util.Marshal.PtrToStringUTF8(json));
            }
            catch (Exception e)
            {
                handlerException = e;
            }

            return 0; // instruct librdkafka to immediately free the json ptr.
        }


        private Action<LogMessage> logHandler;
        private object loggerLockObj = new object();
        private Librdkafka.LogDelegate logCallbackDelegate;
        private void LogCallback(IntPtr rk, SyslogLevel level, string fac, string buf)
        {
            // Ensure registered handlers are never called as a side-effect of Dispose/Finalize (prevents deadlocks in common scenarios).
            // Note: kafkaHandle can be null if the callback is during construction (in that case, we want the delegate to run).
            if (ownedKafkaHandle != null && ownedKafkaHandle.IsClosed) { return; }
            try
            {
            logHandler?.Invoke(new LogMessage(Util.Marshal.PtrToStringUTF8(Librdkafka.name(rk)), level, fac, buf));
        }
            catch (Exception)
            {
                // Eat any exception thrown by user log handler code.
            }
        }

        private Librdkafka.DeliveryReportDelegate DeliveryReportCallback;

        private bool _deliveryReportAsUserState;
        private InternalDeliveryReportReceivedDelegate deliveryReportReceivedHandler;

        private static readonly int SizeOfKafkaMsg = Marshal.SizeOf(typeof(rd_kafka_message));

        private void DeliveryReportCallbackImpl(IntPtr rk, IntPtr rkmessage, IntPtr opaque)
        {
            // Ensure registered handlers are never called as a side-effect of Dispose/Finalize (prevents deadlocks in common scenarios).
            if (ownedKafkaHandle.IsClosed) { return; }

            try
            {
//            var msg = Util.Marshal.PtrToStructure<rd_kafka_message>(rkmessage);
                unsafe
                {
                    var msg = MemoryMarshal.Read<rd_kafka_message>(new ReadOnlySpan<byte>(rkmessage.ToPointer(),
                        SizeOfKafkaMsg));

                    deliveryReportReceivedHandler?.Invoke(ref msg);

                    if (_deliveryReportAsUserState)
                    {
                        return;
                    }

                    // the msg._private property has dual purpose. Here, it is an opaque pointer set
                    // by Topic.Produce to be an IDeliveryHandler. When Consuming, it's for internal
                    // use (hence the name).
                    if (msg._private == IntPtr.Zero)
                    {
                        // Note: this can occur if the ProduceAsync overload that accepts a DeliveryHandler
                        // was used and the delivery handler was set to null.
                        return;
                    }

                    var gch = GCHandle.FromIntPtr(msg._private);
                    var deliveryHandler = (IDeliveryHandler) gch.Target;
                    gch.Free();

                    Headers headers = null;
                    if (this.enableDeliveryReportHeaders)
                    {
                        headers = new Headers();
                        Librdkafka.message_headers(rkmessage, out IntPtr hdrsPtr);
                        if (hdrsPtr != IntPtr.Zero)
                        {
                            for (var i = 0;; ++i)
                            {
                                var err = Librdkafka.header_get_all(hdrsPtr, (IntPtr) i, out IntPtr namep,
                                    out IntPtr valuep, out IntPtr sizep);
                                if (err != ErrorCode.NoError)
                                {
                                    break;
                                }

                                var headerName = Util.Marshal.PtrToStringUTF8(namep);
                                byte[] headerValue = null;
                                if (valuep != IntPtr.Zero)
                                {
                                    headerValue = new byte[(int) sizep];
                                    Marshal.Copy(valuep, headerValue, 0, (int) sizep);
                                }

                                headers.Add(headerName, headerValue);
                            }
                        }
                    }

                    IntPtr timestampType = (IntPtr) TimestampType.NotAvailable;
                    long timestamp = 0;
                    if (enableDeliveryReportTimestamp)
                    {
                        timestamp = Librdkafka.message_timestamp(rkmessage, out timestampType);
                    }

                    PersistenceStatus messageStatus = PersistenceStatus.PossiblyPersisted;
                    if (enableDeliveryReportPersistedStatus)
                    {
                        messageStatus = Librdkafka.message_status(rkmessage);
                    }

                    deliveryHandler.HandleDeliveryReport(
                        new DeliveryReport<Null, Null>
                        {
                            // Topic is not set here in order to avoid the marshalling cost.
                            // Instead, the delivery handler is expected to cache the topic string.
                            Partition = msg.partition,
                            Offset = msg.offset,
                            Error = KafkaHandle.CreatePossiblyFatalError(msg.err, null),
                            Status = messageStatus,
                            Message = new Message<Null, Null>
                                {Timestamp = new Timestamp(timestamp, (TimestampType) timestampType), Headers = headers}
                        }
                    );
                }
            }
            catch (Exception e)
            {
                handlerException = e;
            }
        }

        private void ProduceImpl(
            string topic,
            ReadOnlySpan<byte> val,
            ReadOnlySpan<byte> key,
            Timestamp timestamp,
            Partition partition,
            IEnumerable<IHeader> headers,
            IDeliveryHandler deliveryHandler)
        {
            if (timestamp.Type != TimestampType.CreateTime)
            {
                if (timestamp != Timestamp.Default)
                {
                    throw new ArgumentException("Timestamp must be either Timestamp.Default, or Timestamp.CreateTime.");
                }
            }

            ErrorCode err;
            if (this.enableDeliveryReports && deliveryHandler != null)
            {
                // Passes the TaskCompletionSource to the delivery report callback via the msg_opaque pointer

                // Note: There is a level of indirection between the GCHandle and
                // physical memory address. GCHandle.ToIntPtr doesn't get the
                // physical address, it gets an id that refers to the object via
                // a handle-table.
                var gch = GCHandle.Alloc(deliveryHandler);
                var ptr = GCHandle.ToIntPtr(gch);

                err = KafkaHandle.Produce(
                    topic,
                    val,
                    key,
                    partition.Value,
                    timestamp.UnixTimestampMs,
                    headers,
                    ptr);

                if (err != ErrorCode.NoError)
                {
                    // note: freed in the delivery handler callback otherwise.
                    gch.Free();
                }
            }
            else
            {
                err = KafkaHandle.Produce(
                    topic,
                    val,
                    key,
                    partition.Value,
                    timestamp.UnixTimestampMs,
                    headers,
                    IntPtr.Zero);
            }

            if (err != ErrorCode.NoError)
            {
                throw new KafkaException(KafkaHandle.CreatePossiblyFatalError(err, null));
            }
        }

        private void ProduceImpl(
            string topic,
            ReadOnlySpan<byte> val,
            ReadOnlySpan<byte> key,
            Timestamp timestamp,
            Partition partition,
            IEnumerable<IHeader> headers,
            IntPtr deliveryHandler)
        {
            if (timestamp.Type != TimestampType.CreateTime)
            {
                if (timestamp != Timestamp.Default)
                {
                    throw new ArgumentException("Timestamp must be either Timestamp.Default, or Timestamp.CreateTime.");
                }
            }

            ErrorCode err = ErrorCode.NoError;
            if (this.enableDeliveryReports)
            {
                err = KafkaHandle.Produce(
                    topic,
                    val,
                    key,
                    partition.Value,
                    timestamp.UnixTimestampMs,
                    headers,
                    deliveryHandler);
            }

            if (err != ErrorCode.NoError)
            {
                throw new KafkaException(KafkaHandle.CreatePossiblyFatalError(err, null));
            }
        }



        /// <summary>
        ///     Refer to <see cref="Confluent.Kafka.IProducer{TKey,TValue}.Poll(TimeSpan)" />
        /// </summary>
        public int Poll(TimeSpan timeout)
        {
            if (manualPoll)
            {
                return this.KafkaHandle.Poll((IntPtr)timeout.TotalMillisecondsAsInt());
            }

            lock (pollSyncObj)
            {
                if (eventsServedCount == 0)
                {
                    Monitor.Wait(pollSyncObj, timeout);
                }

                var result = eventsServedCount;
                eventsServedCount = 0;
                return result;
            }
        }

        public int Poll(int timeoutMs)
        {
            if (manualPoll)
            {
                return this.KafkaHandle.Poll((IntPtr)timeoutMs);
            }

            lock (pollSyncObj)
            {
                if (eventsServedCount == 0)
                {
                    Monitor.Wait(pollSyncObj, timeoutMs);
                }

                var result = eventsServedCount;
                eventsServedCount = 0;
                return result;
            }
        }


        /// <inheritdoc/>
        public int Flush(TimeSpan timeout)
        {
            var result = KafkaHandle.Flush(timeout.TotalMillisecondsAsInt());
            if (this.handlerException != null)
            {
                errorHandler?.Invoke(new Error(ErrorCode.Local_Application, handlerException.ToString()));
                var ex = this.handlerException;
                this.handlerException = null;
            }
            return result;
        }


        /// <inheritdoc/>
        public void Flush(CancellationToken cancellationToken)
        {
            while (true)
            {
                int result = KafkaHandle.Flush(100);
                if (this.handlerException != null)
                {
                    errorHandler?.Invoke(new Error(ErrorCode.Local_Application, handlerException.ToString()));
                    var ex = this.handlerException;
                    this.handlerException = null;
                }

                if (result == 0)
                {
                    return;
                }
                if (cancellationToken.IsCancellationRequested)
                {
                    // TODO: include flush number in exception.
                    throw new OperationCanceledException();
                }
            }
        }

        
        /// <inheritdoc/>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }


        /// <summary>
        ///     Releases the unmanaged resources used by the
        ///     <see cref="Producer{TKey,TValue}" />
        ///     and optionally disposes the managed resources.
        /// </summary>
        /// <param name="disposing">
        ///     true to release both managed and unmanaged resources;
        ///     false to release only unmanaged resources.
        /// </param>
        protected virtual void Dispose(bool disposing)
        {
            // Calling Dispose a second or subsequent time should be a no-op.
            lock (disposeHasBeenCalledLockObj)
            { 
                if (disposeHasBeenCalled) { return; }
                disposeHasBeenCalled = true;
            }

            // do nothing if we borrowed a handle.
            if (ownedKafkaHandle == null) { return; }

            if (disposing)
            {
                if (!this.manualPoll)
                {
                    callbackCts.Cancel();
                    try
                    {
                        // Note: It's necessary to wait on callbackTask before disposing kafkaHandle
                        // since the poll loop makes use of this.
                        callbackTask.Wait();
                    }
                    catch (AggregateException e)
                    {
                        if (e.InnerException.GetType() != typeof(TaskCanceledException))
                        {
                            throw e.InnerException;
                        }
                    }
                    finally
                    {
                        callbackCts.Dispose();
                    }
                }

                // calls to rd_kafka_destroy may result in callbacks
                // as a side-effect. however the callbacks this class
                // registers with librdkafka ensure that any registered
                // events are not called if kafkaHandle has been closed.
                // this avoids deadlocks in common scenarios.
                ownedKafkaHandle.Dispose();
            }
        }


        /// <inheritdoc/>
        public string Name
            => KafkaHandle.Name;


        /// <inheritdoc/>
        public int AddBrokers(string brokers)
            => KafkaHandle.AddBrokers(brokers);


        /// <inheritdoc/>
        public Handle Handle 
        {
            get
            {
                if (this.ownedKafkaHandle != null)
                {
                    return new Handle { Owner = this, LibrdkafkaHandle = ownedKafkaHandle };
                }
                
                return borrowedHandle;
            }
        }

        private void InitializeSerializers(
            ISerializer<TKey> keySerializer,
            ISerializer<TValue> valueSerializer,
            IAsyncSerializer<TKey> asyncKeySerializer,
            IAsyncSerializer<TValue> asyncValueSerializer)
        {
            // setup key serializer.
            if (keySerializer == null && asyncKeySerializer == null)
            {
                if (!defaultSerializers.TryGetValue(typeof(TKey), out object serializer))
                {
                    throw new ArgumentNullException(
                        $"Key serializer not specified and there is no default serializer defined for type {typeof(TKey).Name}.");
                }
                this.keySerializer = (ISerializer<TKey>)serializer;
            }
            else if (keySerializer == null && asyncKeySerializer != null)
            {
                this.asyncKeySerializer = asyncKeySerializer;
            }
            else if (keySerializer != null && asyncKeySerializer == null)
            {
                this.keySerializer = keySerializer;
            }
            else
            {
                throw new InvalidOperationException("FATAL: Both async and sync key serializers were set.");
            }

            // setup value serializer.
            if (valueSerializer == null && asyncValueSerializer == null)
            {
                if (!defaultSerializers.TryGetValue(typeof(TValue), out object serializer))
                {
                    throw new ArgumentNullException(
                        $"Value serializer not specified and there is no default serializer defined for type {typeof(TValue).Name}.");
                }
                this.valueSerializer = (ISerializer<TValue>)serializer;
            }
            else if (valueSerializer == null && asyncValueSerializer != null)
            {
                this.asyncValueSerializer = asyncValueSerializer;
            }
            else if (valueSerializer != null && asyncValueSerializer == null)
            {
                this.valueSerializer = valueSerializer;
            }
            else
            {
                throw new InvalidOperationException("FATAL: Both async and sync value serializers were set.");
            }
        }

        internal Producer(DependentProducerBuilder<TKey, TValue> builder)
        {
            this.borrowedHandle = builder.Handle;

            if (!borrowedHandle.Owner.GetType().Name.Contains("Producer")) // much simpler than checking actual types.
            {
                throw new Exception("A Producer instance may only be constructed using the handle of another Producer instance.");
            }

            InitializeSerializers(
                builder.KeySerializer, builder.ValueSerializer,
                builder.AsyncKeySerializer, builder.AsyncValueSerializer);
        }

        internal Producer(ProducerBuilder<TKey, TValue> builder)
        {
            var baseConfig = builder.ConstructBaseConfig(this);

            // TODO: Make Tasks auto complete when EnableDeliveryReportsPropertyName is set to false.
            // TODO: Hijack the "delivery.report.only.error" configuration parameter and add functionality to enforce that Tasks 
            //       that never complete are never created when this is set to true.

            this.statisticsHandler = baseConfig.statisticsHandler;
            this.logHandler = baseConfig.logHandler;
            this.errorHandler = baseConfig.errorHandler;
            this.deliveryReportReceivedHandler = baseConfig.deliveryReportReceivedHandler;

            var config = Confluent.Kafka.Config.ExtractCancellationDelayMaxMs(baseConfig.config, out this.cancellationDelayMaxMs);

            this.DeliveryReportCallback = DeliveryReportCallbackImpl;

            Librdkafka.Initialize(null);

            var modifiedConfig = Library.NameAndVersionConfig
                .Concat(config
                .Where(prop => 
                    prop.Key != ConfigPropertyNames.Producer.EnableBackgroundPoll &&
                    prop.Key != ConfigPropertyNames.Producer.EnableDeliveryReports &&
                    prop.Key != ConfigPropertyNames.Producer.DeliveryReportFields && 
                    prop.Key != ConfigPropertyNames.Producer.DeliveryReportAsUserState)
                .ToList());

            if (modifiedConfig.Where(obj => obj.Key == "delivery.report.only.error").Count() > 0)
            {
                // A managed object is kept alive over the duration of the produce request. If there is no
                // delivery report generated, there will be a memory leak. We could possibly support this 
                // property by keeping track of delivery reports in managed code, but this seems like 
                // more trouble than it's worth.
                throw new ArgumentException("The 'delivery.report.only.error' property is not supported by this client");
            }

            var enableBackgroundPollObj = config.FirstOrDefault(prop => prop.Key == ConfigPropertyNames.Producer.EnableBackgroundPoll).Value;
            if (enableBackgroundPollObj != null)
            {
                this.manualPoll = !bool.Parse(enableBackgroundPollObj);
            }

            var enableDeliveryReportsObj = config.FirstOrDefault(prop => prop.Key == ConfigPropertyNames.Producer.EnableDeliveryReports).Value;
            if (enableDeliveryReportsObj != null)
            {
                this.enableDeliveryReports = bool.Parse(enableDeliveryReportsObj);
            }

            var deliveryReportEnabledFieldsObj = config.FirstOrDefault(prop => prop.Key == ConfigPropertyNames.Producer.DeliveryReportFields).Value;
            if (deliveryReportEnabledFieldsObj != null)
            {
                var fields = deliveryReportEnabledFieldsObj.Replace(" ", "");
                if (fields != "all")
                {
                    this.enableDeliveryReportKey = false;
                    this.enableDeliveryReportValue = false;
                    this.enableDeliveryReportHeaders = false;
                    this.enableDeliveryReportTimestamp = false;
                    this.enableDeliveryReportPersistedStatus = false;
                    if (fields != "none")
                    {
                        var parts = fields.Split(',');
                        foreach (var part in parts)
                        {
                            switch (part)
                            {
                                case "key": this.enableDeliveryReportKey = true; break;
                                case "value": this.enableDeliveryReportValue = true; break;
                                case "timestamp": this.enableDeliveryReportTimestamp = true; break;
                                case "headers": this.enableDeliveryReportHeaders = true; break;
                                case "status": this.enableDeliveryReportPersistedStatus = true; break;
                                default: throw new ArgumentException(
                                    $"Unknown delivery report field name '{part}' in config value '{ConfigPropertyNames.Producer.DeliveryReportFields}'.");
                            }
                        }
                    }
                }
            }

            var deliveryReportAsUserStateObj = config.FirstOrDefault(prop => prop.Key == ConfigPropertyNames.Producer.DeliveryReportAsUserState).Value;
            if (deliveryReportAsUserStateObj != null)
            {
                _deliveryReportAsUserState = Convert.ToBoolean(deliveryReportAsUserStateObj);
            }

            var configHandle = SafeConfigHandle.Create();

            foreach (var kvp in modifiedConfig)
            {
                if (kvp.Value == null)
                {
                    throw new ArgumentNullException($"'{kvp.Key}' configuration parameter must not be null.");
                }

                configHandle.Set(kvp.Key, kvp.Value);
            }


            IntPtr configPtr = configHandle.DangerousGetHandle();

            if (enableDeliveryReports)
            {
                Librdkafka.conf_set_dr_msg_cb(configPtr, DeliveryReportCallback);
            }

            // Explicitly keep references to delegates so they are not reclaimed by the GC.
            errorCallbackDelegate = ErrorCallback;
            logCallbackDelegate = LogCallback;
            statisticsCallbackDelegate = StatisticsCallback;

            if (errorHandler != null)
            {
            Librdkafka.conf_set_error_cb(configPtr, errorCallbackDelegate);
            }
            if (logHandler != null)
            {
            Librdkafka.conf_set_log_cb(configPtr, logCallbackDelegate);
            }
            if (statisticsHandler != null)
            {
            Librdkafka.conf_set_stats_cb(configPtr, statisticsCallbackDelegate);
            }

            this.ownedKafkaHandle = SafeKafkaHandle.Create(RdKafkaType.Producer, configPtr, this);
            configHandle.SetHandleAsInvalid(); // config object is no longer usable.

            if (!manualPoll)
            {
                callbackCts = new CancellationTokenSource();
                callbackTask = StartPollTask(callbackCts.Token);
            }

            InitializeSerializers(
                builder.KeySerializer, builder.ValueSerializer,
                builder.AsyncKeySerializer, builder.AsyncValueSerializer);
        }


        /// <inheritdoc/>
        public async Task<DeliveryResult<TKey, TValue>> ProduceAsyncUsingAsyncSerializer(
            TopicPartition topicPartition,
            Message<TKey, TValue> message,
            CancellationToken cancellationToken)
        {
            Headers headers = message.Headers ?? new Headers();

            byte[] keyBytes;
            try
            {
                keyBytes = 
                    // (keySerializer != null)
                    // ? keySerializer.Serialize(message.Key, new SerializationContext(MessageComponentType.Key, topicPartition.Topic, headers))
                    // :
                    await asyncKeySerializer.SerializeAsync(message.Key, new SerializationContext(MessageComponentType.Key, topicPartition.Topic, headers)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                throw new ProduceException<TKey, TValue>(
                    new Error(ErrorCode.Local_KeySerialization),
                    new DeliveryResult<TKey, TValue>
                    {
                        Message = message,
                        TopicPartitionOffset = new TopicPartitionOffset(topicPartition, Offset.Unset)
                    },
                    ex);
            }

            byte[] valBytes;
            try
            {
                valBytes = 
                    // (valueSerializer != null)
                    // ? valueSerializer.Serialize(message.Value, new SerializationContext(MessageComponentType.Value, topicPartition.Topic, headers))
                    // : 
                    await asyncValueSerializer.SerializeAsync(message.Value, new SerializationContext(MessageComponentType.Value, topicPartition.Topic, headers)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                throw new ProduceException<TKey, TValue>(
                    new Error(ErrorCode.Local_ValueSerialization),
                    new DeliveryResult<TKey, TValue>
                    {
                        Message = message,
                        TopicPartitionOffset = new TopicPartitionOffset(topicPartition, Offset.Unset)
                    },
                    ex);
            }

            try
            {
                if (enableDeliveryReports)
                {
                    var handler = new TypedTaskDeliveryHandlerShim(
                        topicPartition.Topic,
                        enableDeliveryReportKey ? message.Key : default(TKey),
                        enableDeliveryReportValue ? message.Value : default(TValue));

                    ProduceImpl(
                        topicPartition.Topic,
                        valBytes,
                        keyBytes,
                        message.Timestamp, topicPartition.Partition, headers,
                        handler);

                    if (cancellationToken != null && cancellationToken.CanBeCanceled)
                    {
                        cancellationToken.Register(() => handler.TrySetCanceled());
                    }

                    return await handler.Task.ConfigureAwait(false);
                }
                else
                {
                    ProduceImpl(
                        topicPartition.Topic,
                        valBytes,
                        keyBytes,
                        message.Timestamp, topicPartition.Partition, headers, 
                        null);

                    var result = new DeliveryResult<TKey, TValue>
                    {
                        TopicPartitionOffset = new TopicPartitionOffset(topicPartition, Offset.Unset),
                        Message = message
                    };

                    return result;
                }
            }
            catch (KafkaException ex)
            {
                throw new ProduceException<TKey, TValue>(
                    ex.Error,
                    new DeliveryResult<TKey, TValue>
                    {
                        Message = message,
                        TopicPartitionOffset = new TopicPartitionOffset(topicPartition, Offset.Unset)
                    });
            }
        }

        public Task<DeliveryResult<TKey, TValue>> ProduceAsync(
            TopicPartition topicPartition,
            Message<TKey, TValue> message) =>
            ProduceAsync(topicPartition, message, CancellationToken.None);

        public Task<DeliveryResult<TKey, TValue>> ProduceAsync(
            TopicPartition topicPartition,
            Message<TKey, TValue> message,
            CancellationToken cancellationToken)
        {
            ReadOnlySpan<byte> keyBytes;
            try
            {
                if (_keyScratchBuffer == null)
                {
                    _keyScratchBuffer = new byte[_defaultKeyScratchBufferSize];
                }
                keyBytes = keySerializer.Serialize(message.Key, new SerializationContext(MessageComponentType.Key, topicPartition.Topic, message.Headers), _keyScratchBuffer);
            }
            catch (Exception ex)
            {
                throw new ProduceException<TKey, TValue>(
                    new Error(ErrorCode.Local_KeySerialization),
                    new DeliveryResult<TKey, TValue>
                    {
                        Message = message,
                        TopicPartitionOffset = new TopicPartitionOffset(topicPartition, Offset.Unset)
                    },
                    ex);
            }

            ReadOnlySpan<byte> valBytes;
            try
            {
                if (_valueScratchBuffer == null)
                {
                    _valueScratchBuffer = new byte[_defaultValueScratchBufferSize];
                }
                valBytes = valueSerializer.Serialize(message.Value, new SerializationContext(MessageComponentType.Value, topicPartition.Topic, message.Headers), _valueScratchBuffer);
            }
            catch (Exception ex)
            {
                throw new ProduceException<TKey, TValue>(
                    new Error(ErrorCode.Local_ValueSerialization),
                    new DeliveryResult<TKey, TValue>
                    {
                        Message = message,
                        TopicPartitionOffset = new TopicPartitionOffset(topicPartition, Offset.Unset)
                    },
                    ex);
            }

            try
            {
                if (enableDeliveryReports)
                {
                    var handler = new TypedTaskDeliveryHandlerShim(
                        topicPartition.Topic,
                        enableDeliveryReportKey ? message.Key : default(TKey),
                        enableDeliveryReportValue ? message.Value : default(TValue));

                    ProduceImpl(
                        topicPartition.Topic,
                        valBytes,
                        keyBytes,
                        message.Timestamp, topicPartition.Partition, message.Headers,
                        handler);

                    if (cancellationToken.CanBeCanceled)
                    {
                        cancellationToken.Register(() => handler.TrySetCanceled());
                    }
                    
                    return handler.Task;
                }
                else
                {
                    ProduceImpl(
                        topicPartition.Topic,
                        valBytes,
                        keyBytes,
                        message.Timestamp, topicPartition.Partition, message.Headers,
                        null);

                    var result = new DeliveryResult<TKey, TValue>
                    {
                        TopicPartitionOffset = new TopicPartitionOffset(topicPartition, Offset.Unset),
                        Message = message
                    };

                    return Task.FromResult(result);
                }
            }
            catch (KafkaException ex)
            {
                throw new ProduceException<TKey, TValue>(
                    ex.Error,
                    new DeliveryResult<TKey, TValue>
                    {
                        Message = message,
                        TopicPartitionOffset = new TopicPartitionOffset(topicPartition, Offset.Unset)
                    });
            }
        }
        
        
        public Task<DeliveryResult<TKey, TValue>> ProduceAsync(
            string topic,
            int partition,
            Message<TKey, TValue> message,
            CancellationToken cancellationToken)
        {
            ReadOnlySpan<byte> keyBytes;
            try
            {
                if (_keyScratchBuffer == null)
                {
                    _keyScratchBuffer = new byte[_defaultKeyScratchBufferSize];
                }
                keyBytes = keySerializer.Serialize(message.Key, new SerializationContext(MessageComponentType.Key, topic, message.Headers), _keyScratchBuffer);
            }
            catch (Exception ex)
            {
                throw new ProduceException<TKey, TValue>(
                    new Error(ErrorCode.Local_KeySerialization),
                    new DeliveryResult<TKey, TValue>
                    {
                        Message = message,
                        TopicPartitionOffset = new TopicPartitionOffset(topic, partition, Offset.Unset)
                    },
                    ex);
            }

            ReadOnlySpan<byte> valBytes;
            try
            {
                if (_valueScratchBuffer == null)
                {
                    _valueScratchBuffer = new byte[_defaultValueScratchBufferSize];
                }
                valBytes = valueSerializer.Serialize(message.Value, new SerializationContext(MessageComponentType.Value, topic, message.Headers), _valueScratchBuffer);
            }
            catch (Exception ex)
            {
                throw new ProduceException<TKey, TValue>(
                    new Error(ErrorCode.Local_ValueSerialization),
                    new DeliveryResult<TKey, TValue>
                    {
                        Message = message,
                        TopicPartitionOffset = new TopicPartitionOffset(topic, partition, Offset.Unset)
                    },
                    ex);
            }

            try
            {
                if (enableDeliveryReports)
                {
                    var handler = new TypedTaskDeliveryHandlerShim(
                        topic,
                        enableDeliveryReportKey ? message.Key : default(TKey),
                        enableDeliveryReportValue ? message.Value : default(TValue));

                    ProduceImpl(
                        topic,
                        valBytes,
                        keyBytes,
                        message.Timestamp, partition, message.Headers,
                        handler);
                    
                    if (cancellationToken.CanBeCanceled)
                    {
                        cancellationToken.Register(() => handler.TrySetCanceled());
                    }                    

                    return handler.Task;
                }
                else
                {
                    ProduceImpl(
                        topic,
                        valBytes,
                        keyBytes,
                        message.Timestamp, partition, message.Headers,
                        null);

                    var result = new DeliveryResult<TKey, TValue>
                    {
                        TopicPartitionOffset = new TopicPartitionOffset(topic, partition, Offset.Unset),
                        Message = message
                    };

                    return Task.FromResult(result);
                }
            }
            catch (KafkaException ex)
            {
                throw new ProduceException<TKey, TValue>(
                    ex.Error,
                    new DeliveryResult<TKey, TValue>
                    {
                        Message = message,
                        TopicPartitionOffset = new TopicPartitionOffset(topic, partition, Offset.Unset)
                    });
            }
        }


        /// <summary>
        ///     Refer to <see cref="Confluent.Kafka.IProducer{TKey,TValue}.ProduceAsync(string, Message{TKey, TValue})" />
        /// </summary>
        public Task<DeliveryResult<TKey, TValue>> ProduceAsync(
            string topic,
            Message<TKey, TValue> message,
            CancellationToken cancellationToken)
            => ProduceAsync(new TopicPartition(topic, Partition.Any), message, cancellationToken);


        /// <inheritdoc/>
        public void Produce(
            string topic,
            Message<TKey, TValue> message,
            Action<DeliveryReport<TKey, TValue>> deliveryHandler = null
        )
            => Produce(new TopicPartition(topic, Partition.Any), message, deliveryHandler);


        /// <inheritdoc/>
        public void Produce(
            TopicPartition topicPartition,
            Message<TKey, TValue> message,
            Action<DeliveryReport<TKey, TValue>> deliveryHandler = null)
        {
            if (deliveryHandler != null && !enableDeliveryReports)
            {
                throw new InvalidOperationException("A delivery handler was specified, but delivery reports are disabled.");
            }

            Headers headers = message.Headers ?? new Headers();
            ReadOnlySpan<byte> keyBytes;
            try
            {
                if (_keyScratchBuffer == null)
                {
                    _keyScratchBuffer = new byte[_defaultKeyScratchBufferSize];
                }
                keyBytes = (keySerializer != null)
                    ? keySerializer.Serialize(message.Key, new SerializationContext(MessageComponentType.Key, topicPartition.Topic, headers),_valueScratchBuffer)
                    : throw new InvalidOperationException("Produce called with an IAsyncSerializer key serializer configured but an ISerializer is required.");
            }
            catch (Exception ex)
            {
                throw new ProduceException<TKey, TValue>(
                    new Error(ErrorCode.Local_KeySerialization, ex.ToString()),
                    new DeliveryResult<TKey, TValue>
                    {
                        Message = message,
                        TopicPartitionOffset = new TopicPartitionOffset(topicPartition, Offset.Unset),
                    }
                );
            }

            ReadOnlySpan<byte> valBytes;
            try
            {
                if (_valueScratchBuffer == null)
                {
                    _valueScratchBuffer = new byte[_defaultValueScratchBufferSize];
                }
                valBytes = (valueSerializer != null)
                    ? valueSerializer.Serialize(message.Value, new SerializationContext(MessageComponentType.Value, topicPartition.Topic, headers), _valueScratchBuffer)
                    : throw new InvalidOperationException("Produce called with an IAsyncSerializer value serializer configured but an ISerializer is required.");
            }
            catch (Exception ex)
            {
                throw new ProduceException<TKey, TValue>(
                    new Error(ErrorCode.Local_ValueSerialization, ex.ToString()),
                    new DeliveryResult<TKey, TValue>
                    {
                        Message = message,
                        TopicPartitionOffset = new TopicPartitionOffset(topicPartition, Offset.Unset),
                    }
                );
            }

            try
            {
                ProduceImpl(
                    topicPartition.Topic,
                    valBytes, 
                    keyBytes,
                    message.Timestamp, topicPartition.Partition, 
                    headers,
                    new TypedDeliveryHandlerShim_Action(
                        topicPartition.Topic,
                        enableDeliveryReportKey ? message.Key : default(TKey),
                        enableDeliveryReportValue ? message.Value : default(TValue),
                        deliveryHandler));
            }
            catch (KafkaException ex)
            {
                throw new ProduceException<TKey, TValue>(
                    ex.Error,
                    new DeliveryReport<TKey, TValue>
                        {
                            Message = message,
                            TopicPartitionOffset = new TopicPartitionOffset(topicPartition, Offset.Unset)
                        });
            }
        }

        [ThreadStatic] private static byte[] _keyScratchBuffer;
        [ThreadStatic] private static byte[] _valueScratchBuffer;

        public void BeginProduce(string topic, int partition, TKey key, TValue value, IntPtr userState,
            Timestamp timestamp = new Timestamp(),
            Headers headers = null)
        {
            if (userState != IntPtr.Zero && !enableDeliveryReports)
            {
                throw new InvalidOperationException("Delivery report must be enabled to use userstate.");
            }

            ReadOnlySpan<byte> keyBytes;
            try
            {
                if (_keyScratchBuffer == null)
                {
                    _keyScratchBuffer = new byte[_defaultKeyScratchBufferSize];
                }
                keyBytes = keySerializer.Serialize(key, new SerializationContext(MessageComponentType.Key, topic), _keyScratchBuffer);
            }
            catch (Exception ex)
            {
                throw new ProduceException<TKey, TValue>(
                    new Error(ErrorCode.Local_KeySerialization, ex.ToString()),
                    new DeliveryResult<TKey, TValue>
                    {
                        Message = new Message<TKey, TValue>()
                            {Value = value, Key = key, Headers = headers, Timestamp = timestamp},
                        TopicPartitionOffset = new TopicPartitionOffset(topic, (Partition) partition, Offset.Unset),
                    }
                );
            }

            ReadOnlySpan<byte> valBytes;
            try
            {
                if (_valueScratchBuffer == null)
                {
                    _valueScratchBuffer = new byte[_defaultValueScratchBufferSize];
                }
                valBytes = valueSerializer.Serialize(value, new SerializationContext(MessageComponentType.Value, topic), _valueScratchBuffer);
            }
            catch (Exception ex)
            {
                throw new ProduceException<TKey, TValue>(
                    new Error(ErrorCode.Local_ValueSerialization, ex.ToString()),
                    new DeliveryResult<TKey, TValue>
                    {
                        Message = new Message<TKey, TValue>()
                            {Value = value, Key = key, Headers = headers, Timestamp = timestamp},
                        TopicPartitionOffset = new TopicPartitionOffset(topic, (Partition) partition, Offset.Unset),
                    }
                );
            }

            try
            {
                ProduceImpl(
                    topic,
                    valBytes,
                    keyBytes,
                    timestamp, partition,
                    headers, userState);
            }
            catch (KafkaException ex)
            {
                throw new ProduceException<TKey, TValue>(
                    ex.Error,
                    new DeliveryReport<TKey, TValue>
                    {
                        Message = new Message<TKey, TValue>()
                            {Value = value, Key = key, Headers = headers, Timestamp = timestamp},
                        TopicPartitionOffset = new TopicPartitionOffset(topic, (Partition) partition, Offset.Unset),
                    });
            }
        } 
        
        public void BeginProduceNull(string topic, int partition, TKey key, IntPtr userState,
            Timestamp timestamp = new Timestamp(),
            Headers headers = null)
        {
            if (userState != IntPtr.Zero && !enableDeliveryReports)
            {
                throw new InvalidOperationException("Delivery report must be enabled to use userstate.");
            }

            ReadOnlySpan<byte> keyBytes;
            try
            {
                if (_keyScratchBuffer == null)
                {
                    _keyScratchBuffer = new byte[_defaultKeyScratchBufferSize];
                }
                keyBytes = keySerializer.Serialize(key, new SerializationContext(MessageComponentType.Key, topic), _keyScratchBuffer);
            }
            catch (Exception ex)
            {
                throw new ProduceException<TKey, TValue>(
                    new Error(ErrorCode.Local_KeySerialization, ex.ToString()),
                    new DeliveryResult<TKey, TValue>
                    {
                        Message = new Message<TKey, TValue>()
                            {Value = default, Key = key, Headers = headers, Timestamp = timestamp},
                        TopicPartitionOffset = new TopicPartitionOffset(topic, (Partition) partition, Offset.Unset),
                    }
                );
            }

            try
            {
                ProduceImpl(
                    topic,
                    ReadOnlySpan<byte>.Empty, 
                    keyBytes,
                    timestamp, partition,
                    headers, userState);
            }
            catch (KafkaException ex)
            {
                throw new ProduceException<TKey, TValue>(
                    ex.Error,
                    new DeliveryReport<TKey, TValue>
                    {
                        Message = new Message<TKey, TValue>()
                            {Value = default, Key = key, Headers = headers, Timestamp = timestamp},
                        TopicPartitionOffset = new TopicPartitionOffset(topic, (Partition) partition, Offset.Unset),
                    });
            }
        }


        private int _defaultKeyScratchBufferSize = 65536;
        private int _defaultValueScratchBufferSize = 65536;


        private class TypedTaskDeliveryHandlerShim : TaskCompletionSource<DeliveryResult<TKey, TValue>>, IDeliveryHandler
        {
            public TypedTaskDeliveryHandlerShim(string topic, TKey key, TValue val)
#if !NET45
                : base(TaskCreationOptions.RunContinuationsAsynchronously)
#endif
            {
                Topic = topic;
                Key = key;
                Value = val;
            }

            public string Topic;

            public TKey Key;

            public TValue Value;

            public void HandleDeliveryReport(DeliveryReport<Null, Null> deliveryReport)
            {
                if (deliveryReport == null)
                {
#if NET45
                    System.Threading.Tasks.Task.Run(() => TrySetResult(null));
#else
                    TrySetResult(null);
#endif
                    return;
                }

                var dr = new DeliveryResult<TKey, TValue>
                {
                    TopicPartitionOffset = deliveryReport.TopicPartitionOffset,
                    Status = deliveryReport.Status,
                    Message = new Message<TKey, TValue>
                    {
                        Key = Key,
                        Value = Value,
                        Timestamp = deliveryReport.Message.Timestamp,
                        Headers = deliveryReport.Message.Headers
                    }
                };
                // topic is cached in this object, not set in the deliveryReport to avoid the 
                // cost of marshalling it.
                dr.Topic = Topic;

#if NET45
                if (deliveryReport.Error.IsError)
                {
                    System.Threading.Tasks.Task.Run(() => SetException(new ProduceException<TKey, TValue>(deliveryReport.Error, dr)));
                }
                else
                {
                    System.Threading.Tasks.Task.Run(() => TrySetResult(dr));
                }
#else
                if (deliveryReport.Error.IsError)
                {
                    TrySetException(new ProduceException<TKey, TValue>(deliveryReport.Error, dr));
                }
                else
                {
                    TrySetResult(dr);
                }
#endif
            }
        }

        private class TypedDeliveryHandlerShim_Action : IDeliveryHandler
        {
            public TypedDeliveryHandlerShim_Action(string topic, TKey key, TValue val, Action<DeliveryReport<TKey, TValue>> handler)
            {
                Topic = topic;
                Key = key;
                Value = val;
                Handler = handler;
            }

            public string Topic;

            public TKey Key;

            public TValue Value;

            public Action<DeliveryReport<TKey, TValue>> Handler;

            public void HandleDeliveryReport(DeliveryReport<Null, Null> deliveryReport)
            {
                if (deliveryReport == null)
                {
                    return;
                }

                var dr = new DeliveryReport<TKey, TValue>
                {
                    TopicPartitionOffsetError = deliveryReport.TopicPartitionOffsetError,
                    Status = deliveryReport.Status,
                    Message = new Message<TKey, TValue> 
                    {
                        Key = Key,
                        Value = Value,
                        Timestamp = deliveryReport.Message == null 
                            ? new Timestamp(0, TimestampType.NotAvailable) 
                            : deliveryReport.Message.Timestamp,
                        Headers = deliveryReport.Message?.Headers
                    }
                };
                // topic is cached in this object, not set in the deliveryReport to avoid the 
                // cost of marshalling it.
                dr.Topic = Topic;

                if (Handler != null)
                {
                    Handler(dr);
                }
            }
        }

        /// <inheritdoc/>
        public void InitTransactions(TimeSpan timeout)
            => KafkaHandle.InitTransactions(timeout.TotalMillisecondsAsInt());

        /// <inheritdoc/>
        public void BeginTransaction()
            => KafkaHandle.BeginTransaction();

        /// <inheritdoc/>
        public void CommitTransaction(TimeSpan timeout)
            => KafkaHandle.CommitTransaction(timeout.TotalMillisecondsAsInt());
        
        /// <inheritdoc/>
        public void AbortTransaction(TimeSpan timeout)
            => KafkaHandle.AbortTransaction(timeout.TotalMillisecondsAsInt());

        /// <inheritdoc/>
        public void SendOffsetsToTransaction(IEnumerable<TopicPartitionOffset> offsets, IConsumerGroupMetadata groupMetadata, TimeSpan timeout)
            => KafkaHandle.SendOffsetsToTransaction(offsets, groupMetadata, timeout.TotalMillisecondsAsInt());
    }

    internal delegate void InternalDeliveryReportReceivedDelegate(ref rd_kafka_message msg);
}
