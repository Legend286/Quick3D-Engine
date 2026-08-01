// SPDX-License-Identifier: MIT

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Engine.CBindings;
using Engine.RHI;
using Engine.Scene;

namespace Engine.Editor.Services;

public static class ThumbnailGenerator
{
    private const int MaterialCacheVersion = 2;
    private const int ModelCacheVersion = 4;

    private sealed class ThumbnailWorker : IDisposable
    {
        public required string ProjectRoot { get; init; }
        public required RhiDevice Device { get; init; }
        public required RhiSwapchain DummySwap { get; init; }
        public required EcsWorld World { get; init; }
        public required IGameLoop Loop { get; init; }
        public required GameAssemblyLoadContext LoadContext { get; init; }

        public void Dispose()
        {
            Loop.Dispose();
            World.Dispose();
            DummySwap.Dispose();
            Device.Dispose();
            LoadContext.Unload();
        }
    }

    private sealed class ThumbnailRequest
    {
        public required string ProjectRoot { get; init; }
        public required string AssetPath { get; init; }
        public required string AssetType { get; init; }
        public required int Size { get; init; }
        public required int ModelPartIndex { get; init; }
        public required TaskCompletionSource<byte[]?> Completion { get; init; }
    }

    private static readonly object _workerLock = new();
    private static readonly BlockingCollection<ThumbnailRequest> _requests =
        new();
    private static readonly ConcurrentDictionary<string, Task<Bitmap?>> _inFlight = new();
    private static Thread? _workerThread;

    private static void EnsureWorkerStarted()
    {
        lock (_workerLock)
        {
            if (_workerThread is { IsAlive: true })
                return;
            _workerThread = new Thread(WorkerMain)
            {
                IsBackground = true,
                Name = "Quick3D Thumbnail Renderer"
            };
            _workerThread.Start();
        }
    }

    private static string ResolveGameDllPath(string projectRoot)
    {
        if (!string.IsNullOrEmpty(projectRoot))
        {
            var searchPaths = new[]
            {
                Path.Combine(projectRoot, "Game", "bin", "Release", "net8.0", "osx-arm64", "Engine.Game.dll"),
                Path.Combine(projectRoot, "Game", "bin", "Debug", "net8.0", "osx-arm64", "Engine.Game.dll"),
                Path.Combine(projectRoot, "Game", "bin", "Release", "net8.0", "Engine.Game.dll"),
                Path.Combine(projectRoot, "Game", "bin", "Debug", "net8.0", "Engine.Game.dll"),
            };
            foreach (var path in searchPaths)
            {
                if (File.Exists(path)) return path;
            }
            return Path.Combine(projectRoot, "Game", "bin", "Release", "net8.0", "Engine.Game.dll");
        }
        return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Engine.Game.dll");
    }

    public static string GetCacheFilePath(
        string assetPath,
        string assetType,
        int size = 256,
        int modelPartIndex = -1)
    {
        var cacheDir = Path.Combine(App.ProjectRoot, "out", ".cache", "thumbnails");
        Directory.CreateDirectory(cacheDir);

        var fileInfo = new FileInfo(assetPath);
        int cacheVersion = assetType switch
        {
            "Material" => MaterialCacheVersion,
            "Model" => ModelCacheVersion,
            _ => 1
        };
        string cacheKeySource =
            $"{cacheVersion}\n{assetType}\n{size}\n" +
            $"{modelPartIndex}\n" +
            $"{Path.GetFullPath(assetPath)}\n" +
            $"{fileInfo.LastWriteTimeUtc.Ticks}";
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(cacheKeySource));
        string cacheKey = Convert.ToHexString(hash).ToLowerInvariant() + ".png";
        return Path.Combine(cacheDir, cacheKey);
    }

    public static async Task<Bitmap?> GetOrGenerateThumbnailAsync(
        string assetPath,
        string assetType,
        int size = 256,
        int modelPartIndex = -1)
    {
        string cacheFile = GetCacheFilePath(
            assetPath,
            assetType,
            size,
            modelPartIndex);
        if (TryLoadBitmap(cacheFile, out var cachedBitmap))
            return cachedBitmap;

        var task = _inFlight.GetOrAdd(
            cacheFile,
            _ => GenerateThumbnailAsync(
                assetPath,
                assetType,
                cacheFile,
                size,
                modelPartIndex));
        try
        {
            return await task;
        }
        finally
        {
            _inFlight.TryRemove(cacheFile, out _);
        }
    }

    private static async Task<Bitmap?> GenerateThumbnailAsync(
        string assetPath,
        string assetType,
        string cacheFile,
        int size,
        int modelPartIndex)
    {
        try
        {
            var fileInfo = new FileInfo(assetPath);
            if (!fileInfo.Exists)
                return null;
            byte[]? bytes = await RenderThumbnailAsync(
                App.ProjectRoot,
                assetPath,
                assetType,
                size,
                modelPartIndex).ConfigureAwait(false);
            if (bytes == null)
                return null;
            return await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(
                () => CreateBitmap(
                    bytes,
                    cacheFile,
                    size),
                Avalonia.Threading.DispatcherPriority.Background);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ThumbnailGenerator] Error generating thumbnail for {assetPath}: {ex}");
            return null;
        }
    }

    private static Task<byte[]?> RenderThumbnailAsync(
        string projectRoot,
        string assetPath,
        string assetType,
        int size,
        int modelPartIndex)
    {
        EnsureWorkerStarted();
        var completion = new TaskCompletionSource<byte[]?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _requests.Add(new ThumbnailRequest
        {
            ProjectRoot = projectRoot,
            AssetPath = assetPath,
            AssetType = assetType,
            Size = size,
            ModelPartIndex = modelPartIndex,
            Completion = completion
        });
        return completion.Task;
    }

    private static void WorkerMain()
    {
        ThumbnailWorker? worker = null;
        try
        {
            foreach (ThumbnailRequest request in _requests.GetConsumingEnumerable())
            {
                try
                {
                    if (worker == null ||
                        !string.Equals(
                            worker.ProjectRoot,
                            request.ProjectRoot,
                            StringComparison.Ordinal))
                    {
                        worker?.Dispose();
                        worker = null;
                        worker = CreateWorker(request.ProjectRoot);
                    }
                    request.Completion.TrySetResult(
                        RenderThumbnailBytes(worker, request));
                }
                catch (Exception exception)
                {
                    Console.WriteLine(
                        $"[ThumbnailGenerator] Error generating thumbnail " +
                        $"for {request.AssetPath}: {exception}");
                    request.Completion.TrySetResult(null);
                }
            }
        }
        finally
        {
            worker?.Dispose();
        }
    }

    private static ThumbnailWorker CreateWorker(string projectRoot)
    {
        string dllPath = ResolveGameDllPath(projectRoot);
        if (!File.Exists(dllPath))
            throw new FileNotFoundException("Game DLL not found.", dllPath);

        RhiDevice? device = null;
        RhiSwapchain? dummySwap = null;
        EcsWorld? world = null;
        GameAssemblyLoadContext? loadContext = null;
        IGameLoop? loop = null;
        try
        {
            RhiNative.RhiInit(out IntPtr devicePointer);
            device = new RhiDevice(devicePointer, ownsHandle: true);
            dummySwap = new RhiSwapchain(
                device,
                IntPtr.Zero,
                ownsHandle: true);
            world = new EcsWorld();
            loadContext = new GameAssemblyLoadContext(dllPath);
            var assembly = loadContext.LoadFromAssemblyName(
                new AssemblyName("Engine.Game"));
            Type loopType = assembly.GetTypes().First(
                type => typeof(IGameLoop).IsAssignableFrom(type) &&
                    !type.IsInterface);
            loop = (IGameLoop)Activator.CreateInstance(
                loopType,
                false)!;
            loop.Init(
                device.Handle,
                dummySwap.Handle,
                world,
                enableImGui: false);
            return new ThumbnailWorker
            {
                ProjectRoot = projectRoot,
                Device = device,
                DummySwap = dummySwap,
                World = world,
                Loop = loop,
                LoadContext = loadContext
            };
        }
        catch
        {
            loop?.Dispose();
            world?.Dispose();
            dummySwap?.Dispose();
            device?.Dispose();
            loadContext?.Unload();
            throw;
        }
    }

    private static byte[] RenderThumbnailBytes(
        ThumbnailWorker worker,
        ThumbnailRequest request)
    {
        using var target = RhiTexture.CreateRenderTarget(
            worker.Device,
            (uint)request.Size,
            (uint)request.Size,
            RhiNative.TextureFormat.Bgra8Unorm);
        worker.Loop.RenderThumbnail(
            Path.Combine(request.ProjectRoot, "Content"),
            request.AssetPath,
            request.AssetType,
            target,
            (uint)request.Size,
            (uint)request.Size,
            modelPartIndex: request.ModelPartIndex);
        return target.Readback(
            (uint)request.Size,
            (uint)request.Size,
            (uint)(request.Size * 4));
    }

    private static Bitmap CreateBitmap(
        byte[] bytes,
        string cacheFile,
        int size)
    {
        using var bitmap = new WriteableBitmap(
            new PixelSize(size, size),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Premul);
        using (var framebuffer = bitmap.Lock())
        {
            System.Runtime.InteropServices.Marshal.Copy(
                bytes,
                0,
                framebuffer.Address,
                bytes.Length);
        }
        bitmap.Save(cacheFile);
        return new Bitmap(cacheFile);
    }

    private static bool TryLoadBitmap(string cacheFile, out Bitmap? bitmap)
    {
        bitmap = null;
        if (!File.Exists(cacheFile))
            return false;

        try
        {
            bitmap = new Bitmap(cacheFile);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
