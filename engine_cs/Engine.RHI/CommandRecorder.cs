// SPDX-License-Identifier: MIT
// CommandRecorder: high-level ring API over the C command-list + encoder pair.
//
// Auto-submits on Dispose if the caller forgets to call Submit() explicitly.
// This prevents dropped MTLCommandBuffer handles (which would otherwise linger
// under ARC without ever being committed).

using System;
using Engine.CBindings;

namespace Engine.RHI;

public sealed class CommandRecorder : IDisposable
{
    private readonly RhiDevice _device;
    public IntPtr CmdList { get; }
    public IntPtr CurrentEncoder { get; private set; }
    private bool _submitted;

    public CommandRecorder(RhiDevice device)
    {
        _device = device;
        CmdList = RhiNative.RhiBeginCmdlist(device.Handle);
        if (CmdList == IntPtr.Zero)
            throw new InvalidOperationException("rhi_begin_cmdlist returned null");
    }

    public void BeginRenderPass(RhiTexture colorAttachment,
                                RhiNative.LoadOp loadOp = RhiNative.LoadOp.Clear,
                                RhiNative.StoreOp storeOp = RhiNative.StoreOp.Store,
                                RhiTexture? depth = null)
    {
        var color = new RhiNative.PassAttachment
        {
            Texture = colorAttachment.Handle,
            LoadOp = loadOp,
            StoreOp = storeOp,
        };
        RhiNative.PassAttachment[] colors = new[] { color };
        unsafe
        {
            // depthStruct + dPtr live on this unsafe block's stack so their
            // address is fixed for the duration of the RhiBeginRenderPass
            // synchronous call. Using a Nullable<T> + &.Value fails because
            // Nullable<T>.Value returns a copy.
            RhiNative.PassAttachment depthStruct = default;
            RhiNative.PassAttachment* dPtr = null;
            if (depth is not null)
            {
                depthStruct = new RhiNative.PassAttachment
                {
                    Texture = depth.Handle,
                    LoadOp = RhiNative.LoadOp.Clear,
                    StoreOp = RhiNative.StoreOp.Store,
                };
                dPtr = &depthStruct;
            }
            fixed (RhiNative.PassAttachment* p = colors)
            {
                var desc = new RhiNative.PassDesc
                {
                    Abi = 1,
                    ColorAttachments = (IntPtr)p,
                    ColorCount = (uint)colors.Length,
                    DepthAttachment = (IntPtr)dPtr,
                };
                CurrentEncoder = RhiNative.RhiBeginRenderPass(CmdList, in desc);
            }
        }
        if (CurrentEncoder == IntPtr.Zero)
            throw new InvalidOperationException("rhi_begin_render_pass returned null");
    }

    public void EndPass()
    {
        if (CurrentEncoder == IntPtr.Zero) return;
        RhiNative.RhiEndPass(CurrentEncoder);
        CurrentEncoder = IntPtr.Zero;
    }

    public void BindPipeline(RhiPipeline pipeline)
        => RhiNative.RhiCmdBindPipeline(CurrentEncoder, pipeline.Handle);

    public void BindVertexBuffer(uint slot, RhiBuffer buf, ulong offset = 0)
        => RhiNative.RhiCmdBindVertexBuffer(CurrentEncoder, slot, buf.Handle, offset);

    public void SetViewport(float x, float y, float w, float h,
                            float minDepth = 0, float maxDepth = 1)
        => RhiNative.RhiCmdSetViewport(CurrentEncoder, x, y, w, h, minDepth, maxDepth);

    public void Draw(uint vertexCount, uint instanceCount = 1,
                     uint firstVertex = 0, uint firstInstance = 0)
    {
        var args = new RhiNative.DrawArgs
        {
            Abi = 1,
            VertexCount = vertexCount,
            InstanceCount = instanceCount,
            FirstVertex = firstVertex,
            FirstInstance = firstInstance,
        };
        RhiNative.RhiCmdDraw(CurrentEncoder, in args);
    }

    public void Submit()
    {
        if (_submitted) return;
        if (CurrentEncoder != IntPtr.Zero)
        {
            RhiNative.RhiEndPass(CurrentEncoder);
            CurrentEncoder = IntPtr.Zero;
        }
        RhiNative.RhiSubmit(_device.Handle, CmdList);
        _submitted = true;
    }

    public void Dispose()
    {
        // Best-effort: if the caller never called Submit(), do it now. This
        // commits any pending commands and avoids the ARC-leak case where the
        // MTLCommandBuffer is retained but never executed.
        if (!_submitted)
        {
            try { Submit(); }
            catch (Exception ex)
            {
                Engine.CBindings.Log.Error($"[engine-rhi] auto-submit failed: {ex.Message}", "rhi");
            }
            _submitted = true;
        }
        GC.SuppressFinalize(this);
    }

    /// <summary>Safety net: if the caller forgets Dispose(), the finalizer
    /// thread still submits the MTLCommandBuffer before GC reclaims the
    /// recorded Rhi* handles. Targets the common LLM mistake of letting
    /// command buffers linger without commit.</summary>
    ~CommandRecorder() => Dispose();
}
