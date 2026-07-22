using System.Collections.Concurrent;
using Vortice.Direct3D12;
using SharpView.Core;

namespace SharpView.Services;

/// <summary>Holds decoded thumbnail pixel data waiting for GPU upload.</summary>
sealed record ThumbnailData(string FilePath, int Width, int Height, byte[] Pixels);

/// <summary>A thumbnail that lives on the GPU and can be drawn.</summary>
sealed class CachedThumbnail
{
    public ID3D12Resource Texture { get; init; } = null!;
    public int SrvSlot { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }

    /// <summary>Node in the LRU list — stored here so move-to-front is O(1).</summary>
    public LinkedListNode<string> LruNode { get; set; } = null!;
}

/// <summary>
/// Manages async thumbnail decoding on background threads and caching of GPU textures,
/// with LRU eviction to bound GPU memory usage.
/// </summary>
sealed class ThumbnailCache : IDisposable
{
    /// <summary>Side of the square thumbnails this cache produces, in pixels. The
    /// decoder delivers exactly this size (cover-scaled + center-cropped with
    /// high-quality filtering) and the strip draws it 1:1, so the GPU never
    /// resamples a thumbnail — that is what keeps them sharp.</summary>
    public const int ThumbnailSize = 55;
    const int MaxCached = 120;
    const int MaxUploadsPerFrame = 4;

    readonly DeviceResources _res;
    readonly ConcurrentQueue<ThumbnailData> _pendingQueue = new();
    readonly Dictionary<string, CachedThumbnail> _cache = new();
    readonly LinkedList<string> _lruOrder = new(); // O(1) moves via CachedThumbnail.LruNode
    readonly HashSet<string> _loadingSet = new();
    readonly object _lock = new();
    CancellationTokenSource _cts = new();

    public ThumbnailCache(DeviceResources res)
    {
        _res = res;
    }

    /// <summary>True if decoded thumbnails are waiting to be uploaded to the GPU.</summary>
    public bool HasPendingUploads => !_pendingQueue.IsEmpty;

    /// <summary>True while any thumbnail work (decode or upload) is still outstanding.
    /// Used by the render loop to keep drawing until the strip is fully populated.</summary>
    public bool IsBusy
    {
        get
        {
            if (!_pendingQueue.IsEmpty) return true;
            lock (_lock) return _loadingSet.Count > 0;
        }
    }

    /// <summary>Request thumbnails for the given file paths. Skips already cached/loading entries.</summary>
    public void RequestThumbnails(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            lock (_lock)
            {
                if (_cache.ContainsKey(path) || _loadingSet.Contains(path))
                    continue;
                _loadingSet.Add(path);
            }

            var ct = _cts.Token;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    if (ct.IsCancellationRequested) return;
                    var pixels = ImageDecoder.DecodeSquareBgra(path, ThumbnailSize);
                    if (!ct.IsCancellationRequested)
                        _pendingQueue.Enqueue(new ThumbnailData(path, ThumbnailSize, ThumbnailSize, pixels));
                }
                catch
                {
                    // Decode failed — allow future retries.
                    lock (_lock) _loadingSet.Remove(path);
                }
            });
        }
    }

    /// <summary>
    /// Upload pending decoded thumbnails to the GPU. Must be called on the render
    /// thread with <paramref name="cmdList"/> in the recording state — typically the
    /// frame's own command list, right after BeginFrame and before any draws. No GPU
    /// wait is required: eviction and staging-buffer cleanup use fence-tagged
    /// <see cref="DeviceResources.DeferRelease"/>, so this never stalls the pipeline.
    /// </summary>
    public bool ProcessUploads(ID3D12GraphicsCommandList cmdList)
    {
        var batch = new List<ThumbnailData>(MaxUploadsPerFrame);
        while (batch.Count < MaxUploadsPerFrame && _pendingQueue.TryDequeue(out var data))
            batch.Add(data);

        if (batch.Count == 0) return false;

        EnsureCapacity(batch.Count);

        int uploaded = 0;
        foreach (var data in batch)
        {
            bool alreadyCached;
            lock (_lock)
            {
                _loadingSet.Remove(data.FilePath);
                alreadyCached = _cache.ContainsKey(data.FilePath);
            }
            if (alreadyCached) continue;

            int srvSlot = _res.AllocateSrvSlot();
            var texture = TextureUploader.Upload(
                _res, data.Width, data.Height, data.Pixels, srvSlot, cmdList);

            var cached = new CachedThumbnail
            {
                Texture = texture,
                SrvSlot = srvSlot,
                Width = data.Width,
                Height = data.Height,
            };

            lock (_lock)
            {
                _cache[data.FilePath] = cached;
                cached.LruNode = _lruOrder.AddFirst(data.FilePath);
            }

            uploaded++;
        }
        return uploaded > 0;
    }

    /// <summary>Get a cached thumbnail, or null if not loaded yet. Marks it most-recently-used.</summary>
    public CachedThumbnail? Get(string path)
    {
        lock (_lock)
        {
            if (_cache.TryGetValue(path, out var cached))
            {
                // O(1) move-to-front — no linear search through the LRU list.
                _lruOrder.Remove(cached.LruNode);
                _lruOrder.AddFirst(cached.LruNode);
                return cached;
            }
        }
        return null;
    }

    /// <summary>
    /// Evict least-recently-used thumbnails until there is room for
    /// <paramref name="incomingCount"/> new entries. Evicted textures may still be
    /// referenced by an in-flight frame, so they (and their SRV slots) are released
    /// via fence-tagged deferral instead of a blocking GPU sync. Render thread only.
    /// </summary>
    void EnsureCapacity(int incomingCount)
    {
        List<CachedThumbnail>? evicted = null;

        lock (_lock)
        {
            while (_cache.Count + incomingCount > MaxCached && _lruOrder.Last is not null)
            {
                string oldest = _lruOrder.Last.Value;
                _lruOrder.RemoveLast();

                if (_cache.Remove(oldest, out var cached))
                    (evicted ??= new List<CachedThumbnail>()).Add(cached);
            }
        }

        if (evicted is null) return;

        foreach (var cached in evicted)
            _res.DeferRelease(cached.Texture, cached.SrvSlot);
    }

    /// <summary>Clear all cached thumbnails and cancel pending loads.</summary>
    public void Clear()
    {
        _cts.Cancel();
        _cts.Dispose();
        _cts = new CancellationTokenSource();

        while (_pendingQueue.TryDequeue(out _)) { }

        lock (_lock)
        {
            foreach (var kv in _cache)
            {
                // Fence-tagged deferral: texture AND SRV slot are reclaimed only once
                // the GPU passes the next fence (WaitForGpu / DeviceResources.Dispose
                // also release everything remaining).
                _res.DeferRelease(kv.Value.Texture, kv.Value.SrvSlot);
            }
            _cache.Clear();
            _lruOrder.Clear();
            _loadingSet.Clear();
        }
    }

    public void Dispose()
    {
        Clear();
        _cts.Cancel();
        _cts.Dispose();
    }
}
