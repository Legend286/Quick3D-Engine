# `Game/`

Your gameplay code. Hot-reloaded via `AssemblyLoadContext` swap. Compiled into `dotnet build Game`.

Anything in this folder can reference anything in `engine_cs/` (full engine + RHI). Game scripts attach to entities as FLECS components or run as global systems.
