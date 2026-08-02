# ECS Components

The engine uses FLECS as the ECS substrate. Core components are owned by the engine. Game-specific components are user-defined in `Game/`.

## Animation

`AnimatorComponent` is CPU-owned animation intent. It stores stable skeleton and
clip IDs, a playback clock, a playback rate, active/looping flags, and a
generation for stale-slot protection. The renderer consumes active components
through the GPU animation pass; gameplay remains authoritative for transitions
and entity transforms.

```csharp
world.Set(entity, AnimatorComponent.Create(
    skeletonId,
    clipId,
    playbackRate: 1.0f,
    looping: true));
```

See [GPU animation runtime](../renderer/gpu-animation.md) and
[`engine-spec.md` §11](../../engine-spec.md).
