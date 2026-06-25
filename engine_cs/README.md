# `engine_cs/`

C# engine modules. Each subsystem lives under its own folder under the `Engine.<Module>` namespace:

```
Engine.Scene/
Engine.ECS/
Engine.Renderer/
Engine.RHI/         (P/Invoke bindings against engine_c/engine.h)
Engine.Physics/
Engine.Audio/
Engine.Assets/
Engine.Editor/
Engine.Networking/
Engine.Scripting/
Engine.Steamworks/
Engine.Profiling/
```

C# is **inside** the engine ABI. C# is the engine — *not* user code. User code lives in `Game/` and is hot-reloaded.
