# C# Assembly Hot-Reload

Runtime assembly loading and compilation.

## Purpose
Enables iterative game loop and render pipeline updates without restarting the Editor. Compiles changes in `Engine.Game.csproj` on-the-fly and swaps the active dynamic assembly context.

## Public API Surface
### Interfaces (`Engine.RHI.IGameLoop`)
- `Init(IntPtr deviceHandle, IntPtr swapchainHandle, IEntityStore world)` - Initializes the game loop reference with RHI handles and the ECS world.
- `LoadScene(string contentRoot, string sceneName)` - Loads a scene from the project directory.
- `RenderFrame(RhiTexture backBuffer, uint width, uint height)` - Renders a single frame to the specified target texture.

### Loading Infrastructure (`GameAssemblyLoadContext`)
- Collectible `AssemblyLoadContext` subclass that loads assembly bytes directly via stream to avoid file locking on disk.
- Delegates shared contract and bindings assemblies (`Engine.RHI`, `Engine.RenderGraph`, `Engine.Scene`, `Engine.CBindings`) to the default `AssemblyLoadContext` to avoid type isolation/mismatch issues across the host-plugin boundary.

## Usage Example
```csharp
// Inside ViewportPanelViewModel.cs
var context = new GameAssemblyLoadContext(dllPath);
var assembly = context.LoadFromAssemblyName(new AssemblyName("Engine.Game"));
var loopType = assembly.GetType("Engine.Game.GameLoop");
var gameLoop = (IGameLoop)Activator.CreateInstance(loopType);
```

## Performance Characteristics
- Loading assemblies from streams avoids OS-level file-locks, enabling compilers (`dotnet build`) to write directly to the same output file while the Editor is running.
- Context is garbage-collectible; unloading triggers collection of type metadata to prevent memory footprint growth across cycles.

## Cross-references
- [`engine-spec.md` §19](../../engine-spec.md)
