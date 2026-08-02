# Scene Lifetime

## Purpose

Scene replacement establishes a hard ownership boundary for renderer plans,
GPU caches, shadow atlases, asynchronous readbacks, ECS entities, and loaded
asset resources so a new scene cannot retain work or allocations from the
previous scene.

## Public API Surface

| Symbol | Purpose |
| --- | --- |
| `IGameLoop.LoadScene` | Releases the current scene before loading and compiling another scene. |
| `IGameLoop.NewScene` | Releases the current scene and creates an empty renderable scene. |
| `RenderGraphExecutor.ResetSceneResources` | Drops retained transient heaps, imported-resource references, and profiling history after GPU idle. |

## Usage Example

```csharp
gameLoop.NewScene(contentRoot);
ulong sun = gameLoop.AddDirectionalLight(
    Vector3.Normalize(new Vector3(-0.4f, -1.0f, -0.35f)),
    Vector3.One,
    3.5f,
    0.012f);
```

## Performance Characteristics

Scene replacement waits once for both graphics and compute queues because
plan-owned buffers and textures cannot be released while commands still use
them. Normal frame updates never perform this wait. After the queues become
idle, the old render plans are disposed before mesh, material, texture, and
asset registries are cleared. The render-graph transient heap, timestamp
history, GPU work-budget learning, and pending visibility picks are reset so
large prior scenes cannot affect the new scene's memory footprint or update
cadence. Bindless texture registrations are released as well; otherwise the
native heap would retain strong references after loader caches dropped their
managed wrappers.

Device-wide shader compilation and bindless-heap infrastructure remain alive
because they are renderer resources rather than scene resources. Persistent
viewport-sized depth and visibility surfaces are reused and cleared by the
first frame of the new plan.

## Cross-References

- [Render Graph](../renderer/render-graph.md)
- [Visibility Buffer](../renderer/visibility-buffer.md)
- [Scene Lights](lights.md)
- [engine-spec.md](../../engine-spec.md)
