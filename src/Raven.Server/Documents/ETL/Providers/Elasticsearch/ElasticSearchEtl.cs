﻿using System;
using System.Collections.Generic;
using System.Linq;
using Elasticsearch.Net;
using Nest;
using Raven.Client.Documents.Operations.Counters;
using Raven.Client.Documents.Operations.ETL;
using Raven.Client.Documents.Operations.ETL.ElasticSearch;
using Raven.Server.Documents.ETL.Providers.ElasticSearch.Enumerators;
using Raven.Server.Documents.ETL.Providers.ElasticSearch.Test;
using Raven.Server.Documents.ETL.Stats;
using Raven.Server.Documents.Replication.ReplicationItems;
using Raven.Server.Documents.TimeSeries;
using Raven.Server.Exceptions.ETL.ElasticSearch;
using Raven.Server.ServerWide;
using Raven.Server.ServerWide.Context;
using Sparrow.Json;
using Sparrow.Json.Parsing;

namespace Raven.Server.Documents.ETL.Providers.ElasticSearch
{
    public class ElasticSearchEtl : EtlProcess<ElasticSearchItem, ElasticSearchIndexWithRecords, ElasticSearchEtlConfiguration, ElasticSearchConnectionString, EtlStatsScope, EtlPerformanceOperation>
    {
        internal const string IndexBulkAction = @"{""index"":{""_id"":null}}";

        private readonly HashSet<string> _existingIndexes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public readonly ElasticSearchEtlMetricsCountersManager ElasticSearchMetrics = new ElasticSearchEtlMetricsCountersManager();

        public ElasticSearchEtl(Transformation transformation, ElasticSearchEtlConfiguration configuration, DocumentDatabase database, ServerStore serverStore)
            : base(transformation, configuration, database, serverStore, ElasticSearchEtlTag)
        {
            Metrics = ElasticSearchMetrics;
        }

        public const string ElasticSearchEtlTag = "ElasticSearch ETL";

        public override EtlType EtlType => EtlType.ElasticSearch;

        public override bool ShouldTrackCounters() => false;

        public override bool ShouldTrackTimeSeries() => false;

        protected override bool ShouldTrackAttachmentTombstones() => false;

        private ElasticClient _client;

        protected override EtlStatsScope CreateScope(EtlRunStats stats)
        {
            return new EtlStatsScope(stats);
        }

        protected override bool ShouldFilterOutHiLoDocument() => true;

        protected override IEnumerator<ElasticSearchItem> ConvertDocsEnumerator(DocumentsOperationContext context, IEnumerator<Document> docs, string collection)
        {
            return new DocumentsToElasticSearchItems(docs, collection);
        }

        protected override IEnumerator<ElasticSearchItem> ConvertTombstonesEnumerator(DocumentsOperationContext context, IEnumerator<Tombstone> tombstones, string collection,
            bool trackAttachments)
        {
            return new TombstonesToElasticSearchItems(tombstones, collection);
        }

        protected override IEnumerator<ElasticSearchItem> ConvertAttachmentTombstonesEnumerator(DocumentsOperationContext context, IEnumerator<Tombstone> tombstones,
            List<string> collections)
        {
            throw new NotSupportedException("Attachment tombstones aren't supported by ElasticSearch ETL");
        }

        protected override IEnumerator<ElasticSearchItem> ConvertCountersEnumerator(DocumentsOperationContext context, IEnumerator<CounterGroupDetail> counters,
            string collection)
        {
            throw new NotSupportedException("Counters aren't supported by ElasticSearch ETL");
        }

        protected override IEnumerator<ElasticSearchItem> ConvertTimeSeriesEnumerator(DocumentsOperationContext context, IEnumerator<TimeSeriesSegmentEntry> timeSeries,
            string collection)
        {
            throw new NotSupportedException("Time series aren't supported by ElasticSearch ETL");
        }

        protected override IEnumerator<ElasticSearchItem> ConvertTimeSeriesDeletedRangeEnumerator(DocumentsOperationContext context,
            IEnumerator<TimeSeriesDeletedRangeItem> timeSeries, string collection)
        {
            throw new NotSupportedException("Time series aren't supported by ElasticSearch ETL");
        }

        protected override EtlTransformer<ElasticSearchItem, ElasticSearchIndexWithRecords, EtlStatsScope, EtlPerformanceOperation> GetTransformer(DocumentsOperationContext context)
        {
            return new ElasticSearchDocumentTransformer(Transformation, Database, context, Configuration);
        }

        protected override int LoadInternal(IEnumerable<ElasticSearchIndexWithRecords> records, DocumentsOperationContext context, EtlStatsScope scope)
        {
            int count = 0;

            _client ??= ElasticSearchHelper.CreateClient(Configuration.Connection);

            foreach (var index in records)
            {
                string indexName = index.IndexName.ToLower();

                EnsureIndexExists(indexName, index);
                
                var actionDataPairs = new List<string>();

                foreach (ElasticSearchItem insert in index.Inserts)
                {
                    if (insert.Property == null) 
                        continue;

                    using (var json = EnsureLowerCasedIndexIdProperty(context, insert.Property.RawValue, index))
                    {
                        actionDataPairs.Add(IndexBulkAction); // action
                        actionDataPairs.Add(json.ToString()); // json data
                    }

                    count++;
                }

                if (index.InsertOnlyMode == false)
                    count += DeleteByQueryOnIndexIdProperty(index);

                if (actionDataPairs.Count > 0)
                {
                    var bulkBody = PostData.MultiJson(actionDataPairs);

                    var bulkIndexResponse = _client.LowLevel.Bulk<BulkResponse>(indexName, bulkBody, new BulkRequestParameters { Refresh = Refresh.WaitFor });

                    if (bulkIndexResponse.IsValid == false)
                        ThrowElasticSearchLoadException($"Failed to index data to '{index}' index", bulkIndexResponse.ServerError, bulkIndexResponse.OriginalException,
                            bulkIndexResponse.DebugInformation);
                }
            }

            return count;
        }

        internal static BlittableJsonReaderObject EnsureLowerCasedIndexIdProperty(DocumentsOperationContext context, BlittableJsonReaderObject json,
            ElasticSearchIndexWithRecords index)
        {
            if (json.TryGet(index.IndexIdProperty, out LazyStringValue idProperty))
            {
                using (var old = json)
                {
                    json.Modifications = new DynamicJsonValue(json) { [index.IndexIdProperty] = LowerCaseIndexIdProperty(idProperty) };

                    json = context.ReadObject(json, "es-etl-load");
                }
            }

            return json;
        }

        private int DeleteByQueryOnIndexIdProperty(ElasticSearchIndexWithRecords index)
        {
            string indexName = index.IndexName.ToLower();

            var idsToDelete = new List<string>();

            foreach (ElasticSearchItem delete in index.Deletes)
            {
                idsToDelete.Add(LowerCaseIndexIdProperty(delete.DocumentId));
            }

            var deleteResponse = _client.DeleteByQuery<string>(d => d
                .Index(indexName)
                .Refresh()
                .Query(q => q
                    .Terms(p => p
                        .Field(index.IndexIdProperty)
                        .Terms((IEnumerable<string>)idsToDelete))
                )
            );

            if (deleteResponse.IsValid == false)
                ThrowElasticSearchLoadException($"Failed to delete by query from index '{index}'. Documents IDs: {string.Join(',', idsToDelete)}",
                    deleteResponse.ServerError, deleteResponse.OriginalException, deleteResponse.DebugInformation);

            return (int)deleteResponse.Deleted;
        }

        private void EnsureIndexExists(string indexName, ElasticSearchIndexWithRecords index)
        {
            if (_existingIndexes.Contains(indexName) == false && _client.Indices.Exists(new IndexExistsRequest(Indices.Index(indexName))).Exists == false)
            {
                CreateDefaultIndex(indexName, index);

                _existingIndexes.Add(indexName);
            }
            else
            {
                _existingIndexes.Add(indexName);
            }
        }

        private void CreateDefaultIndex(string indexName, ElasticSearchIndexWithRecords index)
        {
            var response = _client.Indices.Create(indexName, c => c
                .Map(m => m
                    .Properties(p => p
                        .Keyword(t => t
                            .Name(index.IndexIdProperty)))));

            // The request made it to the server but something went wrong in ElasticSearch (query parsing exception, non-existent index, etc)
            if (response.ServerError != null)
                throw new ElasticSearchLoadException(
                    $"Failed to create '{indexName}' index. Error: {response.ServerError.Error}. Debug Information: {response.DebugInformation}");

            // ElasticSearch error occurred or a connection error (the server could not be reached, request timed out, etc)
            if (response.OriginalException != null)
                throw new ElasticSearchLoadException($"Failed to create '{indexName}' index. Debug Information: {response.DebugInformation}", response.OriginalException);
        }

        internal static string LowerCaseIndexIdProperty(LazyStringValue id)
        {
            return id.ToLowerInvariant();
        }

        private void ThrowElasticSearchLoadException(string message, ServerError serverError, Exception originalException, string debugInformation)
        {
            if (serverError != null)
                message += $". Server error: {serverError}";

            if (string.IsNullOrEmpty(debugInformation) == false)
                message += $". Debug information: {debugInformation}";

            throw new ElasticSearchLoadException(message, originalException);
        }

        public ElasticSearchEtlTestScriptResult RunTest(IEnumerable<ElasticSearchIndexWithRecords> records, DocumentsOperationContext context)
        {
            var simulatedWriter = new ElasticSearchIndexWriterSimulator();
            var summaries = new List<IndexSummary>();
            
            foreach (var record in records)
            {
                var commands = simulatedWriter.SimulateExecuteCommandText(record, context);
                
                summaries.Add(new IndexSummary
                {
                    IndexName = record.IndexName.ToLower(),
                    Commands = commands.ToArray()
                });
            }
            
            return new ElasticSearchEtlTestScriptResult
            {
                TransformationErrors = Statistics.TransformationErrorsInCurrentBatch.Errors.ToList(),
                Summary = summaries
            };
        }
    }
}
