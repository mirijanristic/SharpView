using System.Runtime.CompilerServices;
using Vortice.Direct3D12;

namespace SharpView.Core;

/// <summary>
/// A texture mid-upload: destination resource created, upload buffer allocated,
/// mapped and (by the caller) filled — everything a WORKER thread may do.
/// D3D12 resource creation and Map are free-threaded, so the entire expensive
/// part of an upload (allocation + the full-image pixel write) happens off the
/// render thread; <see cref="TextureUploader.FinishUpload"/> then only records
/// the GPU copy — microseconds instead of a tens-of-milliseconds memcpy.
/// The mapped memory is WRITE-COMBINED: write rows sequentially, never read it.
/// </summary>
sealed class StagingTexture
{
    public required ID3D12Resource Texture { get; init; }      // created in CopyDest state
    public required ID3D12Resource UploadBuffer { get; init; } // mapped until Finish/Abandon
    public required IntPtr Mapped { get; init; }
    public required uint RowPitch { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required PlacedSubresourceFootPrint Footprint { get; init; }

    /// <summary>Destroy a staging pair whose GPU copy was never recorded. The
    /// GPU has never seen either resource, so direct disposal is safe from any
    /// thread (ID3D12 resource release is free-threaded) — no fence tagging.</summary>
    public void Abandon()
    {
        UploadBuffer.Unmap(0u);
        UploadBuffer.Dispose();
        Texture.Dispose();
    }
}

/// <summary>
/// Texture upload split into thread-friendly halves: <see cref="PrepareStaging"/>
/// (any thread — allocate + map), the caller's pixel write into
/// <see cref="StagingTexture.Mapped"/> (any thread), and <see cref="FinishUpload"/>
/// (render thread — records copy + barrier + SRV into the frame's command list).
/// The one-shot <see cref="Upload"/> convenience keeps the old byte[] path for
/// small textures (thumbnails), built from the same halves.
/// </summary>
static unsafe class TextureUploader
{
    /// <summary>
    /// Create the destination texture and a mapped upload buffer for it.
    /// Callable from ANY thread. The buffer is allocated as RowPitch × height —
    /// slightly larger than the true copyable size (whose final row is
    /// unpadded), so "RowPitch × height" is an honest capacity to hand to row
    /// writers like WIC's CopyPixels.
    /// </summary>
    public static StagingTexture PrepareStaging(DeviceResources res, int width, int height)
    {
        var texDesc = ResourceDescription.Texture2D(
            DeviceResources.TextureFormat, (uint)width, (uint)height, 1, 1);

        var texture = res.Device.CreateCommittedResource(
            new HeapProperties(HeapType.Default), HeapFlags.None,
            texDesc, ResourceStates.CopyDest);

        var layouts = new PlacedSubresourceFootPrint[1];
        var numRows = new uint[1];
        var rowSizes = new ulong[1];
        res.Device.GetCopyableFootprints(texDesc, 0, 1, 0, layouts, numRows, rowSizes, out _);
        var footprint = layouts[0];

        ulong bufferSize = (ulong)footprint.Footprint.RowPitch * (uint)height;
        var uploadBuf = res.Device.CreateCommittedResource(
            new HeapProperties(HeapType.Upload), HeapFlags.None,
            ResourceDescription.Buffer(bufferSize),
            ResourceStates.GenericRead);

        void* mapped;
        uploadBuf.Map(0u, &mapped);

        return new StagingTexture
        {
            Texture = texture,
            UploadBuffer = uploadBuf,
            Mapped = (IntPtr)mapped,
            RowPitch = footprint.Footprint.RowPitch,
            Width = width,
            Height = height,
            Footprint = footprint,
        };
    }

    /// <summary>Row-copy tightly packed BGRA pixels into a mapped staging buffer,
    /// honoring the GPU row pitch. Callable from any thread.</summary>
    public static void CopyRows(byte[] pixels, int width, int height,
                                IntPtr destination, uint rowPitch)
    {
        int tightPitch = width * 4;
        fixed (byte* srcPtr = pixels)
        {
            byte* dstPtr = (byte*)destination;
            if (rowPitch == (uint)tightPitch)
            {
                // Tightly packed on both sides (width multiple of 64 px) — one big copy.
                Unsafe.CopyBlock(dstPtr, srcPtr, (uint)(tightPitch * height));
            }
            else
            {
                for (int y = 0; y < height; y++)
                {
                    Unsafe.CopyBlock(
                        dstPtr + y * (long)rowPitch,
                        srcPtr + y * (long)tightPitch,
                        (uint)tightPitch);
                }
            }
        }
    }

    /// <summary>
    /// Record the GPU side of a prepared-and-filled staging texture into the
    /// frame's command list (render thread only): unmap, copy, barrier to
    /// PixelShaderResource, SRV creation. No GPU wait is required: draws
    /// recorded after the copy on the same queue see the finished texture, and
    /// the staging buffer is released via fence-tagged
    /// <see cref="DeviceResources.DeferRelease"/>. Returns the live texture.
    /// </summary>
    public static ID3D12Resource FinishUpload(
        DeviceResources res, StagingTexture staging, int srvSlot,
        ID3D12GraphicsCommandList cmdList)
    {
        staging.UploadBuffer.Unmap(0u);

        cmdList.CopyTextureRegion(
            new TextureCopyLocation(staging.Texture, 0), 0, 0, 0,
            new TextureCopyLocation(staging.UploadBuffer, staging.Footprint));
        cmdList.ResourceBarrierTransition(staging.Texture,
            ResourceStates.CopyDest, ResourceStates.PixelShaderResource);

        // The staging buffer must outlive the command list execution.
        // DeviceResources releases it once the next fence signal completes (no stall).
        res.DeferRelease(staging.UploadBuffer);

        res.Device.CreateShaderResourceView(staging.Texture,
            new ShaderResourceViewDescription
            {
                Format = DeviceResources.TextureFormat,
                ViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension.Texture2D,
                Shader4ComponentMapping = ShaderComponentMapping.Default,
                Texture2D = new Texture2DShaderResourceView { MipLevels = 1 },
            },
            res.GetSrvCpuHandle(srvSlot));

        return staging.Texture;
    }

    /// <summary>
    /// One-shot upload of decoded 32bpp BGRA pixels to a new GPU texture (the
    /// thumbnail path). Must be called on the render thread with
    /// <paramref name="cmdList"/> in the recording state; composed from the
    /// same prepare / fill / finish halves the main-image path uses.
    /// </summary>
    public static ID3D12Resource Upload(
        DeviceResources res,
        int width, int height, byte[] pixels,
        int srvSlot,
        ID3D12GraphicsCommandList cmdList)
    {
        var staging = PrepareStaging(res, width, height);
        CopyRows(pixels, width, height, staging.Mapped, staging.RowPitch);
        return FinishUpload(res, staging, srvSlot, cmdList);
    }
}
