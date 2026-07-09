using System;
using System.IO;
using System.Linq;
using System.Reflection;
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
    private static IGameLoop? _thumbnailLoop;
    private static RhiDevice? _device;
    private static RhiSwapchain? _dummySwap;
    private static readonly object _lock = new();
    private static bool _initialized = false;

    private static void EnsureInitialized()
    {
        if (_initialized) return;
        lock (_lock)
        {
            if (_initialized) return;

            RhiNative.RhiInit(out var rhiDevicePtr);
            _device = new RhiDevice(rhiDevicePtr, ownsHandle: true);
            _dummySwap = new RhiSwapchain(_device, IntPtr.Zero, ownsHandle: true);

            var dllPath = ResolveGameDllPath();
            if (File.Exists(dllPath))
            {
                var loadContext = new GameAssemblyLoadContext(dllPath);
                var assembly = loadContext.LoadFromAssemblyName(new AssemblyName("Engine.Game"));
                var loopType = assembly.GetTypes().First(t => typeof(IGameLoop).IsAssignableFrom(t) && !t.IsInterface);
                _thumbnailLoop = (IGameLoop)Activator.CreateInstance(loopType)!;
                _thumbnailLoop.Init(_device.Handle, _dummySwap.Handle, null!);
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

    public static async Task<Bitmap?> GetOrGenerateThumbnailAsync(string assetPath, string assetType)
    {
        return await Task.Run(() =>
        {
            try
            {
                var cacheDir = Path.Combine(App.ProjectRoot, "out", ".cache", "thumbnails");
                Directory.CreateDirectory(cacheDir);

                var fileInfo = new FileInfo(assetPath);
                if (!fileInfo.Exists) return null;

                string cacheKey = $"{assetPath}_{fileInfo.LastWriteTimeUtc.Ticks}".GetHashCode().ToString("X8") + ".png";
                string cacheFile = Path.Combine(cacheDir, cacheKey);

                if (File.Exists(cacheFile))
                {
                    try { return new Bitmap(cacheFile); } catch { }
                }

                EnsureInitialized();

                if (_thumbnailLoop == null || _device == null)
                {
                    Console.WriteLine($"[ThumbnailGenerator] Failed to initialize: Loop={_thumbnailLoop != null}, Device={_device != null}");
                    return null;
                }

                lock (_lock)
                {
                    using var target = RhiTexture.CreateRenderTarget(_device, 256, 256, RhiNative.TextureFormat.Bgra8Unorm);

                    string contentRoot = Path.Combine(App.ProjectRoot, "Content");
                    _thumbnailLoop.RenderThumbnail(contentRoot, assetPath, assetType, target);

                    var bytes = target.Readback(256, 256, 256 * 4);

                    using var wb = new WriteableBitmap(new PixelSize(256, 256), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
                    using (var fb = wb.Lock())
                    {
                        System.Runtime.InteropServices.Marshal.Copy(bytes, 0, fb.Address, bytes.Length);
                    }

                    wb.Save(cacheFile);
                    return new Bitmap(cacheFile);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ThumbnailGenerator] Error generating thumbnail for {assetPath}: {ex}");
                return null;
            }
        });
    }
}
