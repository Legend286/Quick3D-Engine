// SPDX-License-Identifier: MIT
// HelloTrianglePass: a single RenderPass that owns its shader + pipeline +
// buffers (created at construction, disposed on Dispose) and reads the
// triangle geometry from the scene's EcsWorld instead of a hardcoded fallback.
//
// The EcsWorld is consulted once for the first entity that carries a
// TriangleComponent; if none exists, a hardcoded triangle keeps the hello-
// triangle path alive without crashing.

using System.IO;
using Engine.RenderGraph;
using Engine.RHI;
using Engine.Scene;

namespace Engine.Game;

public sealed class HelloTrianglePass : RenderPass, IDisposable
{
    private readonly RhiDevice _device;
    private readonly IEntityStore _world;
    private readonly ScenePass _scenePass;
    private readonly Scene _scene;
    private readonly string _contentRoot;

    private readonly RhiShader _vs;
    private readonly RhiShader _fs;
    private readonly RhiPipeline _pipeline;
    private RhiBuffer _posBuffer;
    private RhiBuffer _colBuffer;
    private MeshData _triangle;
    private bool _resourcesReady;

    public HelloTrianglePass(RhiDevice device, IEntityStore world,
                              Scene scene, ScenePass scenePass, string contentRoot)
    {
        _device      = device;
        _world       = world;
        _scene       = scene;
        _scenePass   = scenePass;
        _contentRoot = contentRoot;
        Name         = scenePass.Name;

        string src = LoadShaderSource(_scenePass.ShaderVertex);
        _vs = RhiShader.FromSource(_device, src, "triangle_vs");
        _fs = RhiShader.FromSource(_device, src, "triangle_fs");

        _pipeline = RhiPipeline.CreateGraphics(
            _device, _vs, _fs,
            RhiNative.TextureFormat.Bgra8Unorm,
            enableDepth: false);

        RebuildGeometry();
    }

    /// <summary>Pull the latest triangle-component data from the world and
    /// re-upload the vertex + color buffers. Re-creates the buffers if the
    /// new entity carries more (or fewer) vertices than the prior one.</summary>
    public void RebuildGeometry()
    {
        _triangle = QueryTriangleFromWorld() ?? FallbackMesh();
        ulong desiredPos = (ulong)(_triangle.Positions.Length * sizeof(float));
        ulong desiredCol = (ulong)(_triangle.Colors.Length * sizeof(float));

        if (_posBuffer is null)
        {
            _posBuffer = RhiBuffer.Create(_device, desiredPos, RhiNative.BufferUsage.Vertex);
            _colBuffer = RhiBuffer.Create(_device, desiredCol, RhiNative.BufferUsage.Vertex);
        }
        else
        {
            // If the entity grew beyond the original buffer we re-create.
            if (_posBuffer.Size < desiredPos)
            {
                _posBuffer.Dispose();
                _posBuffer = RhiBuffer.Create(_device, desiredPos, RhiNative.BufferUsage.Vertex);
            }
            if (_colBuffer.Size < desiredCol)
            {
                _colBuffer.Dispose();
                _colBuffer = RhiBuffer.Create(_device, desiredCol, RhiNative.BufferUsage.Vertex);
            }
        }
        _posBuffer.Upload<float>(_triangle.Positions);
        _colBuffer.Upload<float>(_triangle.Colors);
        _resourcesReady = true;
    }

    private MeshData? QueryTriangleFromWorld()
    {
        // EcsWorld.Query<T>() is a Phase-3 API; for MVP1 we walk the world via
        // IEntityStore.TryGet on every plausible entity id (sparse).
        for (uint id = 1; id < 1024; ++id)
        {
            if (_world.TryGet<TriangleComponent>(id, out var tri) && tri.Positions is not null)
            {
                return new MeshData
                {
                    Positions = tri.Positions,
                    Colors    = tri.Colors ?? tri.Positions,
                };
            }
        }
        return null;
    }

    private static MeshData FallbackMesh() => new()
    {
        Positions = new float[]
        {
             0.0f,  0.6f, 0.0f,
            -0.6f, -0.4f, 0.0f,
             0.6f, -0.4f, 0.0f,
        },
        Colors = new float[]
        {
            1.0f, 0.0f, 0.0f,
            0.0f, 1.0f, 0.0f,
            0.0f, 0.0f, 1.0f,
        },
    };

    public override void Setup(RenderGraphBuilder builder)
    {
        // Declare the back-buffer WRITE using the same sentinel handle the
        // Renderer uses to bind the swapchain. The barrier compiler treats
        // them as the same logical resource so any pre-pass transition into
        // RenderTarget lands in the right slot.
        builder.Write(Engine.Game.Renderer.BackBufferHandle,
                      ResourceState.RenderTarget);

        // Internal vertex buffer declaration so multi-pass graphs can reason
        // about transitions. Phase 3 will plant this with a stable handle.
        var posHandle = builder.CreateBuffer(new BufferDesc
        {
            Size  = (ulong)(_triangle.Positions.Length * sizeof(float)),
            Usage = RhiNative.BufferUsage.Vertex,
        });
        builder.Read(posHandle, ResourceState.ShaderRead);
    }

    public override void Execute(ICommandSink sink, RenderGraphContext context)
    {
        if (!_resourcesReady) RebuildGeometry();

        RhiTexture colorTarget = null!;
        bool found = false;
        foreach (var kv in context.Textures)
        {
            colorTarget = kv.Value;
            found = true;
            break;
        }
        if (!found) return;

        (uint w, uint h) = (1280u, 720u);

        sink.BeginRenderPass(colorTarget,
                             RhiNative.LoadOp.Clear,
                             RhiNative.StoreOp.Store,
                             depth: null);
        sink.BindPipeline(_pipeline);
        sink.SetViewport(0, 0, w, h);
        sink.BindVertexBuffer(0, _posBuffer);
        sink.BindVertexBuffer(1, _colBuffer);

        uint total = 0;
        foreach (var d in _scenePass.Draws) total += (uint)d.VertexCount;
        sink.Draw(total);
        sink.EndPass();
    }

    private string LoadShaderSource(string relPath)
    {
        string full = Path.Combine(_contentRoot, relPath);
        if (!File.Exists(full)) return DefaultShader();
        return File.ReadAllText(full);
    }

    private static string DefaultShader() =>
        "using namespace metal;\n" +
        "vertex float4 default_vs(uint vid [[vertex_id]])\n" +
        "{ return float4(0,0,0,1); }\n" +
        "fragment float4 default_fs(float4 p [[position]])\n" +
        "{ return float4(1,0,0,1); }\n";

    public void Dispose()
    {
        _posBuffer?.Dispose();
        _colBuffer?.Dispose();
        _pipeline?.Dispose();
        _vs?.Dispose();
        _fs?.Dispose();
    }
}

internal struct MeshData
{
    public float[] Positions;
    public float[] Colors;
}
