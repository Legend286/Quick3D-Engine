# Render Graph

**Purpose**: The Render Graph framework manages high-level GPU execution logic, transient resource aliasing, and render pass dependencies.

## Public API Surface
- `RenderGraphCompiler`: Sorts passes, determines lifetimes, and calculates optimal memory aliasing.
- `RenderGraphExecutor`: Traverses the compiled graph, allocating and executing transient resources efficiently via heap-based memory.
- `RenderPass`: Base class for user-defined rendering passes (e.g., `PbrPass`, `GridPass`).
- `MemoryAliasingPlan`: Represents overlapping resource layouts over time.

## Usage Example
```csharp
var compiler = new RenderGraphCompiler();
var plan = compiler.Compile(scene.Passes);

var executor = new RenderGraphExecutor(device);
executor.Execute(plan, sink);
```

## Performance
Leverages `RhiHeap` for memory aliasing. Transient textures are created and destroyed using aliased memory from a central heap, significantly lowering memory footprints and allocation overhead.
