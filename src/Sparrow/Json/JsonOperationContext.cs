﻿using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Sparrow.Collections;
using Sparrow.Global;
using Sparrow.Json.Parsing;
using Sparrow.Threading;
using Sparrow.Utils;

#if VALIDATE

using Sparrow.Debugging;

#endif

namespace Sparrow.Json
{
    /// <summary>
    /// Single threaded for contexts
    /// </summary>
    public class JsonOperationContext : PooledItem
    {
        private int _generation;
        internal long PoolGeneration;
        public const int InitialStreamSize = 4096;
        private const int MaxInitialStreamSize = 16 * 1024 * 1024;
        private readonly int _initialSize;
        private readonly int _longLivedSize;
        private readonly int _maxNumberOfAllocatedStringValues;
        private readonly ArenaMemoryAllocator _arenaAllocator;
        private ArenaMemoryAllocator _arenaAllocatorForLongLivedValues;
        private AllocatedMemoryData _tempBuffer;

        private readonly Dictionary<StringSegment, LazyStringValue> _fieldNames = new Dictionary<StringSegment, LazyStringValue>(StringSegmentEqualityStructComparer.BoxedInstance);

        private struct PathCacheHolder
        {
            public PathCacheHolder(Dictionary<StringSegment, object> path, Dictionary<int, object> byIndex)
            {
                Path = path;
                ByIndex = byIndex;
            }

            public readonly Dictionary<StringSegment, object> Path;
            public readonly Dictionary<int, object> ByIndex;
        }

        private int _numberOfAllocatedPathCaches = -1;
        private readonly PathCacheHolder[] _allocatePathCaches = new PathCacheHolder[512];
        private readonly Stack<MemoryStream> _cachedMemoryStreams = new Stack<MemoryStream>();

        private int _numberOfAllocatedStringsValues;
        private readonly FastList<LazyStringValue> _allocateStringValues = new FastList<LazyStringValue>(256);

        private int _highestNumberOfReusedAllocatedStringsValuesInCurrentInterval;
        private DateTime _lastAllocatedStringValueTime = DateTime.UtcNow;
        private static readonly TimeSpan _allocatedStringValuesCheckInterval = TimeSpan.FromMinutes(1);

        /// <summary>
        /// This flag means that this should be disposed, usually because we exceeded the maximum
        /// amount of memory budget we have and need to return it to the system
        /// </summary>
        public bool DoNotReuse;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void AcquirePathCache(out Dictionary<StringSegment, object> pathCache, out Dictionary<int, object> pathCacheByIndex)
        {
            // PERF: Avoids allocating gigabytes in FastDictionary instances on high traffic RW operations like indexing.
            if (_numberOfAllocatedPathCaches >= 0)
            {
                var cache = _allocatePathCaches[_numberOfAllocatedPathCaches--];
                Debug.Assert(cache.Path != null);
                Debug.Assert(cache.ByIndex != null);

                pathCache = cache.Path;
                pathCacheByIndex = cache.ByIndex;

                return;
            }

            pathCache = new Dictionary<StringSegment, object>(StringSegmentEqualityStructComparer.BoxedInstance);
            pathCacheByIndex = new Dictionary<int, object>(NumericEqualityComparer.BoxedInstanceInt32);
        }

        public void ReleasePathCache(Dictionary<StringSegment, object> pathCache, Dictionary<int, object> pathCacheByIndex)
        {
            if (_numberOfAllocatedPathCaches < _allocatePathCaches.Length - 1 && pathCache.Count < 256)
            {
                pathCache.Clear();
                pathCacheByIndex.Clear();

                _allocatePathCaches[++_numberOfAllocatedPathCaches] = new PathCacheHolder(pathCache, pathCacheByIndex);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe LazyStringValue AllocateStringValue(string str, byte* ptr, int size)
        {
            if (_numberOfAllocatedStringsValues < _allocateStringValues.Count)
            {
                var lazyStringValue = _allocateStringValues[_numberOfAllocatedStringsValues++];
                _highestNumberOfReusedAllocatedStringsValuesInCurrentInterval = Math.Max(_highestNumberOfReusedAllocatedStringsValuesInCurrentInterval, _numberOfAllocatedStringsValues);

                lazyStringValue.Renew(str, ptr, size);
                return lazyStringValue;
            }

            var allocateStringValue = new LazyStringValue(str, ptr, size, this);
            if (_numberOfAllocatedStringsValues < _maxNumberOfAllocatedStringValues)
            {
                _allocateStringValues.Add(allocateStringValue);
                _numberOfAllocatedStringsValues++;

                _lastAllocatedStringValueTime = DateTime.UtcNow;
            }

            return allocateStringValue;
        }

        public unsafe class ManagedPinnedBuffer : IDisposable
        {
            public const int LargeBufferSize = 128 * Constants.Size.Kilobyte;
            public const int Size = 32 * Constants.Size.Kilobyte;

            internal BufferSegment BufferInstance;
            public ArraySegment<byte> Buffer;
            public int Length;
            public int Valid, Used;
            public byte* Pointer;

            private GCHandle _handle;
            private bool _disposed;

            public void Dispose()
            {
                if (_disposed)
                    return;
                _disposed = true;
                GC.SuppressFinalize(this);
                var bufferBefore = BufferInstance;
                BufferInstance = null;
                Buffer = new ArraySegment<byte>();
               
                UnpinMemory();

                Length = 0;
                Valid = Used = 0;

                if (bufferBefore != null)
                {
                    if (bufferBefore.Count == LargeBufferSize)
                        _largeBufferSegments.Free(bufferBefore);
                    else
                        _smallBufferSegments.Free(bufferBefore);
                }

                _pinnedBufferPool.Free(this);
            }

            ~ManagedPinnedBuffer()
            {
                UnpinMemory();
            }

            public (IDisposable ReleaseBuffer, ManagedPinnedBuffer Buffer) Clone<T>(JsonContextPoolBase<T> pool)
                where T : JsonOperationContext
            {
                if (Length != Size)
                    throw new InvalidOperationException("Cloned buffer must be of the same size");

                var releaseCtx = pool.AllocateOperationContext(out T ctx);
                var returnBuffer = ctx.GetManagedBuffer(out var buffer);
                var clean = new Disposer(returnBuffer, releaseCtx);
                try
                {
                    buffer.Valid = Valid - Used;
                    buffer.Used = 0;
                    Memory.Copy(buffer.Pointer, Pointer + Used, buffer.Valid);

                    return (clean, buffer);
                }
                catch
                {
                    clean.Dispose();
                    throw;
                }
            }

            private class Disposer : IDisposable
            {
                private readonly IDisposable[] _toDispose;

                public Disposer(params IDisposable[] toDispose)
                {
                    _toDispose = toDispose;
                }

                public void Dispose()
                {
                    foreach (var disposable in _toDispose)
                    {
                        disposable.Dispose();
                    }
                }
            }

            private ManagedPinnedBuffer()
            {
                GC.SuppressFinalize(this); // we only want finalization if we have values
            }

            private void Init(BufferSegment buffer)
            {
                BufferInstance = buffer;
                Buffer = new ArraySegment<byte>(buffer.Array, buffer.Offset, buffer.Count);
                Length = buffer.Count;
                GC.ReRegisterForFinalize(this);
            }

            public void PinMemory()
            {
                Debug.Assert(_handle.IsAllocated == false, "_handle.IsAllocated == false");

                _handle = GCHandle.Alloc(BufferInstance.Array, GCHandleType.Pinned);
                Pointer = (byte*)_handle.AddrOfPinnedObject() + BufferInstance.Offset;
            }

            public void UnpinMemory()
            {
                if (_handle.IsAllocated)
                {
                    _handle.Free();
                    Pointer = null;
                }
            }

            private static readonly ObjectPool<ManagedPinnedBuffer> _pinnedBufferPool = new ObjectPool<ManagedPinnedBuffer>(() => new ManagedPinnedBuffer());
            private static readonly ObjectPool<BufferSegment> _smallBufferSegments = new ObjectPool<BufferSegment>(CreateSmallBuffers);

            private static readonly ObjectPool<BufferSegment> _largeBufferSegments = new ObjectPool<BufferSegment>(() => new BufferSegment
            {
                Array = new byte[LargeBufferSize],
                Count = LargeBufferSize,
                Offset = 0
            });

            private static BufferSegment CreateSmallBuffers()
            {
                Debug.Assert(Size * 8 > 80 * 1024);// expected to reside on LOH
                var buffer = new byte[Size * 8];

                for (int i = 0; i < 7; i++)
                {
                    // we put the remaining values in the buffer pool
                    _smallBufferSegments.Free(new BufferSegment
                    {
                        Array = buffer,
                        Count = Size,
                        Offset = i * Size
                    });
                }
                return new BufferSegment
                {
                    Array = buffer,
                    Count = Size,
                    Offset = Size * 7,
                };
            }

            internal class BufferSegment
            {
                public byte[] Array;
                public int Offset;
                public int Count;
            }

            public static ManagedPinnedBuffer LongLivedInstance()
            {
                return AllocateInstance(_largeBufferSegments);
            }

            private static ManagedPinnedBuffer AllocateInstance(ObjectPool<BufferSegment> pool)
            {
                var buffer = pool.Allocate();
               
                try
                {
                    var mpb = _pinnedBufferPool.Allocate();

                    mpb.Init(buffer);
                    mpb.PinMemory();

                    return mpb;
                }
                catch (Exception)
                {
                    pool.Free(buffer);
                    throw;
                }
            }

            public static ManagedPinnedBuffer ShortLivedInstance()
            {
                return AllocateInstance(_smallBufferSegments);
            }
        }

        private Stack<ManagedPinnedBuffer> _managedBuffers;

        public CachedProperties CachedProperties;

        private readonly JsonParserState _jsonParserState;
        private readonly ObjectJsonParser _objectJsonParser;
        private readonly BlittableJsonDocumentBuilder _documentBuilder;

        public int Generation => _generation;

        public long AllocatedMemory => _arenaAllocator.TotalUsed;

        protected readonly SharedMultipleUseFlag LowMemoryFlag;

        public static JsonOperationContext ShortTermSingleUse()
        {
            return new JsonOperationContext(4096, 1024, 8 * 1024, SharedMultipleUseFlag.None);
        }

        public JsonOperationContext(int initialSize, int longLivedSize, int maxNumberOfAllocatedStringValues, SharedMultipleUseFlag lowMemoryFlag)
        {
            Debug.Assert(lowMemoryFlag != null);
            _disposeOnceRunner = new DisposeOnce<SingleAttempt>(() =>
            {
#if MEM_GUARD_STACK
                DebugStuff.ElectricFencedMemory.DecrementContext();
                DebugStuff.ElectricFencedMemory.UnregisterContextAllocation(this);
#endif

                List<Exception> exceptions = null;

                TryExecute(() => Reset(true));

                TryDispose(_documentBuilder);
                TryDispose(_arenaAllocator);
                TryDispose(_arenaAllocatorForLongLivedValues);
                TryExecute(_allocateStringValues.Clear);

                if (_managedBuffers != null)
                {
                    foreach (var managedPinnedBuffer in _managedBuffers)
                    {
                        TryDispose(managedPinnedBuffer as IDisposable);
                    }

                    _managedBuffers = null;
                }

                if (exceptions != null)
                    throw new AggregateException("Failed to dispose context", exceptions);

                void TryDispose(IDisposable d)
                {
                    if (d == null)
                        return;
                    TryExecute(d.Dispose);
                }

                void TryExecute(Action a)
                {
                    try
                    {
                        a();
                    }
                    catch (Exception e)
                    {
                        if (exceptions == null)
                            exceptions = new List<Exception>();
                        exceptions.Add(e);
                    }
                }
            });

            _initialSize = initialSize;
            _longLivedSize = longLivedSize;
            _maxNumberOfAllocatedStringValues = maxNumberOfAllocatedStringValues;
            _arenaAllocator = new ArenaMemoryAllocator(lowMemoryFlag, initialSize);
            _arenaAllocatorForLongLivedValues = new ArenaMemoryAllocator(lowMemoryFlag, longLivedSize);
            CachedProperties = new CachedProperties(this);
            _jsonParserState = new JsonParserState();
            _objectJsonParser = new ObjectJsonParser(_jsonParserState, this);
            _documentBuilder = new BlittableJsonDocumentBuilder(this, _jsonParserState, _objectJsonParser);
            LowMemoryFlag = lowMemoryFlag;

#if MEM_GUARD_STACK
            DebugStuff.ElectricFencedMemory.IncrementContext();
            DebugStuff.ElectricFencedMemory.RegisterContextAllocation(this, Environment.StackTrace);
#endif
        }

        public ReturnBuffer GetManagedBuffer(out ManagedPinnedBuffer buffer)
        {
            EnsureNotDisposed();
            if (_managedBuffers == null)
                _managedBuffers = new Stack<ManagedPinnedBuffer>();

            if (_managedBuffers.Count == 0)
                buffer = ManagedPinnedBuffer.ShortLivedInstance();
            else
            {
                buffer = _managedBuffers.Pop();
                buffer.PinMemory();
            }

            buffer.Valid = buffer.Used = 0;

            return new ReturnBuffer(buffer, this);
        }

        public struct ReturnBuffer : IDisposable
        {
            private ManagedPinnedBuffer _buffer;
            private readonly JsonOperationContext _parent;

            public ReturnBuffer(ManagedPinnedBuffer buffer, JsonOperationContext parent)
            {
                _buffer = buffer;
                _parent = parent;
            }

            public void Dispose()
            {
                if (_buffer == null)
                    return;

                //_parent disposal sets _managedBuffers to null,
                //throwing ObjectDisposedException() to make it more visible
                if (_parent.Disposed)
                    ThrowParentWasDisposed();

                _buffer.UnpinMemory();
                _parent._managedBuffers.Push(_buffer);
                _buffer = null;
            }

            private static void ThrowParentWasDisposed()
            {
                throw new ObjectDisposedException(
                    "ReturnBuffer should not be disposed after it's parent operation context was disposed");
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public AllocatedMemoryData GetMemory(int requestedSize)
        {
#if DEBUG || VALIDATE
            if (requestedSize <= 0)
                throw new ArgumentException(nameof(requestedSize));
#endif

            var allocatedMemory = _arenaAllocator.Allocate(requestedSize);
            allocatedMemory.ContextGeneration = Generation;
            allocatedMemory.Parent = this;
#if DEBUG
            allocatedMemory.IsLongLived = false;
#endif
            return allocatedMemory;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public AllocatedMemoryData GetLongLivedMemory(int requestedSize)
        {
#if DEBUG || VALIDATE
            if (requestedSize <= 0)
                throw new ArgumentException(nameof(requestedSize));
#endif
            //we should use JsonOperationContext in single thread
            if (_arenaAllocatorForLongLivedValues == null)
            {
                //_arenaAllocatorForLongLivedValues == null when the context is after Reset() but before Renew()
                ThrowAlreadyDisposedForLongLivedAllocator();

                //make compiler happy, previous row will throw
                return null;
            }

            var allocatedMemory = _arenaAllocatorForLongLivedValues.Allocate(requestedSize);
            allocatedMemory.ContextGeneration = Generation;
            allocatedMemory.Parent = this;
#if DEBUG
            allocatedMemory.IsLongLived = true;
#endif
            return allocatedMemory;
        }

        private static void ThrowAlreadyDisposedForLongLivedAllocator()
        {
            throw new ObjectDisposedException("Could not allocated long lived memory, because the context is after Reset() but before Renew(). Is it possible that you have tried to use the context AFTER it was returned to the context pool?");
        }

        /// <summary>
        /// Generates new unmanaged stream. Should be disposed at the end of the usage.
        /// </summary>
        public UnmanagedWriteBuffer GetStream(int initialSize)
        {
            var bufferMemory = GetMemory(Math.Min(MaxInitialStreamSize, Math.Max(InitialStreamSize, initialSize)));
            return new UnmanagedWriteBuffer(this, bufferMemory);
        }

        private readonly DisposeOnce<SingleAttempt> _disposeOnceRunner;
        public bool Disposed => _disposeOnceRunner.Disposed;

        public override void Dispose()
        {
            _disposeOnceRunner.Dispose();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public LazyStringValue GetLazyStringForFieldWithCaching(StringSegment key)
        {
            EnsureNotDisposed();

            if (_fieldNames.TryGetValue(key, out LazyStringValue value))
            {
                //sanity check, in case the 'value' is manually disposed outside of this function
                Debug.Assert(value.IsDisposed == false);
                return value;
            }

            return GetLazyStringForFieldWithCachingUnlikely(key);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public LazyStringValue GetLazyStringForFieldWithCaching(string field)
        {
            EnsureNotDisposed();
            if (_fieldNames.TryGetValue(field, out LazyStringValue value))
            {
                // PERF: This is usually the most common scenario, so actually being contiguous improves the behavior.
                Debug.Assert(value.IsDisposed == false);
                return value;
            }

            return GetLazyStringForFieldWithCachingUnlikely(field);
        }

        private LazyStringValue GetLazyStringForFieldWithCachingUnlikely(StringSegment key)
        {
#if DEBUG || VALIDATE
            using (new SingleThreadAccessAssertion(_threadId, "GetLazyStringForFieldWithCachingUnlikely"))
            {
#endif
                EnsureNotDisposed();
                LazyStringValue value = GetLazyString(key, longLived: true);
                _fieldNames[key.Value] = value;

                //sanity check, in case the 'value' is manually disposed outside of this function
                Debug.Assert(value.IsDisposed == false);
                return value;
#if DEBUG || VALIDATE
            }
#endif
        }

        public LazyStringValue GetLazyString(string field)
        {
            EnsureNotDisposed();

            if (field == null)
                return null;

            return GetLazyString(field, longLived: false);
        }

        private unsafe LazyStringValue GetLazyString(StringSegment field, bool longLived)
        {
            var state = new JsonParserState();
            var maxByteCount = Encodings.Utf8.GetMaxByteCount(field.Length);

            int escapePositionsSize = JsonParserState.FindEscapePositionsMaxSize(field, out _);

            int memorySize = maxByteCount + escapePositionsSize;
            var memory = longLived ? GetLongLivedMemory(memorySize) : GetMemory(memorySize);

            fixed (char* pField = field.Buffer)
            {
                var address = memory.Address;
                var actualSize = Encodings.Utf8.GetBytes(pField + field.Offset, field.Length, address, memory.SizeInBytes);

                state.FindEscapePositionsIn(address, ref actualSize, escapePositionsSize);

                state.WriteEscapePositionsTo(address + actualSize);
                LazyStringValue result = longLived == false ? AllocateStringValue(field.Value, address, actualSize) : new LazyStringValue(field.Value, address, actualSize, this);
                result.AllocatedMemoryData = memory;

                if (state.EscapePositions.Count > 0)
                {
                    result.EscapePositions = state.EscapePositions.ToArray();
                }
                return result;
            }
        }

        public unsafe LazyStringValue GetLazyString(byte* ptr, int size, bool longLived = false)
        {
            var state = new JsonParserState();
            var maxByteCount = Encodings.Utf8.GetMaxByteCount(size);

            int escapePositionsSize = JsonParserState.FindEscapePositionsMaxSize(ptr, size, out _);

            int memorySize = maxByteCount + escapePositionsSize;
            var memory = longLived ? GetLongLivedMemory(memorySize) : GetMemory(memorySize);

            var address = memory.Address;

            Memory.Copy(address, ptr, size);

            state.FindEscapePositionsIn(address, ref size, escapePositionsSize);

            state.WriteEscapePositionsTo(address + size);
            LazyStringValue result = longLived == false ? AllocateStringValue(null, address, size) : new LazyStringValue(null, address, size, this);
            result.AllocatedMemoryData = memory;

            if (state.EscapePositions.Count > 0)
            {
                result.EscapePositions = state.EscapePositions.ToArray();
            }
            return result;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe LazyStringValue GetLazyStringValue(byte* ptr)
        {
            // See format of the lazy string ID in the GetLowerIdSliceAndStorageKey method
            var size = BlittableJsonReaderBase.ReadVariableSizeInt(ptr, 0, out var offset);
            return AllocateStringValue(null, ptr + offset, size);
        }

        public BlittableJsonReaderObject ReadForDisk(Stream stream, string documentId)
        {
            return ParseToMemory(stream, documentId, BlittableJsonDocumentBuilder.UsageMode.ToDisk);
        }

        public ValueTask<BlittableJsonReaderObject> ReadForDiskAsync(Stream stream, string documentId, CancellationToken? token = null)
        {
            return ParseToMemoryAsync(stream, documentId, BlittableJsonDocumentBuilder.UsageMode.ToDisk, token);
        }

        public ValueTask<BlittableJsonReaderObject> ReadForMemoryAsync(Stream stream, string documentId, CancellationToken? token = null)
        {
            return ParseToMemoryAsync(stream, documentId, BlittableJsonDocumentBuilder.UsageMode.None, token);
        }

        public BlittableJsonReaderObject ReadForMemory(Stream stream, string documentId)
        {
            return ParseToMemory(stream, documentId, BlittableJsonDocumentBuilder.UsageMode.None);
        }

        public unsafe BlittableJsonReaderObject ReadForMemory(string jsonString, string documentId)
        {
            // todo: maybe use ManagedPinnedBuffer here
            var maxByteSize = Encodings.Utf8.GetMaxByteCount(jsonString.Length);

            fixed (char* val = jsonString)
            {
                var buffer = ArrayPool<byte>.Shared.Rent(maxByteSize);
                try
                {
                    fixed (byte* buf = buffer)
                    {
                        Encodings.Utf8.GetBytes(val, jsonString.Length, buf, maxByteSize);
                        using (var ms = new MemoryStream(buffer))
                        {
                            return ReadForMemory(ms, documentId);
                        }
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }
        }

        public BlittableJsonReaderObject ReadObject(DynamicJsonValue builder, string documentId,
            BlittableJsonDocumentBuilder.UsageMode mode = BlittableJsonDocumentBuilder.UsageMode.None, IBlittableDocumentModifier modifier = null)
        {
            return ReadObjectInternal(builder, documentId, mode, modifier);
        }

        public BlittableJsonReaderObject ReadObject(BlittableJsonReaderObject obj, string documentId,
            BlittableJsonDocumentBuilder.UsageMode mode = BlittableJsonDocumentBuilder.UsageMode.None)
        {
            return ReadObjectInternal(obj, documentId, mode);
        }

        private BlittableJsonReaderObject ReadObjectInternal(object builder, string documentId,
            BlittableJsonDocumentBuilder.UsageMode mode, IBlittableDocumentModifier modifier = null)
        {
            _jsonParserState.Reset();
            _objectJsonParser.Reset(builder);
            _documentBuilder.Renew(documentId, mode);
            CachedProperties.NewDocument();
            _documentBuilder._modifier = modifier;
            _documentBuilder.ReadObjectDocument();
            if (_documentBuilder.Read() == false)
                throw new InvalidOperationException("Partial content in object json parser shouldn't happen");
            _documentBuilder.FinalizeDocument();

            _objectJsonParser.Reset(null);

            var reader = _documentBuilder.CreateReader();
            return reader;
        }

        public async Task<BlittableJsonReaderObject> ReadFromWebSocket(
            WebSocket webSocket,
            string debugTag,
            CancellationToken token)
        {
            if (Disposed)
                ThrowObjectDisposed();

            _jsonParserState.Reset();
            UnmanagedJsonParser parser = null;
            BlittableJsonDocumentBuilder builder = null;
            var managedBuffer = default(ReturnBuffer);
            var generation = _generation;

            try
            {
                parser = new UnmanagedJsonParser(this, _jsonParserState, debugTag);
                builder = new BlittableJsonDocumentBuilder(this,
                    BlittableJsonDocumentBuilder.UsageMode.None, debugTag, parser, _jsonParserState);
                managedBuffer = GetManagedBuffer(out var bytes);
                try
                {
                    builder.ReadObjectDocument();
                    var result = await webSocket.ReceiveAsync(bytes.Buffer, token).ConfigureAwait(false);

                    token.ThrowIfCancellationRequested();
                    EnsureNotDisposed();

                    if (result.MessageType == WebSocketMessageType.Close)
                        return null;
                    bytes.Valid = result.Count;
                    bytes.Used = 0;

                    parser.SetBuffer(bytes);
                    while (true)
                    {
                        var read = builder.Read();
                        bytes.Used += parser.BufferOffset;
                        if (read)
                            break;
                        result = await webSocket.ReceiveAsync(bytes.Buffer, token).ConfigureAwait(false);
                        token.ThrowIfCancellationRequested();
                        EnsureNotDisposed();
                        bytes.Valid = result.Count;
                        bytes.Used = 0;
                        parser.SetBuffer(bytes);
                    }
                    builder.FinalizeDocument();
                    return builder.CreateReader();
                }
                catch (Exception)
                {
                    builder.Dispose();
                    throw;
                }
            }
            finally
            {
                DisposeIfNeeded(generation, parser, builder);
                if (generation == _generation)
                    managedBuffer.Dispose();
            }
        }

        public BlittableJsonReaderObject Read(Stream stream, string documentId, IBlittableDocumentModifier modifier = null)
        {
            var state = BlittableJsonDocumentBuilder.UsageMode.ToDisk;
            return ParseToMemory(stream, documentId, state, modifier);
        }

        private BlittableJsonReaderObject ParseToMemory(Stream stream, string debugTag, BlittableJsonDocumentBuilder.UsageMode mode, IBlittableDocumentModifier modifier = null)
        {
            using (GetManagedBuffer(out var bytes))
                return ParseToMemory(stream, debugTag, mode, bytes, modifier);
        }

        public BlittableJsonReaderObject ParseToMemory(Stream stream, string debugTag,
            BlittableJsonDocumentBuilder.UsageMode mode,
            ManagedPinnedBuffer bytes, IBlittableDocumentModifier modifier = null)
        {
            EnsureNotDisposed();

            _jsonParserState.Reset();
            using (var parser = new UnmanagedJsonParser(this, _jsonParserState, debugTag))
            using (var builder = new BlittableJsonDocumentBuilder(this, mode, debugTag, parser, _jsonParserState, modifier: modifier))
            {
                CachedProperties.NewDocument();
                builder.ReadObjectDocument();
                while (true)
                {
                    if (bytes.Valid == bytes.Used)
                    {
                        var read = stream.Read(bytes.Buffer.Array, bytes.Buffer.Offset, bytes.Length);
                        EnsureNotDisposed();
                        if (read == 0)
                            throw new EndOfStreamException("Stream ended without reaching end of json content");
                        bytes.Valid = read;
                        bytes.Used = 0;
                    }
                    parser.SetBuffer(bytes);
                    var result = builder.Read();
                    bytes.Used += parser.BufferOffset;
                    if (result)
                        break;
                }
                builder.FinalizeDocument();

                var reader = builder.CreateReader();
                return reader;
            }
        }

        public unsafe BlittableJsonReaderObject ParseBuffer(byte* buffer, int length, string debugTag,
            BlittableJsonDocumentBuilder.UsageMode mode, IBlittableDocumentModifier modifier = null)
        {
            EnsureNotDisposed();

            _jsonParserState.Reset();
            using (var parser = new UnmanagedJsonParser(this, _jsonParserState, debugTag))
            using (var builder = new BlittableJsonDocumentBuilder(this, mode, debugTag, parser, _jsonParserState, modifier: modifier))
            {
                CachedProperties.NewDocument();
                builder.ReadObjectDocument();
                parser.SetBuffer(buffer, length);

                if (builder.Read() == false)
                    throw new EndOfStreamException("Buffer ended without reaching end of json content");

                builder.FinalizeDocument();

                var reader = builder.CreateReader();
                return reader;
            }
        }

        public unsafe BlittableJsonReaderArray ParseBufferToArray(string value, string debugTag,
            BlittableJsonDocumentBuilder.UsageMode mode, IBlittableDocumentModifier modifier = null)
        {
            EnsureNotDisposed();

            _jsonParserState.Reset();
            using (var parser = new UnmanagedJsonParser(this, _jsonParserState, debugTag))
            using (var builder = new BlittableJsonDocumentBuilder(this, mode, debugTag, parser, _jsonParserState, modifier: modifier))
            using (GetManagedBuffer(out var buffer))
            {
                CachedProperties.NewDocument();
                builder.ReadArrayDocument();

                var maxChars = buffer.Length / 8; //utf8 max size is 8 bytes, must consider worst case possiable

                bool lastReadResult = false;
                for (int i = 0; i < value.Length; i += maxChars)
                {
                    var charsToRead = Math.Min(value.Length - i, maxChars);
                    var length = Encodings.Utf8.GetBytes(value, i,
                        charsToRead,
                        buffer.Buffer.Array,
                        buffer.Buffer.Offset);

                    parser.SetBuffer(buffer.Pointer, length);
                    lastReadResult = builder.Read();
                }
                if (lastReadResult == false)
                    throw new EndOfStreamException("Buffer ended without reaching end of json content");

                builder.FinalizeDocument();

                var reader = builder.CreateArrayReader(false);
                return reader;
            }
        }

        public async ValueTask<BlittableJsonReaderObject> ParseToMemoryAsync(WebSocket webSocket, string debugTag,
           BlittableJsonDocumentBuilder.UsageMode mode,
           ManagedPinnedBuffer bytes,
           CancellationToken token = default(CancellationToken)
           )
        {
            EnsureNotDisposed();

            _jsonParserState.Reset();
            UnmanagedJsonParser parser = null;
            BlittableJsonDocumentBuilder builder = null;
            var generation = _generation;
            try
            {
                parser = new UnmanagedJsonParser(this, _jsonParserState, debugTag);
                builder = new BlittableJsonDocumentBuilder(this, mode, debugTag, parser, _jsonParserState);
                CachedProperties.NewDocument();
                builder.ReadObjectDocument();
                while (true)
                {
                    if (bytes.Valid == bytes.Used)
                    {
                        var read = await webSocket.ReceiveAsync(bytes.Buffer, token).ConfigureAwait(false);

                        EnsureNotDisposed();

                        if (read.Count == 0)
                            throw new EndOfStreamException("Stream ended without reaching end of json content");
                        bytes.Valid = read.Count;
                        bytes.Used = 0;
                    }
                    parser.SetBuffer(bytes);
                    var result = builder.Read();
                    bytes.Used += parser.BufferOffset;
                    if (result)
                        break;
                }
                builder.FinalizeDocument();

                var reader = builder.CreateReader();
                return reader;
            }
            finally
            {
                DisposeIfNeeded(generation, parser, builder);
            }
        }

        private void EnsureNotDisposed()
        {
            if (Disposed)
                ThrowObjectDisposed();
        }

        private async ValueTask<BlittableJsonReaderObject> ParseToMemoryAsync(Stream stream, string documentId, BlittableJsonDocumentBuilder.UsageMode mode, CancellationToken? token = null)
        {
            using (GetManagedBuffer(out ManagedPinnedBuffer bytes))
                return await ParseToMemoryAsync(stream, documentId, mode, bytes, token).ConfigureAwait(false);
        }

        public async ValueTask<BlittableJsonReaderObject> ParseToMemoryAsync(Stream stream, string documentId, BlittableJsonDocumentBuilder.UsageMode mode, ManagedPinnedBuffer bytes,
            CancellationToken? token = null,
            int maxSize = int.MaxValue)
        {
            EnsureNotDisposed();

            _jsonParserState.Reset();
            UnmanagedJsonParser parser = null;
            BlittableJsonDocumentBuilder builder = null;
            var generation = _generation;
            var streamDisposer = token?.Register(stream.Dispose);
            try
            {
                parser = new UnmanagedJsonParser(this, _jsonParserState, documentId);
                builder = new BlittableJsonDocumentBuilder(this, mode, documentId, parser, _jsonParserState);

                CachedProperties.NewDocument();
                builder.ReadObjectDocument();
                while (true)
                {
                    token?.ThrowIfCancellationRequested();
                    if (bytes.Valid == bytes.Used)
                    {
                        var read = token.HasValue
                            ? await stream.ReadAsync(bytes.Buffer.Array, bytes.Buffer.Offset, bytes.Length, token.Value).ConfigureAwait(false)
                            : await stream.ReadAsync(bytes.Buffer.Array, bytes.Buffer.Offset, bytes.Length).ConfigureAwait(false);

                        EnsureNotDisposed();

                        if (read == 0)
                            throw new EndOfStreamException("Stream ended without reaching end of json content");
                        bytes.Valid = read;
                        bytes.Used = 0;
                        maxSize -= read;
                        if (maxSize < 0)
                            throw new ArgumentException($"The maximum size allowed for {documentId} ({maxSize}) has been exceeded, aborting");
                    }
                    parser.SetBuffer(bytes);
                    var result = builder.Read();
                    bytes.Used += parser.BufferOffset;
                    if (result)
                        break;
                }
                builder.FinalizeDocument();

                var reader = builder.CreateReader();
                return reader;
            }
            finally
            {
                streamDisposer?.Dispose();
                DisposeIfNeeded(generation, parser, builder);
            }
        }

        private void DisposeIfNeeded(int generation, UnmanagedJsonParser parser, BlittableJsonDocumentBuilder builder)
        {
            // if the generation has changed, that means that we had reset the context
            // this can happen if we were waiting on an async call for a while, got timed out / error / something
            // and the context was reset before we got back from the async call
            // since the full context was reset, there is no point in trying to dispose things, they were already
            // taken care of
            if (generation == _generation)
            {
                parser?.Dispose();
                builder?.Dispose();
            }
        }

        private void ThrowObjectDisposed()
        {
            throw new ObjectDisposedException(nameof(JsonOperationContext));
        }

        protected internal virtual void Renew()
        {
            if (Disposed)
                ThrowObjectDisposed();

            _arenaAllocator.RenewArena();
            if (_arenaAllocatorForLongLivedValues == null)
            {
                _arenaAllocatorForLongLivedValues = new ArenaMemoryAllocator(LowMemoryFlag, _longLivedSize);
                CachedProperties = new CachedProperties(this);
            }
        }

        protected internal virtual unsafe void Reset(bool forceReleaseLongLivedAllocator = false, bool releaseAllocatedStringValues = false)
        {
            if (_tempBuffer != null && _tempBuffer.Address != null)
            {
                _arenaAllocator.Return(_tempBuffer);
                _tempBuffer = null;
            }

            _documentBuilder.Reset();

            // We don't reset _arenaAllocatorForLongLivedValues. It's used as a cache buffer for long lived strings like field names.
            // When a context is re-used, the buffer containing those field names was not reset and the strings are still valid and alive.

            var allocatorForLongLivedValues = _arenaAllocatorForLongLivedValues;
            if (allocatorForLongLivedValues != null &&
                (allocatorForLongLivedValues.Allocated > _initialSize || forceReleaseLongLivedAllocator))
            {
                foreach (var mem in _fieldNames.Values)
                {
                    _arenaAllocatorForLongLivedValues.Return(mem.AllocatedMemoryData);
                    mem.AllocatedMemoryData = null;
                    mem.IsDisposed = true;
                }

                _arenaAllocatorForLongLivedValues = null;
                // at this point, the long lived section is far too large, this is something that can happen
                // if we have dynamic properties. A back of the envelope calculation gives us roughly 32K
                // property names before this kicks in, which is a true abuse of the system. In this case,
                // in order to avoid unlimited growth, we'll reset the long lived section
                allocatorForLongLivedValues.Dispose();

                _fieldNames.Clear();
                CachedProperties = null; // need to release this so can be collected
            }

            for (var i = 0; i < _numberOfAllocatedStringsValues; i++)
                _allocateStringValues[i].Reset();

            _numberOfAllocatedStringsValues = 0;

            if (releaseAllocatedStringValues && _allocateStringValues.Count > 0)
            {
                var now = DateTime.UtcNow;
                if (now - _lastAllocatedStringValueTime >= _allocatedStringValuesCheckInterval)
                {
                    _lastAllocatedStringValueTime = now;
                    var halfOfAllocatedStringValues = _allocateStringValues.Count / 2;
                    if (_highestNumberOfReusedAllocatedStringsValuesInCurrentInterval <= halfOfAllocatedStringValues)
                        _allocateStringValues.Trim(halfOfAllocatedStringValues);

                    _highestNumberOfReusedAllocatedStringsValuesInCurrentInterval = 0;
                }
            }

            _objectJsonParser.Reset(null);
            _arenaAllocator.ResetArena();

            _generation++;

            if (_pooledArrays != null)
            {
                foreach (var pooledTypesKVP in _pooledArrays)
                {
                    foreach (var pooledArraysOfCurrentType in pooledTypesKVP.Value.Array)
                    {
                        pooledTypesKVP.Value.Releaser(pooledArraysOfCurrentType);
                    }
                }

                _pooledArrays = null;
            }

            ClearUnreturnedPathCache();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ClearUnreturnedPathCache()
        {
            for (var i = _numberOfAllocatedPathCaches + 1; i < _allocatePathCaches.Length - 1; i++)
            {
                var cache = _allocatePathCaches[i];

                //never allocated, no reason to continue seeking
                if (cache.Path == null)
                    break;

                //idly there shouldn't be unreleased path cache but we do have placed where we don't dispose of blittable object readers
                //and rely on the context.Reset to clear unwanted memory, but it didn't take care of the path cache.

                //Clear references for allocated cache paths so the GC can collect them.
                cache.ByIndex.Clear();
                cache.Path.Clear();
            }
        }

        public void Write(Stream stream, BlittableJsonReaderObject json)
        {
            EnsureNotDisposed();
            using (var writer = new BlittableJsonTextWriter(this, stream))
            {
                writer.WriteObject(json);
            }
        }

        public void Write(AbstractBlittableJsonTextWriter writer, BlittableJsonReaderObject json)
        {
            EnsureNotDisposed();
            WriteInternal(writer, json);
        }

        private void WriteInternal(AbstractBlittableJsonTextWriter writer, object json)
        {
            _jsonParserState.Reset();
            _objectJsonParser.Reset(json);

            _objectJsonParser.Read();

            WriteObject(writer, _jsonParserState, _objectJsonParser);

            _objectJsonParser.Reset(null);
        }

        public void Write(AbstractBlittableJsonTextWriter writer, DynamicJsonValue json)
        {
            EnsureNotDisposed();
            WriteInternal(writer, json);
        }

        public void Write(AbstractBlittableJsonTextWriter writer, DynamicJsonArray json)
        {
            EnsureNotDisposed();
            _jsonParserState.Reset();
            _objectJsonParser.Reset(json);

            _objectJsonParser.Read();

            WriteArray(writer, _jsonParserState, _objectJsonParser);

            _objectJsonParser.Reset(null);
        }

        public unsafe void WriteObject(AbstractBlittableJsonTextWriter writer, JsonParserState state, ObjectJsonParser parser)
        {
            EnsureNotDisposed();
            if (state.CurrentTokenType != JsonParserToken.StartObject)
                throw new InvalidOperationException("StartObject expected, but got " + state.CurrentTokenType);

            writer.WriteStartObject();
            bool first = true;
            while (true)
            {
                if (parser.Read() == false)
                    throw new InvalidOperationException("Object json parser can't return partial results");
                if (state.CurrentTokenType == JsonParserToken.EndObject)
                    break;

                if (state.CurrentTokenType != JsonParserToken.String)
                    throw new InvalidOperationException("Property expected, but got " + state.CurrentTokenType);

                if (first == false)
                    writer.WriteComma();
                first = false;

                var lazyStringValue = AllocateStringValue(null, state.StringBuffer, state.StringSize);
                writer.WritePropertyName(lazyStringValue);

                if (parser.Read() == false)
                    throw new InvalidOperationException("Object json parser can't return partial results");

                WriteValue(writer, state, parser);
            }
            writer.WriteEndObject();
        }

        private unsafe void WriteValue(AbstractBlittableJsonTextWriter writer, JsonParserState state, ObjectJsonParser parser)
        {
            switch (state.CurrentTokenType)
            {
                case JsonParserToken.Null:
                    writer.WriteNull();
                    break;
                case JsonParserToken.False:
                    writer.WriteBool(false);
                    break;
                case JsonParserToken.True:
                    writer.WriteBool(true);
                    break;
                case JsonParserToken.String:
                    if (state.CompressedSize.HasValue)
                    {
                        var lazyCompressedStringValue = new LazyCompressedStringValue(null, state.StringBuffer,
                            state.StringSize, state.CompressedSize.Value, this);
                        writer.WriteString(lazyCompressedStringValue);
                    }
                    else
                    {
                        writer.WriteString(AllocateStringValue(null, state.StringBuffer, state.StringSize));
                    }
                    break;
                case JsonParserToken.Float:
                    writer.WriteDouble(new LazyNumberValue(AllocateStringValue(null, state.StringBuffer, state.StringSize)));
                    break;
                case JsonParserToken.Integer:
                    writer.WriteInteger(state.Long);
                    break;
                case JsonParserToken.StartObject:
                    WriteObject(writer, state, parser);
                    break;
                case JsonParserToken.StartArray:
                    WriteArray(writer, state, parser);
                    break;
                default:
                    throw new ArgumentOutOfRangeException("Could not understand " + state.CurrentTokenType);
            }
        }

        public void WriteArray(AbstractBlittableJsonTextWriter writer, JsonParserState state, ObjectJsonParser parser)
        {
            EnsureNotDisposed();
            if (state.CurrentTokenType != JsonParserToken.StartArray)
                throw new InvalidOperationException("StartArray expected, but got " + state.CurrentTokenType);

            writer.WriteStartArray();
            bool first = true;
            while (true)
            {
                if (parser.Read() == false)
                    throw new InvalidOperationException("Object json parser can't return partial results");

                if (state.CurrentTokenType == JsonParserToken.EndArray)
                    break;

                if (first == false)
                    writer.WriteComma();
                first = false;

                WriteValue(writer, state, parser);
            }
            writer.WriteEndArray();
        }

        public bool GrowAllocation(AllocatedMemoryData allocation, int sizeIncrease)
        {
            EnsureNotDisposed();
            return _arenaAllocator.GrowAllocation(allocation, sizeIncrease);
        }

        public MemoryStream CheckoutMemoryStream()
        {
            EnsureNotDisposed();
            if (_cachedMemoryStreams.Count == 0)
            {
                return new MemoryStream();
            }

            var stream = _cachedMemoryStreams.Pop();
            _sizeOfMemoryStreamCache -= stream.Capacity;

            return stream;
        }

        private const long MemoryStreamCacheThreshold = Constants.Size.Megabyte;
        private const int MemoryStreamCacheMaxCapacityInBytes = 64 * Constants.Size.Megabyte;

        private long _sizeOfMemoryStreamCache;

        public void ReturnMemoryStream(MemoryStream stream)
        {
            //We don't want to hold big streams in the cache or have too big of a cache
            if (stream.Capacity > MemoryStreamCacheThreshold || _sizeOfMemoryStreamCache >= MemoryStreamCacheMaxCapacityInBytes)
            {
                return;
            }

            EnsureNotDisposed();

            stream.SetLength(0);
            _cachedMemoryStreams.Push(stream);
            _sizeOfMemoryStreamCache += stream.Capacity;
        }

        public void ReturnMemory(AllocatedMemoryData allocation)
        {
            EnsureNotDisposed();
            if (_generation != allocation.ContextGeneration)
                ThrowUseAfterFree(allocation);

            _arenaAllocator.Return(allocation);
        }

        private static void ThrowUseAfterFree(AllocatedMemoryData allocation)
        {
#if MEM_GUARD_STACK || TRACK_ALLOCATED_MEMORY_DATA
            throw new InvalidOperationException(
                $"UseAfterFree detected! Attempt to return memory from previous generation, Reset has already been called and the memory reused! Allocated by: {allocation.AllocatedBy}. Thread name: {Thread.CurrentThread.Name}");
#else
            throw new InvalidOperationException(
                $"UseAfterFree detected! Attempt to return memory from previous generation, Reset has already been called and the memory reused! Thread name: {Thread.CurrentThread.Name}");
#endif
        }

        public AvoidOverAllocationScope AvoidOverAllocation()
        {
            EnsureNotDisposed();
            _arenaAllocator.AvoidOverAllocation = true;
            return new AvoidOverAllocationScope(this);
        }

        public struct AvoidOverAllocationScope : IDisposable
        {
            private JsonOperationContext _parent;

            public AvoidOverAllocationScope(JsonOperationContext parent)
            {
                _parent = parent;
            }

            public void Dispose()
            {
                _parent._arenaAllocator.AvoidOverAllocation = false;
            }
        }

        private Dictionary<Type, (Action<Array> Releaser, List<Array> Array)> _pooledArrays = null;

        public T[] AllocatePooledArray<T>(int size)
        {
            if (_pooledArrays == null)
                _pooledArrays = new Dictionary<Type, (Action<Array> Releaser, List<Array> Array)>();

            if (_pooledArrays.TryGetValue(typeof(T), out var allocationsArray) == false)
            {
                void Releaser(Array x) => ArrayPool<T>.Shared.Return((T[])x, true);

                allocationsArray = (Releaser, new List<Array>());
                _pooledArrays[typeof(T)] = allocationsArray;
            }

            var allocatedArray = ArrayPool<T>.Shared.Rent(size);
            allocationsArray.Array.Add(allocatedArray);
            return allocatedArray;
        }

#if DEBUG || VALIDATE

        private class IntReference
        {
            public long Value;
        }

        private IntReference _threadId = new IntReference { Value = 0 };

        private class SingleThreadAccessAssertion : IDisposable
        {
            private readonly IntReference _capturedThreadId;
            private int _currentThreadId;
            private string _method;

            public SingleThreadAccessAssertion(IntReference expectedCapturedThread, string method)
            {
                _capturedThreadId = expectedCapturedThread;
                _currentThreadId = Environment.CurrentManagedThreadId;
                _method = method;
                if (Interlocked.CompareExchange(ref expectedCapturedThread.Value, _currentThreadId, 0) != 0)
                {
                    throw new InvalidOperationException($"Concurrent access to JsonOperationContext.{method} method detected");
                }
            }

            public void Dispose()
            {
                if (Interlocked.CompareExchange(ref _capturedThreadId.Value, 0, _currentThreadId) != _currentThreadId)
                {
                    throw new InvalidOperationException($"Concurrent access to JsonOperationContext.{_method} method detected");
                }
            }
        }

#endif
    }
}
