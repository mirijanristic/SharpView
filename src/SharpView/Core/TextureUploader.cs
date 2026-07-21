using System.Runtime.CompilerServices;
using Vortice.Direct3D12;

namespace SharpView.Core;

/// <summary>
/// Records a texture upload (staging buffer copy + barrier + SRV creation).
/// </summary>
static unsafe class TextureUploader
{
    /// <summary>
    /// Uploads decoded RGBA pixels to a new GPU texture. Must be called on the render
    /// thread with <paramref name="cmdList"/> in the recording state. The caller must
    /// execute the command list and call <see cref="DeviceResources.WaitForGpu"/> before
    /// the texture is sampled; that same call also releases the staging upload buffer,
    /// which this method schedules via <see cref="DeviceResources.DeferDisposal"/>.
    /// </summary>
    public static ID3D12Resource Upload(
        DeviceResources res,
        int width, int height, byte[] pixels,
        int srvSlot,
        ID3D12GraphicsCommandList cmdList)
    {
        int rowPitch = width * 4;

        var texDesc = ResourceDescription.Texture2D(
            DeviceResources.BackBufferFormat, (uint)width, (uint)height, 1, 1);

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
            for (int y = 0; y < height; y++)
            {
                Unsafe.CopyBlock(
                    dstPtr + y * (long)footprint.Footprint.RowPitch,
                    srcPtr + y * rowPitch,
                    (uint)rowPitch);
            }
        }
        uploadBuf.Unmap(0u);

        cmdList.CopyTextureRegion(
            new TextureCopyLocation(texture, 0), 0, 0, 0,
            new TextureCopyLocation(uploadBuf, footprint));
        cmdList.ResourceBarrierTransition(texture,
            ResourceStates.CopyDest, ResourceStates.PixelShaderResource);

        // The staging buffer must outlive the command list execution.
        // DeviceResources releases it after the next full GPU sync.
        res.DeferDisposal(uploadBuf);

        res.Device.CreateShaderResourceView(texture,
            new ShaderResourceViewDescription
            {
                Format = DeviceResources.BackBufferFormat,
                ViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension.Texture2D,
                Shader4ComponentMapping = ShaderComponentMapping.Default,
                Texture2D = new Texture2DShaderResourceView { MipLevels = 1 },
            },
            res.GetSrvCpuHandle(srvSlot));

        return texture;
    }
}
