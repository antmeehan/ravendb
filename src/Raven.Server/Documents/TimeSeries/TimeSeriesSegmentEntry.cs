﻿using System;
using Sparrow.Json;

namespace Raven.Server.Documents.TimeSeries
{
    public class TimeSeriesSegmentEntry
    {
        public LazyStringValue Key;

        public LazyStringValue DocId;

        public LazyStringValue Name;

        public LazyStringValue DocIdAndName;

        public string ChangeVector;

        public TimeSeriesValuesSegment Segment;

        public int SegmentSize;

        public LazyStringValue Collection;

        public DateTime Baseline;

        public long Etag;
    }
}
