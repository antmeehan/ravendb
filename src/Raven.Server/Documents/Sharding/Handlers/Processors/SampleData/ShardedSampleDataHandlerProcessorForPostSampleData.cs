﻿using System.IO;
using System.Threading.Tasks;
using JetBrains.Annotations;
using Raven.Client.Documents.Smuggler;
using Raven.Server.Documents.Handlers.Processors.SampleData;
using Raven.Server.ServerWide.Context;
using Raven.Server.Smuggler.Documents;
using Raven.Server.Smuggler.Documents.Data;
using Sparrow.Json;

namespace Raven.Server.Documents.Sharding.Handlers.Processors.SampleData
{
    internal class ShardedSampleDataHandlerProcessorForPostSampleData : AbstractSampleDataHandlerProcessorForPostSampleData<ShardedDatabaseRequestHandler, TransactionOperationContext>
    {
        public ShardedSampleDataHandlerProcessorForPostSampleData([NotNull] ShardedDatabaseRequestHandler requestHandler) : base(requestHandler, requestHandler.ContextPool)
        {
        }

        protected override string GetDatabaseName()
        {
            return RequestHandler.DatabaseContext.DatabaseName;
        }

        protected override async ValueTask WaitForIndexNotificationAsync(long index)
        {
            await RequestHandler.ServerStore.Cluster.WaitForIndexNotification(index, RequestHandler.ServerStore.Engine.OperationTimeout);
        }

        protected override async ValueTask ExecuteSmugglerAsync(JsonOperationContext context, ISmugglerSource source, Stream sampleData, DatabaseItemType operateOnTypes)
        {
            var record = RequestHandler.DatabaseContext.DatabaseRecord;

            var smuggler = new ShardedDatabaseSmuggler(source, context, record,
                RequestHandler.Server.ServerStore, RequestHandler.DatabaseContext, RequestHandler,
                options: new DatabaseSmugglerOptionsServerSide
                {
                    OperateOnTypes = operateOnTypes, 
                    SkipRevisionCreation = true
                }, null);

            await smuggler.ExecuteAsync();
        }

        protected override async ValueTask<bool> IsDatabaseEmptyAsync()
        {
            var op = new ShardedCollectionHandler.ShardedCollectionStatisticsOperation();
            var stats = await RequestHandler.ShardExecutor.ExecuteParallelForAllAsync(op);

            return stats.Collections.Count == 0;
        }
    }
}