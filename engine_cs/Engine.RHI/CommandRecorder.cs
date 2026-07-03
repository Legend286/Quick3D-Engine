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
    public RhiNative.QueueType QueueType { get; }
    private bool _submitted;

    public CommandRecorder(RhiDevice device, RhiNative.QueueType queue = RhiNative.QueueType.Graphics)
    {
        _device = device;
        QueueType = queue;
        CmdList = RhiNative.RhiBeginCmdlist(device.Handle, queue);
        if (CmdList == IntPtr.Zero)
            throw new InvalidOperationException("rhi_begin_cmdlist returned null");
    }

    public void BeginRenderPass(RhiTexture colorAttachment,
                                RhiNative.LoadOp loadOp = RhiNative.LoadOp.Clear,
                                RhiNative.StoreOp storeOp = RhiNative.StoreOp.Store,
                                RhiTexture? depth = null,
                                RhiNative.LoadOp depthLoad = RhiNative.LoadOp.Clear,
                                RhiNative.StoreOp depthStore = RhiNative.StoreOp.Store)
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
                    LoadOp = depthLoad,
                    StoreOp = depthStore,
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

    public void BeginComputePass(string? name = null)
    {
        if (CurrentEncoder != IntPtr.Zero)
            throw new InvalidOperationException("Pass already active");

        IntPtr namePtr = name != null ? System.Runtime.InteropServices.Marshal.StringToHGlobalAnsi(name) : IntPtr.Zero;
        CurrentEncoder = RhiNative.RhiBeginComputePass(CmdList, namePtr);
        if (namePtr != IntPtr.Zero) System.Runtime.InteropServices.Marshal.FreeHGlobal(namePtr);
        if (CurrentEncoder == IntPtr.Zero)
            throw new InvalidOperationException("rhi_begin_compute_pass returned null");
    }

    public void EndComputePass()
    {
        EndPass();
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

    public void PushConstants(uint slot, uint size, IntPtr data)
        => RhiNative.RhiCmdPushConstants(CurrentEncoder, slot, size, data);


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

    public void DrawIndirect(RhiBuffer indirectBuffer, ulong offset, uint drawCount, uint stride)
    {
        var args = new RhiNative.DrawIndirectArgs
        {
            Abi = 1,
            IndirectBuffer = indirectBuffer.Handle,
            IndirectBufferOffset = offset,
            DrawCount = drawCount,
            Stride = stride,
        };
        RhiNative.RhiCmdDrawIndirect(CurrentEncoder, in args);
    }

    public void DrawIndexed(uint indexCount, uint instanceCount = 1,
                            uint firstIndex = 0, int vertexOffset = 0, uint firstInstance = 0)
    {
        var args = new RhiNative.DrawIndexedArgs
        {
            Abi = 1,
            IndexCount = indexCount,
            InstanceCount = instanceCount,
            FirstIndex = firstIndex,
            VertexOffset = vertexOffset,
            FirstInstance = firstInstance,
        };
        RhiNative.RhiCmdDrawIndexed(CurrentEncoder, in args);
    }

    public void DrawIndexedIndirect(RhiBuffer indirectBuffer, ulong offset, uint drawCount, uint stride)
    {
        var args = new RhiNative.DrawIndexedIndirectArgs
        {
            Abi = 1,
            IndirectBuffer = indirectBuffer.Handle,
            IndirectBufferOffset = offset,
            DrawCount = drawCount,
            Stride = stride,
        };
        RhiNative.RhiCmdDrawIndexedIndirect(CurrentEncoder, in args);
    }

    public void BindIndexBuffer(RhiBuffer buf, bool is32Bit = false, ulong offset = 0)
        => RhiNative.RhiCmdBindIndexBuffer(CurrentEncoder, buf.Handle, is32Bit ? 1 : 0, offset);

    public void BindTexture(uint slot, RhiTexture tex)
        => RhiNative.RhiCmdBindTexture(CurrentEncoder, slot, tex.Handle);

    public void BindTextureArray(uint slot, RhiTexture[] texs)
    {
        if (texs == null || texs.Length == 0) return;
        Span<IntPtr> handles = stackalloc IntPtr[texs.Length];
        for (int i = 0; i < texs.Length; i++)
            handles[i] = texs[i]?.Handle ?? IntPtr.Zero;
        RhiNative.RhiCmdBindTextureArray(CurrentEncoder, slot, ref handles[0], (uint)texs.Length);
    }

    public void BindSampler(uint slot, RhiSampler samp)
        => RhiNative.RhiCmdBindSampler(CurrentEncoder, slot, samp.Handle);

    public void UseBuffer(RhiBuffer buf, uint usage = 1 /* Read */)
        => RhiNative.RhiCmdUseBuffer(CurrentEncoder, buf.Handle, usage);

    public void SetScissor(uint x, uint y, uint w, uint h)
        => RhiNative.RhiCmdSetScissor(CurrentEncoder, x, y, w, h);

    public void PipelineBarrier(ReadOnlySpan<RhiNative.Barrier> barriers)
    {
        if (barriers.Length == 0) return;
        // End current encoder if active, barriers are recorded at the command-list level.
        if (CurrentEncoder != IntPtr.Zero)
        {
            RhiNative.RhiEndPass(CurrentEncoder);
            CurrentEncoder = IntPtr.Zero;
        }

        unsafe
        {
            fixed (RhiNative.Barrier* p = barriers)
            {
                RhiNative.RhiCmdPipelineBarrier(CmdList, (uint)barriers.Length, (IntPtr)p);
            }
        }
    }

    public void SignalFence(RhiFence fence, ulong value)
    {
        if (CurrentEncoder != IntPtr.Zero)
        {
            RhiNative.RhiEndPass(CurrentEncoder);
            CurrentEncoder = IntPtr.Zero;
        }
        RhiNative.RhiCmdSignalFence(CmdList, fence.Handle, value);
    }

    public void WaitFence(RhiFence fence, ulong value)
    {
        if (CurrentEncoder != IntPtr.Zero)
        {
            RhiNative.RhiEndPass(CurrentEncoder);
            CurrentEncoder = IntPtr.Zero;
        }
        RhiNative.RhiCmdWaitFence(CmdList, fence.Handle, value);
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

    public void Dispatch(uint groupsX, uint groupsY, uint groupsZ)
    {
        if (CurrentEncoder == IntPtr.Zero)
            throw new InvalidOperationException("No active pass");
        RhiNative.RhiCmdDispatch(CurrentEncoder, groupsX, groupsY, groupsZ);
    }
}
