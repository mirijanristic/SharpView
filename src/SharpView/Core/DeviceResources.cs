using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Vortice;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.Direct3D12.Debug;
using Vortice.DXGI;
using Vortice.Mathematics;
using Format = Vortice.DXGI.Format;

namespace SharpView.Core;

/// <summary>
/// Manages all Direct3D 12 device resources: device, swap chain, command infrastructure,
/// pipeline state, descriptor heaps, vertex buffer, and constant buffer.
/// </summary>
/// <remarks>
/// Threading model: all members must be called from the render thread unless noted otherwise.
/// Up to <see cref="FrameCount"/> frames may be in flight on the GPU, which is why the
/// constant buffer is versioned per frame (see <see cref="WriteConstants"/>).
/// </remarks>
sealed unsafe class DeviceResources : IDisposable
{
    public const int FrameCount = 2;
    public const Format BackBufferFormat = Format.R8G8B8A8_UNorm;
    /// <summary>Format of decoded image/thumbnail textures. BGRA matches GDI+'s native
    /// 32bpp memory layout, so decoded pixels are uploaded with straight memcpy —
    /// no per-pixel channel swizzle on the CPU.</summary>
    public const Format TextureFormat = Format.B8G8R8A8_UNorm;
    public const int MaxSrvSlots = 256;
    const int MaxCbSlots = 64;
    const int CbSlotSize = 256; // constant buffer views must be 256-byte aligned

    // Device objects
    public ID3D12Device2 Device { get; private set; } = null!;
    public ID3D12CommandQueue CommandQueue { get; private set; } = null!;
    public IDXGISwapChain3 SwapChain { get; private set; } = null!;

    // Frame resources
    readonly ID3D12Resource[] _renderTargets = new ID3D12Resource[FrameCount];
    readonly ID3D12CommandAllocator[] _cmdAllocators = new ID3D12CommandAllocator[FrameCount];
    ID3D12GraphicsCommandList _cmdList = null!;
    public ID3D12GraphicsCommandList CommandList => _cmdList;

    // Heaps
    ID3D12DescriptorHeap _rtvHeap = null!;
    ID3D12DescriptorHeap _srvHeap = null!;
    int _rtvDescSize;
    int _srvDescSize;

    // Fence
    ID3D12Fence _fence = null!;
    readonly ulong[] _fenceValues = new ulong[FrameCount];
    ulong _currentFenceValue = 1;
    AutoResetEvent _fenceEvent = null!;
    int _frameIndex;

    // Constant buffer (one region of MaxCbSlots per frame in flight)
    ID3D12Resource _constantBuffer = null!;
    byte* _cbMapped;

    // Pipeline
    public ID3D12RootSignature RootSignature { get; private set; } = null!;
    public ID3D12PipelineState Pso { get; private set; } = null!;
    ID3D12Resource _vertexBuffer = null!;
    VertexBufferView _vbView;

    // 1x1 white texture for solid-color rendering
    ID3D12Resource _whiteTexture = null!;
    int _whiteSrvSlot;

    // SRV slot allocator
    readonly bool[] _srvSlotUsed = new bool[MaxSrvSlots];

    // Resources (and SRV slots) that may still be referenced by in-flight GPU work.
    // Each entry is tagged with the next fence value to be signaled; it is released
    // once the GPU has passed that fence (checked in BeginFrame / WaitForGpu), so
    // steady-state cleanup never blocks the CPU on the GPU.
    readonly Queue<(ulong Fence, ID3D12Resource? Resource, int SrvSlot)> _deferred = new();

    bool _disposed;

    public int FrameIndex => _frameIndex;
    public int SrvDescSize => _srvDescSize;
    public int WhiteSrvSlot => _whiteSrvSlot;

    public void Init(IntPtr hwnd, int width, int height)
    {
#if DEBUG
        if (D3D12.D3D12GetDebugInterface(out ID3D12Debug? dbg).Success)
        { dbg!.EnableDebugLayer(); dbg.Dispose(); }
#endif
        using var factory = DXGI.CreateDXGIFactory2<IDXGIFactory4>(false);

        ID3D12Device2? device = null;
        for (uint i = 0; factory.EnumAdapters1(i, out var adapter).Success; i++)
        {
            var desc = adapter.Description1;
            if ((desc.Flags & AdapterFlags.Software) != 0) { adapter.Dispose(); continue; }
            if (D3D12.D3D12CreateDevice(adapter, FeatureLevel.Level_12_0, out device).Success)
            { adapter.Dispose(); break; }
            adapter.Dispose();
        }
        Device = device ?? D3D12.D3D12CreateDevice<ID3D12Device2>(null, FeatureLevel.Level_12_0);

        CommandQueue = Device.CreateCommandQueue(new CommandQueueDescription(CommandListType.Direct));

        var sd = new SwapChainDescription1
        {
            Width = (uint)width,
            Height = (uint)height,
            Format = BackBufferFormat,
            BufferCount = FrameCount,
            BufferUsage = Usage.RenderTargetOutput,
            SwapEffect = SwapEffect.FlipDiscard,
            SampleDescription = new SampleDescription(1, 0),
            Flags = SwapChainFlags.AllowTearing,
        };

        using var tmp = factory.CreateSwapChainForHwnd(CommandQueue, hwnd, sd);
        factory.MakeWindowAssociation(hwnd, WindowAssociationFlags.IgnoreAltEnter);
        SwapChain = tmp.QueryInterface<IDXGISwapChain3>();
        _frameIndex = (int)SwapChain.CurrentBackBufferIndex;

        // RTV heap
        _rtvHeap = Device.CreateDescriptorHeap(new DescriptorHeapDescription(
            DescriptorHeapType.RenderTargetView, FrameCount));
        _rtvDescSize = (int)Device.GetDescriptorHandleIncrementSize(DescriptorHeapType.RenderTargetView);

        // SRV heap (shader visible, large enough for main image + thumbnails)
        _srvHeap = Device.CreateDescriptorHeap(new DescriptorHeapDescription(
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView,
            MaxSrvSlots, DescriptorHeapFlags.ShaderVisible));
        _srvDescSize = (int)Device.GetDescriptorHandleIncrementSize(
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);

        CreateRenderTargets();

        for (int i = 0; i < FrameCount; i++)
            _cmdAllocators[i] = Device.CreateCommandAllocator(CommandListType.Direct);

        _cmdList = Device.CreateCommandList<ID3D12GraphicsCommandList>(
            0, CommandListType.Direct, _cmdAllocators[0]);
        _cmdList.Close();

        _fence = Device.CreateFence(0);
        _fenceEvent = new AutoResetEvent(false);

        CreatePipeline();
        CreateVertexBuffer();
        CreateConstantBuffer();
        CreateWhiteTexture();
    }

    void CreateRenderTargets()
    {
        var handle = _rtvHeap.GetCPUDescriptorHandleForHeapStart();
        for (int i = 0; i < FrameCount; i++)
        {
            _renderTargets[i]?.Dispose();
            _renderTargets[i] = SwapChain.GetBuffer<ID3D12Resource>((uint)i);
            Device.CreateRenderTargetView(_renderTargets[i], null, handle);
            handle += _rtvDescSize;
        }
    }

    void CreatePipeline()
    {
        var rootParams = new RootParameter1[]
        {
            // Param 0: SRV descriptor table (texture)
            new(new RootDescriptorTable1(
                new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 1, 0)),
                ShaderVisibility.Pixel),
            // Param 1: Root CBV (constant buffer - can change address per draw)
            new(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0),
                ShaderVisibility.All),
        };

        var sampler = new StaticSamplerDescription(
            ShaderVisibility.Pixel, 0, 0)
        {
            Filter = Filter.Anisotropic,
            MaxAnisotropy = 16,
            AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp,
            ComparisonFunction = ComparisonFunction.Never,
            MinLOD = 0,
            MaxLOD = float.MaxValue,
        };

        var rsDesc = new VersionedRootSignatureDescription(
            new RootSignatureDescription1(
                RootSignatureFlags.AllowInputAssemblerInputLayout,
                rootParams, new[] { sampler }));

        RootSignature = Device.CreateRootSignature(rsDesc);

        Compiler.Compile(Shaders.HlslSource, "VSMain", "main", "vs_5_1", out var vsBlob, out var vsErr);
        Compiler.Compile(Shaders.HlslSource, "PSMain", "main", "ps_5_1", out var psBlob, out var psErr);
        if (vsBlob == null) throw new InvalidOperationException($"VS error: {vsErr?.AsString()}");
        if (psBlob == null) throw new InvalidOperationException($"PS error: {psErr?.AsString()}");

        ReadOnlyMemory<byte> vsBytes = BlobToBytes(vsBlob);
        ReadOnlyMemory<byte> psBytes = BlobToBytes(psBlob);

        var inputLayout = new InputElementDescription[]
        {
            new("POSITION", 0, Format.R32G32_Float, 0, 0, InputClassification.PerVertexData, 0),
            new("TEXCOORD", 0, Format.R32G32_Float, 8, 0, InputClassification.PerVertexData, 0),
        };

        var psoDesc = new GraphicsPipelineStateDescription
        {
            RootSignature = RootSignature,
            VertexShader = vsBytes,
            PixelShader = psBytes,
            InputLayout = new InputLayoutDescription(inputLayout),
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
            RasterizerState = RasterizerDescription.CullNone,
            BlendState = BlendDescription.AlphaBlend,
            DepthStencilState = DepthStencilDescription.None,
            SampleMask = uint.MaxValue,
            RenderTargetFormats = new[] { BackBufferFormat },
            SampleDescription = new SampleDescription(1, 0),
        };

        Pso = Device.CreateGraphicsPipelineState(psoDesc);
        vsBlob.Dispose();
        psBlob.Dispose();
    }

    static byte[] BlobToBytes(Blob blob)
    {
        int size = (int)(nuint)blob.BufferSize;
        byte[] bytes = new byte[size];
        Marshal.Copy(blob.BufferPointer, bytes, 0, size);
        return bytes;
    }

    void CreateVertexBuffer()
    {
        Vertex[] verts =
        {
            new(-1, -1, 0, 1),
            new(-1,  1, 0, 0),
            new( 1, -1, 1, 1),
            new( 1,  1, 1, 0),
        };

        int size = Unsafe.SizeOf<Vertex>() * verts.Length;

        _vertexBuffer = Device.CreateCommittedResource(
            new HeapProperties(HeapType.Upload), HeapFlags.None,
            ResourceDescription.Buffer((ulong)size),
            ResourceStates.GenericRead);

        void* ptr;
        _vertexBuffer.Map(0u, &ptr);
        fixed (Vertex* src = verts)
            Unsafe.CopyBlock(ptr, src, (uint)size);
        _vertexBuffer.Unmap(0u);

        _vbView = new VertexBufferView(
            _vertexBuffer.GPUVirtualAddress,
            (uint)size, (uint)Unsafe.SizeOf<Vertex>());
    }

    void CreateConstantBuffer()
    {
        // One full region of MaxCbSlots per frame in flight. Without this, the CPU
        // would overwrite constants that the previous (still executing) frame reads.
        int totalSize = CbSlotSize * MaxCbSlots * FrameCount;

        _constantBuffer = Device.CreateCommittedResource(
            new HeapProperties(HeapType.Upload), HeapFlags.None,
            ResourceDescription.Buffer((ulong)totalSize),
            ResourceStates.GenericRead);

        void* ptr;
        _constantBuffer.Map(0u, &ptr);
        _cbMapped = (byte*)ptr;
    }

    void CreateWhiteTexture()
    {
        _whiteSrvSlot = AllocateSrvSlot();
        byte[] white = { 255, 255, 255, 255 };

        var texDesc = ResourceDescription.Texture2D(BackBufferFormat, 1, 1, 1, 1);
        _whiteTexture = Device.CreateCommittedResource(
            new HeapProperties(HeapType.Default), HeapFlags.None,
            texDesc, ResourceStates.CopyDest);

        var layouts = new PlacedSubresourceFootPrint[1];
        var numRows = new uint[1];
        var rowSizes = new ulong[1];
        Device.GetCopyableFootprints(texDesc, 0, 1, 0, layouts, numRows, rowSizes, out ulong uploadSize);

        using var uploadBuf = Device.CreateCommittedResource(
            new HeapProperties(HeapType.Upload), HeapFlags.None,
            ResourceDescription.Buffer(uploadSize),
            ResourceStates.GenericRead);

        void* uploadPtr;
        uploadBuf.Map(0u, &uploadPtr);
        fixed (byte* src = white)
            Unsafe.CopyBlock(uploadPtr, src, 4);
        uploadBuf.Unmap(0u);

        // One-time upload during init; the shared command list is idle here.
        _cmdAllocators[0].Reset();
        _cmdList.Reset(_cmdAllocators[0]);
        _cmdList.CopyTextureRegion(
            new TextureCopyLocation(_whiteTexture, 0), 0, 0, 0,
            new TextureCopyLocation(uploadBuf, layouts[0]));
        _cmdList.ResourceBarrierTransition(_whiteTexture,
            ResourceStates.CopyDest, ResourceStates.PixelShaderResource);
        _cmdList.Close();
        CommandQueue.ExecuteCommandList(_cmdList);
        WaitForGpu();

        Device.CreateShaderResourceView(_whiteTexture,
            new ShaderResourceViewDescription
            {
                Format = BackBufferFormat,
                ViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension.Texture2D,
                Shader4ComponentMapping = ShaderComponentMapping.Default,
                Texture2D = new Texture2DShaderResourceView { MipLevels = 1 },
            },
            GetSrvCpuHandle(_whiteSrvSlot));
    }

    // ─── SRV Slot Allocator ───────────────────────────────────────────

    public int AllocateSrvSlot()
    {
        for (int i = 0; i < MaxSrvSlots; i++)
        {
            if (!_srvSlotUsed[i])
            {
                _srvSlotUsed[i] = true;
                return i;
            }
        }
        throw new InvalidOperationException("Out of SRV slots");
    }

    public void FreeSrvSlot(int slot)
    {
        if (slot >= 0 && slot < MaxSrvSlots)
            _srvSlotUsed[slot] = false;
    }

    public CpuDescriptorHandle GetSrvCpuHandle(int slot)
        => _srvHeap.GetCPUDescriptorHandleForHeapStart() + slot * _srvDescSize;

    public GpuDescriptorHandle GetSrvGpuHandle(int slot)
        => _srvHeap.GetGPUDescriptorHandleForHeapStart() + slot * _srvDescSize;

    // ─── Deferred Disposal ────────────────────────────────────────────

    /// <summary>
    /// Schedule a GPU resource (and optionally its SRV slot) for release once the GPU
    /// has finished all work submitted up to the next fence signal. Unlike a full
    /// <see cref="WaitForGpu"/>, this never blocks — completed entries are reclaimed
    /// at the start of each frame. Render thread only.
    /// </summary>
    public void DeferRelease(ID3D12Resource? resource, int srvSlot = -1)
        => _deferred.Enqueue((_currentFenceValue, resource, srvSlot));

    /// <summary>Dispose deferred resources whose fence the GPU has already passed.
    /// Fence tags are monotonically increasing, so the queue front is always the oldest.</summary>
    void ReleaseCompleted()
    {
        if (_deferred.Count == 0) return;

        ulong completed = _fence.CompletedValue;
        while (_deferred.Count > 0 && _deferred.Peek().Fence <= completed)
        {
            var (_, resource, srvSlot) = _deferred.Dequeue();
            resource?.Dispose();
            if (srvSlot >= 0) FreeSrvSlot(srvSlot);
        }
    }

    // ─── Constant Buffer ──────────────────────────────────────────────

    int CbByteOffset(int slot) => (_frameIndex * MaxCbSlots + slot) * CbSlotSize;

    /// <summary>
    /// Write per-draw constants into the current frame's constant buffer region.
    /// Safe to call between EndFrame (which advances the frame index) and the
    /// next EndFrame, i.e. anywhere in the per-frame Update/Render code.
    /// </summary>
    public void WriteConstants(int slot, in ViewConstants data)
    {
        Unsafe.CopyBlock(_cbMapped + CbByteOffset(slot),
            Unsafe.AsPointer(ref Unsafe.AsRef(in data)),
            (uint)Unsafe.SizeOf<ViewConstants>());
    }

    public ulong GetCbGpuAddress(int slot)
        => _constantBuffer.GPUVirtualAddress + (ulong)CbByteOffset(slot);

    // ─── Frame Methods ────────────────────────────────────────────────

    public void BeginFrame()
    {
        // Reclaim resources whose GPU work already completed — free, no stall.
        ReleaseCompleted();

        var alloc = _cmdAllocators[_frameIndex];
        alloc.Reset();
        _cmdList.Reset(alloc, Pso);

        _cmdList.ResourceBarrierTransition(_renderTargets[_frameIndex],
            ResourceStates.Present, ResourceStates.RenderTarget);

        var rtv = _rtvHeap.GetCPUDescriptorHandleForHeapStart() + _frameIndex * _rtvDescSize;
        _cmdList.OMSetRenderTargets(rtv);
        _cmdList.ClearRenderTargetView(rtv, new Color4(0.07f, 0.07f, 0.07f, 1f));

        _cmdList.SetGraphicsRootSignature(RootSignature);
        _cmdList.SetDescriptorHeaps(1, new[] { _srvHeap });
        _cmdList.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);
        _cmdList.IASetVertexBuffers(0, _vbView);
    }

    public void EndFrame()
    {
        _cmdList.ResourceBarrierTransition(_renderTargets[_frameIndex],
            ResourceStates.RenderTarget, ResourceStates.Present);

        _cmdList.Close();
        CommandQueue.ExecuteCommandList(_cmdList);
        SwapChain.Present(1, PresentFlags.None);

        ulong fv = _currentFenceValue++;
        CommandQueue.Signal(_fence, fv);
        _fenceValues[_frameIndex] = fv;
        _frameIndex = (int)SwapChain.CurrentBackBufferIndex;

        if (_fence.CompletedValue < _fenceValues[_frameIndex])
        {
            _fence.SetEventOnCompletion(_fenceValues[_frameIndex], _fenceEvent);
            _fenceEvent.WaitOne();
        }
    }

    /// <summary>Bind texture + constants and draw the fullscreen quad.</summary>
    public void DrawQuad(int srvSlot, int cbSlot)
    {
        _cmdList.SetGraphicsRootDescriptorTable(0, GetSrvGpuHandle(srvSlot));
        _cmdList.SetGraphicsRootConstantBufferView(1, GetCbGpuAddress(cbSlot));
        _cmdList.DrawInstanced(4, 1, 0, 0);
    }

    public void SetViewportAndScissor(float x, float y, float w, float h)
    {
        _cmdList.RSSetViewport(new Viewport(x, y, w, h, 0f, 1f));
        _cmdList.RSSetScissorRect(new RawRect((int)x, (int)y, (int)(x + w), (int)(y + h)));
    }

    // ─── Resize ───────────────────────────────────────────────────────

    public void Resize(int width, int height)
    {
        if (width <= 0 || height <= 0) return;
        WaitForGpu();

        for (int i = 0; i < FrameCount; i++)
        { _renderTargets[i]?.Dispose(); _renderTargets[i] = null!; }

        SwapChain.ResizeBuffers(FrameCount, (uint)width, (uint)height,
            BackBufferFormat, SwapChainFlags.AllowTearing);
        _frameIndex = (int)SwapChain.CurrentBackBufferIndex;
        CreateRenderTargets();
    }

    /// <summary>
    /// Block until the GPU has finished all submitted work, then release
    /// any resources scheduled via <see cref="DeferRelease"/> (after a full wait,
    /// every deferred entry is guaranteed complete).
    /// </summary>
    public void WaitForGpu()
    {
        ulong v = _currentFenceValue++;
        CommandQueue.Signal(_fence, v);
        _fence.SetEventOnCompletion(v, _fenceEvent);
        _fenceEvent.WaitOne();

        ReleaseCompleted();
    }

    // ─── Dispose ──────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        WaitForGpu();

        _constantBuffer?.Unmap(0);
        _constantBuffer?.Dispose();
        _vertexBuffer?.Dispose();
        _whiteTexture?.Dispose();
        Pso?.Dispose();
        RootSignature?.Dispose();
        _cmdList?.Dispose();
        for (int i = 0; i < FrameCount; i++)
        { _cmdAllocators[i]?.Dispose(); _renderTargets[i]?.Dispose(); }
        _fence?.Dispose();
        _fenceEvent?.Dispose();
        _srvHeap?.Dispose();
        _rtvHeap?.Dispose();
        SwapChain?.Dispose();
        CommandQueue?.Dispose();
        Device?.Dispose();
    }
}
