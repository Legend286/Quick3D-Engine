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

See [GPU animation runtime](../renderer/gpu-animation.md),
[`engine-spec.md` §11](../../engine-spec.md), and
[ECS entity IDs](#entity-ids).

## Entity IDs

Flecs entity IDs are packed 64-bit values. The full `ulong` is retained for
runtime operations; editor labels display only the low 32-bit entity index so
generation metadata does not appear as part of the user-facing name.

```csharp
uint displayIndex = EcsEntityId.GetIndex(entityId);
```
