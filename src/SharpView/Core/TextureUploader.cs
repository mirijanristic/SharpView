using System.Runtime.CompilerServices;
using Vortice.Direct3D12;

namespace SharpView.Core;

/// <summary>
/// Records a texture upload (staging buffer copy + barrier + SRV creation).
/// </summary>
static unsafe class TextureUploader
{
    /// <summary>
    /// Uploads decoded 32bpp BGRA pixels to a new GPU texture. Must be called on the
    /// render thread with <paramref name="cmdList"/> in the recording state. No GPU
    /// wait is required: draws recorded after the copy in the same command list (or in
    /// later lists on the same queue) are guaranteed to see the finished texture, and
    /// the staging buffer is released via <see cref="DeviceResources.DeferRelease"/>
    /// once the next fence signal completes.
    /// </summary>
    public static ID3D12Resource Upload(
        DeviceResources res,
        int width, int height, byte[] pixels,
        int srvSlot,
        ID3D12GraphicsCommandList cmdList)
    {
        int rowPitch = width * 4;

        var texDesc = ResourceDescription.Texture2D(
            DeviceResources.TextureFormat, (uint)width, (uint)height, 1, 1);

        var texture = res.Device.CreateCommittedResource(
            new HeapProperties(HeapType.Default), HeapFlags.None,
            texDesc, ResourceStates.CopyDest);

        var layouts = new PlacedSubresourceFootPrint[1];
        var numRows = new uint[1];
        var rowSizes = new ulong[1];
        res.Device.GetCopyableFootprints(texDesc, 0, 1, 0, layouts, numRows, rowSizes, out ulong uploadSize);
        var footprint = layouts[0];

        var uploadBuf = res.Device.CreateCommittedResource(
            new HeapProperties(HeapType.Upload), HeapFlags.None,
            ResourceDescription.Buffer(uploadSize),
            ResourceStates.GenericRead);

        void* uploadPtr;
        uploadBuf.Map(0u, &uploadPtr);
        byte* dstPtr = (byte*)uploadPtr;

        fixed (byte* srcPtr = pixels)
        {
            uint gpuRowPitch = footprint.Footprint.RowPitch;
            if (gpuRowPitch == (uint)rowPitch)
            {
                // Tightly packed on both sides (width multiple of 64 px) — one big copy.
                Unsafe.CopyBlock(dstPtr, srcPtr, (uint)(rowPitch * height));
            }
            else
            {
                for (int y = 0; y < height; y++)
                {
                    Unsafe.CopyBlock(
                        dstPtr + y * (long)gpuRowPitch,
                        srcPtr + y * (long)rowPitch,
                        (uint)rowPitch);
                }
            }
        }
        uploadBuf.Unmap(0u);

        cmdList.CopyTextureRegion(
            new TextureCopyLocation(texture, 0), 0, 0, 0,
            new TextureCopyLocation(uploadBuf, footprint));
        cmdList.ResourceBarrierTransition(texture,
            ResourceStates.CopyDest, ResourceStates.PixelShaderResource);

        // The staging buffer must outlive the command list execution.
        // DeviceResources releases it once the next fence signal completes (no stall).
        res.DeferRelease(uploadBuf);

        res.Device.CreateShaderResourceView(texture,
            new ShaderResourceViewDescription
            {
                Format = DeviceResources.TextureFormat,
                ViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension.Texture2D,
                Shader4ComponentMapping = ShaderComponentMapping.Default,
                Texture2D = new Texture2DShaderResourceView { MipLevels = 1 },
            },
            res.GetSrvCpuHandle(srvSlot));

        return texture;
    }
}
