﻿using Raven.Client.Documents.Changes;
using Raven.Client.Documents.Conventions;
using Sparrow.Json;
using Sparrow.Json.Parsing;

namespace Raven.Client.Documents.Operations
{
    public class OperationStatusChange : DatabaseChange
    {
        public long OperationId { get; set; }

        public OperationState State { get; set; }

        public DynamicJsonValue ToJson()
        {
            return new DynamicJsonValue
            {
                [nameof(OperationId)] = OperationId,
                [nameof(State)] = State.ToJson()
            };
        }

        internal static OperationStatusChange FromJson(BlittableJsonReaderObject value)
        {
            value.TryGet(nameof(OperationId), out long operationId);
            value.TryGet(nameof(State), out BlittableJsonReaderObject stateAsJson);

            return new OperationStatusChange
            {
                OperationId = operationId,
                State = (OperationState)DocumentConventions.Default.DeserializeEntityFromBlittable(typeof(OperationState), stateAsJson)
            };
        }
    }
}
