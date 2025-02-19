﻿// This source code is dual-licensed under the Apache License, version
// 2.0, and the Mozilla Public License, version 2.0.
// Copyright (c) 2007-2020 VMware, Inc.

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace RabbitMQ.Stream.Client
{
    public struct MessageContext
    {
        public ulong Offset { get; }

        public TimeSpan Timestamp { get; }

        public MessageContext(ulong offset, TimeSpan timestamp)
        {
            Offset = offset;
            Timestamp = timestamp;
        }
    }

    internal struct ConsumerEvents
    {
        public ConsumerEvents(Func<Deliver, Task> deliverHandler,
            Func<bool, Task<IOffsetType>> consumerUpdateHandler)
        {
            DeliverHandler = deliverHandler;
            ConsumerUpdateHandler = consumerUpdateHandler;
        }

        public Func<Deliver, Task> DeliverHandler { get; }
        public Func<bool, Task<IOffsetType>> ConsumerUpdateHandler { get; }
    }

    public record RawConsumerConfig : IConsumerConfig
    {
        public RawConsumerConfig(string stream)
        {
            if (string.IsNullOrWhiteSpace(stream))
            {
                throw new ArgumentException("Stream cannot be null or whitespace.", nameof(stream));
            }

            Stream = stream;
        }

        internal void Validate()
        {
            if (IsSingleActiveConsumer && (Reference == null || Reference.Trim() == string.Empty))
            {
                throw new ArgumentException("With single active consumer, the reference must be set.");
            }
        }

        public IOffsetType OffsetSpec { get; set; } = new OffsetTypeNext();

        // stream name where the consumer will consume the messages.
        // stream must exist before the consumer is created.
        public string Stream { get; }

        public Func<RawConsumer, MessageContext, Message, Task> MessageHandler { get; set; }

        public Action<MetaDataUpdate> MetadataHandler { get; set; } = _ => { };
    }

    public class RawConsumer : AbstractEntity, IConsumer, IDisposable
    {
        private bool _disposed;
        private readonly RawConsumerConfig _config;
        private byte _subscriberId;

        private RawConsumer(Client client, RawConsumerConfig config)
        {
            _client = client;
            _config = config;
        }

        // if a user specify a custom offset 
        // the _client must filter messages
        // and dispatch only the messages starting from the 
        // user offset.
        private bool MaybeDispatch(ulong offset)
        {
            return _config.StoredOffsetSpec switch
            {
                OffsetTypeOffset offsetTypeOffset =>
                    !(offset < offsetTypeOffset.OffsetValue),
                _ => true
            };
        }

        public async Task StoreOffset(ulong offset)
        {
            await _client.StoreOffset(_config.Reference, _config.Stream, offset);
        }

        public static async Task<IConsumer> Create(ClientParameters clientParameters,
            RawConsumerConfig config,
            StreamInfo metaStreamInfo)
        {
            var _client = await RoutingHelper<Routing>.LookupRandomConnection(clientParameters, metaStreamInfo);
            var consumer = new RawConsumer((Client)_client, config);
            await consumer.Init();
            return consumer;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ParseChunk(Chunk chunk)
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            void DispatchMessage(ref SequenceReader<byte> sequenceReader, ulong i)
            {
                WireFormatting.ReadUInt32(ref sequenceReader, out var len);
                try
                {
                    var message = Message.From(ref sequenceReader, len);
                    message.MessageOffset = chunk.ChunkId + i;
                    if (MaybeDispatch(message.MessageOffset))
                    {
                        _config.MessageHandler(this,
                            new MessageContext(message.MessageOffset, TimeSpan.FromMilliseconds(chunk.Timestamp)),
                            message);
                    }
                }
                catch (Exception e)
                {
                    LogEventSource.Log.LogError(
                        $"Error while processing chunk: {chunk.ChunkId} error: {e.Message}");
                }
            }

            var reader = new SequenceReader<byte>(chunk.Data);
            if (chunk.HasSubEntries)
            {
                // it means that it is a subentry batch 
                var numRecords = chunk.NumRecords;
                while (numRecords != 0)
                {
                    SubEntryChunk.Read(ref reader, out var subEntryChunk);
                    var unCompressedData = CompressionHelper.UnCompress(
                        subEntryChunk.CompressionType,
                        subEntryChunk.Data,
                        subEntryChunk.DataLen,
                        subEntryChunk.UnCompressedDataSize);
                    var readerUnCompressed = new SequenceReader<byte>(unCompressedData);

                    for (ulong z = 0; z < subEntryChunk.NumRecordsInBatch; z++)
                    {
                        DispatchMessage(ref readerUnCompressed, z);
                    }

                    numRecords -= subEntryChunk.NumRecordsInBatch;
                }
            }
            else
            {
                // Standard chunk. 
                for (ulong i = 0; i < chunk.NumEntries; i++)
                {
                    DispatchMessage(ref reader, i);
                }
            }
        }

        private async Task Init()
        {
            _config.Validate();

            _client.ConnectionClosed += async reason =>
            {
                if (_config.ConnectionClosedHandler != null)
                {
                    await _config.ConnectionClosedHandler(reason);
                }
            };
            if (_config.MetadataHandler != null)
            {
                _client.Parameters.MetadataHandler += _config.MetadataHandler;
            }

            var consumerProperties = new Dictionary<string, string>();
            if (_config.IsSingleActiveConsumer)
            {
                consumerProperties["name"] = _config.Reference;
                consumerProperties["single-active-consumer"] = "true";
            }

            // this the default value for the consumer.
            _config.StoredOffsetSpec = _config.OffsetSpec;
            const ushort InitialCredit = 10;

            var (consumerId, response) = await _client.Subscribe(
                _config,
                InitialCredit,
                consumerProperties,
                async deliver =>
                {
                    // receive the chunk from the deliver
                    // before parse the chunk, we ask for more credits
                    // in thi way we keep the network busy
                    await _client.Credit(deliver.SubscriptionId, 1);
                    // parse the chunk, we have another function because the sequence reader
                    // can't be used in async context
                    ParseChunk(deliver.Chunk);
                }, async b =>
                {
                    if (_config.ConsumerUpdateListener != null)
                    {
                        // in this case the StoredOffsetSpec is overridden by the ConsumerUpdateListener
                        // since the user decided to override the default behavior
                        _config.StoredOffsetSpec = await _config.ConsumerUpdateListener(
                            _config.Reference,
                            _config.Stream,
                            b);
                    }

                    return _config.StoredOffsetSpec;
                }
            );
            if (response.ResponseCode == ResponseCode.Ok)
            {
                _subscriberId = consumerId;
                return;
            }

            throw new CreateConsumerException($"consumer could not be created code: {response.ResponseCode}");
        }

        public async Task<ResponseCode> Close()
        {
            if (_client.IsClosed)
            {
                return ResponseCode.Ok;
            }

            var result = ResponseCode.Ok;
            try
            {
                var deleteConsumerResponseTask = _client.Unsubscribe(_subscriberId);
                // The  default timeout is usually 10 seconds 
                // in this case we reduce the waiting time
                // the consumer could be removed because of stream deleted 
                // so it is not necessary to wait.
                await deleteConsumerResponseTask.WaitAsync(TimeSpan.FromSeconds(3));
                if (deleteConsumerResponseTask.IsCompletedSuccessfully)
                {
                    result = deleteConsumerResponseTask.Result.ResponseCode;
                }
            }
            catch (Exception e)
            {
                LogEventSource.Log.LogError($"Error removing the consumer id: {_subscriberId} from the server. {e}");
            }

            var closed = _client.MaybeClose($"_client-close-subscriber: {_subscriberId}");
            ClientExceptions.MaybeThrowException(closed.ResponseCode, $"_client-close-subscriber: {_subscriberId}");
            _disposed = true;
            return result;
        }

        //
        private void Dispose(bool disposing)
        {
            if (!disposing)
            {
                return;
            }

            if (_disposed)
            {
                return;
            }

            var closeConsumer = Close();
            closeConsumer.Wait(TimeSpan.FromSeconds(1));
            ClientExceptions.MaybeThrowException(closeConsumer.Result,
                $"Error during remove producer. Subscriber: {_subscriberId}");
        }

        public void Dispose()
        {
            try
            {
                Dispose(true);
            }
            catch (Exception e)
            {
                LogEventSource.Log.LogError($"Error during disposing Consumer: {_subscriberId}.", e);
            }

            GC.SuppressFinalize(this);
        }
    }
}
