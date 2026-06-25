// SPDX-License-Identifier: MIT
// HelloTrianglePass: a single RenderPass that owns its shader + pipeline +
// buffers (created at construction, disposed on Dispose) and reads the
// triangle geometry from the scene's EcsWorld instead of a hardcoded fallback.
//
// The EcsWorld is consulted once for the first entity that carries a
// TriangleComponent; if none exists, a hardcoded triangle keeps the hello-
// triangle path alive without crashing.

using System.IO;
using Engine.CBindings;
using Engine.RenderGraph;
using Engine.RHI;
using Engine.Scene;

namespace Engine.Game;

public sealed class HelloTrianglePass : RenderPass, IDisposable
{
    private readonly RhiDevice _device;
    private readonly IEntityStore _world;
    private readonly ScenePass _scenePass;
    private readonly SceneGraph _scene;
    private readonly string _contentRoot;

    private readonly RhiShader _vs;
    private readonly RhiShader _fs;
    private readonly RhiPipeline _pipeline;
    private RhiBuffer _posBuffer;
    private RhiBuffer _colBuffer;
    private MeshData _triangle;
    private bool _resourcesReady;
    private float _lastAspect;

    public HelloTrianglePass(RhiDevice device, IEntityStore world,
                              SceneGraph scene, ScenePass scenePass, string contentRoot)
    {
        _device = device;
        _world = world;
        _scene = scene;
        _scenePass = scenePass;
        _contentRoot = contentRoot;
        Name = scenePass.Name;

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

        float[] aspectPositions = new float[_triangle.Positions.Length];
        System.Array.Copy(_triangle.Positions, aspectPositions, _triangle.Positions.Length);

        float scaleX = 1.0f;
        float scaleY = 1.0f;
        float aspect = _lastAspect > 0.0f ? _lastAspect : 1.0f;
        if (aspect >= 1.0f)
        {
            scaleX = 1.0f / aspect;
        }
        else
        {
            scaleY = aspect;
        }

        for (int i = 0; i < aspectPositions.Length; i += 3)
        {
            aspectPositions[i] *= scaleX;
            aspectPositions[i + 1] *= scaleY;
        }

        ulong desiredPos = (ulong)(aspectPositions.Length * sizeof(float));
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
        _posBuffer.Upload<float>(aspectPositions);
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
                    Colors = tri.Colors ?? tri.Positions,
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
        builder.Write(Engine.Game.Renderer.BackBufferHandle,
                      ResourceState.RenderTarget);

        var posHandle = builder.CreateBuffer(new BufferDesc
        {
            Size = (ulong)(_triangle.Positions.Length * sizeof(float)),
            Usage = RhiNative.BufferUsage.Vertex,
        });
        builder.Read(posHandle, ResourceState.ShaderRead);
    }

    public override void Execute(ICommandSink sink, RenderGraphContext context)
    {
        // Bind the swapchain back-buffer explicitly by the sentinel handle
        // the Renderer uses. 
        if (!context.TryGetTexture(Engine.Game.Renderer.BackBufferHandle,
                                   out RhiTexture colorTarget))
            return;

        uint w = context.Width > 0 ? context.Width : 1280;
        uint h = context.Height > 0 ? context.Height : 720;

        float aspect = (float)w / h;
        if (!_resourcesReady || System.Math.Abs(aspect - _lastAspect) > 0.001f)
        {
            _lastAspect = aspect;
            RebuildGeometry();
        }

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
        "vertex float4 triangle_vs(uint vid [[vertex_id]])\n" +
        "{ return float4(0,0,0,1); }\n" +
        "fragment float4 triangle_fs(float4 p [[position]])\n" +
        "{ return float4(1,0,0,1); }\n";

    public void Dispose()
    {
        _posBuffer?.Dispose();
        _colBuffer?.Dispose();
        _pipeline?.Dispose();
        _vs?.Dispose();
        _fs?.Dispose();
        _posBuffer = null;
        _colBuffer = null;
    }

    /// <summary>Safety net: see <see cref="RhiBuffer"/>. Drops the partial
    /// RHI handle set if a constructor exception escaped without Dispose().
    /// </summary>
    ~HelloTrianglePass() => Dispose();
}

internal struct MeshData
{
    public float[] Positions;
    public float[] Colors;
}
