﻿using System;
using System.Collections.Generic;
using Raven.Client.Documents.Indexes;
using Raven.Server.Config.Categories;
using Raven.Server.Documents.Indexes.MapReduce;
using Raven.Server.Documents.Indexes.Persistence;
using Raven.Server.ServerWide;
using Raven.Server.ServerWide.Context;

namespace Raven.Server.Documents.Indexes.Workers.Cleanup
{
    public class CleanupDocuments : CleanupItemsBase
    {
        private readonly Index _index;
        private readonly DocumentsStorage _documentsStorage;

        public CleanupDocuments(Index index, DocumentsStorage documentsStorage, IndexStorage indexStorage,
            IndexingConfiguration configuration, MapReduceIndexingContext mapReduceContext) : base(index, indexStorage, configuration, mapReduceContext)
        {
            _index = index;
            _documentsStorage = documentsStorage;
        }

        public override string Name => "DocumentsCleanup";

        protected override long ReadLastProcessedTombstoneEtag(RavenTransaction transaction, string collection) =>
            IndexStorage.ReadLastProcessedTombstoneEtag(transaction, collection);

        protected override void WriteLastProcessedTombstoneEtag(RavenTransaction transaction, string collection, long lastEtag) =>
            IndexStorage.WriteLastTombstoneEtag(transaction, collection, lastEtag);

        internal override void UpdateStats(IndexProgress.CollectionStats inMemoryStats, long lastEtag) =>
            inMemoryStats.UpdateLastEtag(lastEtag, isTombstone: true);

        protected override IEnumerable<TombstoneIndexItem> GetTombstonesFrom(DocumentsOperationContext context, long etag, long start, long take) =>
            _documentsStorage.GetTombstoneIndexItemsFrom(context, etag, start, take);

        protected override IEnumerable<TombstoneIndexItem> GetTombstonesFrom(DocumentsOperationContext context, string collection, long etag, long start, long take) =>
            _documentsStorage.GetTombstoneIndexItemsFrom(context, collection, etag, start, take);

        protected override bool IsValidTombstoneType(TombstoneIndexItem tombstone)
        {
            if (tombstone.Type != IndexItemType.Document)
                return false;

            return true;
        }

        protected override void HandleDelete(TombstoneIndexItem tombstoneIndexItem, string collection, Lazy<IndexWriteOperationBase> writer, QueryOperationContext queryContext,
            TransactionOperationContext indexContext, IndexingStatsScope stats)
        {
            var tombstone = TombstoneIndexItem.DocumentTombstoneIndexItemToTombstone(queryContext.Documents, tombstoneIndexItem);
            _index.HandleDelete(tombstone, collection, writer, indexContext, stats);
        }
    }
}
