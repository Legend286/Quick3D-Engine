# FLECS ECS Integration

Native dynamic ECS integration using S. Mertens' FLECS library.

## Purpose
Exposes a C ABI bridge in `EngineC` to integrate FLECS component and entity management dynamically with C# userspace without hardcoding component schemas in the native C layer.

## Public API Surface
### Native C exports (`engine_c/engine_ecs.h`)
- `engine_ecs_init` - Initializes a new FLECS world.
- `engine_ecs_shutdown` - Shuts down and disposes the FLECS world.
- `engine_ecs_create_entity` - Allocates a new entity handle in the world.
- `engine_ecs_register_component` - Dynamically registers a component by name, byte size, and byte alignment.
- `engine_ecs_set_component` - Binds component data of a specified size to an entity.
- `engine_ecs_get_component` - Retrieves a copy of the component data from an entity.

### C# Wrapper (`EcsWorld`)
- `CreateEntity()` - Creates a new entity.
- `Set<T>(ulong entity, in T component)` - Associates a component struct with an entity (auto-registering the type dynamically on the first call).
- `TryGet<T>(ulong entity, out T component)` - Gets a component struct associated with an entity.

## Usage Example
```csharp
using Engine.RHI;
using System.Runtime.InteropServices;

[StructLayout(LayoutKind.Sequential)]
public struct VelocityComponent
{
    public float X;
    public float Y;
}

using var world = new EcsWorld();
ulong ent = world.CreateEntity();

world.Set(ent, new VelocityComponent { X = 1.5f, Y = -2.0f });

if (world.TryGet<VelocityComponent>(ent, out var vel))
{
    Console.WriteLine($"Velocity: {vel.X}, {vel.Y}");
}
```

## Performance Characteristics
- Dynamic registration uses a `ConcurrentDictionary` on the C# side, making component registrations idempotent and caching metadata lookup.
- Structural components must be sequential structures with explicit layouts (such as `[StructLayout(LayoutKind.Sequential)]` and `[MarshalAs(UnmanagedType.ByValArray)]` for arrays) to ensure unmanaged marshaling compatibility.

## Cross-references
- [`engine-spec.md` §11](../../engine-spec.md)
- [ECS Components](components.md)
