﻿using System;
using System.Threading.Tasks;
using JetBrains.Annotations;
using Raven.Client.Http;
using Raven.Server.Documents.Handlers.Processors.Indexes;
using Raven.Server.ServerWide.Context;

namespace Raven.Server.Documents.Sharding.Handlers.Processors.Indexes;

internal class ShardedIndexProcessorForGenerateCSharpIndexDefinition : AbstractIndexProcessorForGenerateCSharpIndexDefinition<ShardedDatabaseRequestHandler, TransactionOperationContext>
{
    public ShardedIndexProcessorForGenerateCSharpIndexDefinition([NotNull] ShardedDatabaseRequestHandler requestHandler)
        : base(requestHandler, requestHandler.ContextPool)
    {
    }

    protected override bool SupportsCurrentNode => false;

    protected override ValueTask<string> GetResultForCurrentNodeAsync() => throw new NotSupportedException();

    protected override Task<string> GetResultForRemoteNodeAsync(RavenCommand<string> command)
    {
        var shardNumber = GetShardNumber();

        return RequestHandler.ShardExecutor.ExecuteSingleShardAsync(command, shardNumber);
    }
}