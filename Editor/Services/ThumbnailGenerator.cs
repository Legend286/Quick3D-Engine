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
        public required RhiDevice Device { get; init; }
        public required RhiSwapchain DummySwap { get; init; }
        public required IGameLoop Loop { get; init; }

        public void Dispose()
        {
            Loop.Dispose();
            DummySwap.Dispose();
            Device.Dispose();
        }
    }

    private static readonly object _initLock = new();
    private static readonly ConcurrentQueue<ThumbnailWorker> _availableWorkers = new();
    private static readonly ConcurrentDictionary<string, Task<Bitmap?>> _inFlight = new();
    private static SemaphoreSlim? _workerSemaphore;
    private static bool _initialized = false;

    private static void EnsureInitialized()
    {
        if (_initialized) return;
        lock (_initLock)
        {
            if (_initialized) return;

            var dllPath = ResolveGameDllPath();
            if (!File.Exists(dllPath))
            {
                _initialized = true;
                return;
            }

            int workerCount = 1;
            _workerSemaphore = new SemaphoreSlim(workerCount, workerCount);

            for (int i = 0; i < workerCount; i++)
            {
                RhiNative.RhiInit(out var rhiDevicePtr);
                var device = new RhiDevice(rhiDevicePtr, ownsHandle: true);
                var dummySwap = new RhiSwapchain(device, IntPtr.Zero, ownsHandle: true);

                var loadContext = new GameAssemblyLoadContext(dllPath);
                var assembly = loadContext.LoadFromAssemblyName(new AssemblyName("Engine.Game"));
                var loopType = assembly.GetTypes().First(t => typeof(IGameLoop).IsAssignableFrom(t) && !t.IsInterface);
                var loop = (IGameLoop)Activator.CreateInstance(loopType, false)!;
                loop.Init(
                    device.Handle,
                    dummySwap.Handle,
                    null!,
                    enableImGui: false);

                _availableWorkers.Enqueue(new ThumbnailWorker
                {
                    Device = device,
                    DummySwap = dummySwap,
                    Loop = loop
                });
            }

            _initialized = true;
        }
    }

    private static string ResolveGameDllPath()
    {
        if (!string.IsNullOrEmpty(App.ProjectRoot))
        {
            var searchPaths = new[]
            {
                Path.Combine(App.ProjectRoot, "Game", "bin", "Release", "net8.0", "osx-arm64", "Engine.Game.dll"),
                Path.Combine(App.ProjectRoot, "Game", "bin", "Debug", "net8.0", "osx-arm64", "Engine.Game.dll"),
                Path.Combine(App.ProjectRoot, "Game", "bin", "Release", "net8.0", "Engine.Game.dll"),
                Path.Combine(App.ProjectRoot, "Game", "bin", "Debug", "net8.0", "Engine.Game.dll"),
            };
            foreach (var path in searchPaths)
            {
                if (File.Exists(path)) return path;
            }
            return Path.Combine(App.ProjectRoot, "Game", "bin", "Release", "net8.0", "Engine.Game.dll");
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

            EnsureInitialized();
            if (_workerSemaphore == null)
                return null;

            await _workerSemaphore.WaitAsync().ConfigureAwait(false);
            if (!_availableWorkers.TryDequeue(out var worker))
            {
                _workerSemaphore.Release();
                return null;
            }

            try
            {
                using var target = RhiTexture.CreateRenderTarget(worker.Device, (uint)size, (uint)size, RhiNative.TextureFormat.Bgra8Unorm);
                string contentRoot = Path.Combine(App.ProjectRoot, "Content");
                worker.Loop.RenderThumbnail(
                    contentRoot,
                    assetPath,
                    assetType,
                    target,
                    (uint)size,
                    (uint)size,
                    modelPartIndex: modelPartIndex);

                var bytes = target.Readback((uint)size, (uint)size, (uint)(size * 4));

                using var wb = new WriteableBitmap(new PixelSize(size, size), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
                using (var fb = wb.Lock())
                {
                    System.Runtime.InteropServices.Marshal.Copy(bytes, 0, fb.Address, bytes.Length);
                }

                wb.Save(cacheFile);
                return new Bitmap(cacheFile);
            }
            finally
            {
                _availableWorkers.Enqueue(worker);
                _workerSemaphore.Release();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ThumbnailGenerator] Error generating thumbnail for {assetPath}: {ex}");
            return null;
        }
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
