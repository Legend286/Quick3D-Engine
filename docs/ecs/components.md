# ECS Components

> **TODO(ecs):** Core component schema (`Transform`, `Visibility`, `MeshHandle`, `MaterialHandle`, `PhysicsBodyHandle`, `AudioEmitterHandle`, `ScriptRef`).

The engine uses FLECS as the ECS substrate. Core components are owned by the engine. Game-specific components are user-defined in `Game/`.

See [`engine-spec.md` §11](../../engine-spec.md).
