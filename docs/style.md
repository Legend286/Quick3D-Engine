# Style

## File / type naming

| Language | Convention |
| --- | --- |
| C (`*.c`, `*.h`) | `snake_case`. Types end `_t`. Public exports `ENGINE_API`. |
| C++ | `PascalCase` for types, `camelCase` for members, `snake_case` for free functions. Member fields prefixed `_` ONLY for ABI-stable extensions; otherwise no Hungarian. |
| C# (`*.cs`) | `PascalCase` for types/methods/properties, `camelCase` for locals/params. Public types in `Engine.<Module>` namespace. |
| CMake | `snake_case` for everything; targets use `engine_<scope>` prefix. |
| JSON | file names `snake_case.json`; keys `snake_case`. |
| Shaders | `PascalCase.vert` / `PascalCase.frag` / `PascalCase.mesh` (matches the file's primary entry). |

## File headers

Every engine source file starts with:

```c
/* SPDX-License-Identifier: MIT */
```

C#:

```csharp
// SPDX-License-Identifier: MIT
```

CMake:

```cmake
# SPDX-License-Identifier: MIT
```

## Formatting

C/C++ use `clang-format` with rules under `.clang-format` (LLVM base + 4 spaces). C# uses `dotnet format` with `.editorconfig`.

## Comments

Per [`AGENTS.md` §4](../AGENTS.md), chatty inline comments are banned. Allowed: SPDX header, structured Doxygen on public APIs, `TODO(#issue):` markers, shader `// MARK:` dividers.

## Documentation

Every public symbol has a doc entry under `docs/`. New features ship docs in the same commit as the code (or in a tightly-coupled follow-up).
