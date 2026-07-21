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
}

/// <summary>
/// Manages async thumbnail decoding on background threads and caching of GPU textures,
/// with LRU eviction to bound GPU memory usage.
/// </summary>
sealed class ThumbnailCache : IDisposable
{
    const int ThumbnailMaxDim = 80;
    const int MaxCached = 120;
    const int MaxUploadsPerFrame = 4;

    readonly DeviceResources _res;
    readonly ConcurrentQueue<ThumbnailData> _pendingQueue = new();
    readonly Dictionary<string, CachedThumbnail> _cache = new();
    readonly LinkedList<string> _lruOrder = new(); // O(n) Remove is fine for MaxCached=120
    readonly HashSet<string> _loadingSet = new();
    readonly object _lock = new();
    CancellationTokenSource _cts = new();

    public ThumbnailCache(DeviceResources res)
    {
        _res = res;
    }

    /// <summary>True if decoded thumbnails are waiting to be uploaded to the GPU.</summary>
    public bool HasPendingUploads => !_pendingQueue.IsEmpty;

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
                    var pixels = ImageDecoder.DecodeToRgba(path, out int w, out int h,
                        ThumbnailMaxDim, lowQuality: true);
                    if (!ct.IsCancellationRequested)
                        _pendingQueue.Enqueue(new ThumbnailData(path, w, h, pixels));
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
    /// Upload pending decoded thumbnails to the GPU. Must be called on the render thread
    /// with <paramref name="cmdList"/> freshly reset (recording, nothing recorded yet).
    /// Returns true if any upload commands were recorded — the caller must then execute
    /// the command list and call <see cref="DeviceResources.WaitForGpu"/>.
    /// </summary>
    public bool ProcessUploads(ID3D12GraphicsCommandList cmdList)
    {
        // Take this frame's batch first so eviction (which may block on the GPU)
        // happens strictly BEFORE any upload commands are recorded. Evicting mid-batch
        // would sync the GPU and release staging buffers the open command list still uses.
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
                _lruOrder.AddFirst(data.FilePath);
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
                _lruOrder.Remove(path);
                _lruOrder.AddFirst(path);
                return cached;
            }
        }
        return null;
    }

    /// <summary>
    /// Evict least-recently-used thumbnails until there is room for
    /// <paramref name="incomingCount"/> new entries. Performs a full GPU sync before
    /// disposing, since evicted textures may still be referenced by an in-flight frame.
    /// Render thread only; must run before recording into this frame's command list.
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

        _res.WaitForGpu();
        foreach (var cached in evicted)
        {
            cached.Texture.Dispose();
            _res.FreeSrvSlot(cached.SrvSlot);
        }
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
                // Deferred: released at the next full GPU sync (WaitForGpu),
                // which DeviceResources.Dispose also performs.
                _res.DeferDisposal(kv.Value.Texture);
                _res.FreeSrvSlot(kv.Value.SrvSlot);
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
