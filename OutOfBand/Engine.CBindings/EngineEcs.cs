// SPDX-License-Identifier: MIT
using System;
using System.Runtime.InteropServices;

namespace Engine.CBindings;

public static partial class EcsNative
{
    public const string Library = "EngineC";

    [LibraryImport(Library, EntryPoint = "engine_ecs_init")]
    public static partial IntPtr EngineEcsInit();

    [LibraryImport(Library, EntryPoint = "engine_ecs_shutdown")]
    public static partial void EngineEcsShutdown(IntPtr world);

    [LibraryImport(Library, EntryPoint = "engine_ecs_create_entity")]
    public static partial ulong EngineEcsCreateEntity(IntPtr world);

    [LibraryImport(Library, EntryPoint = "engine_ecs_register_component")]
    public static unsafe partial ulong EngineEcsRegisterComponent(
        IntPtr world,
        [MarshalAs(UnmanagedType.LPStr)] string name,
        nuint size,
        nuint alignment);

    [LibraryImport(Library, EntryPoint = "engine_ecs_set_component")]
    public static unsafe partial void EngineEcsSetComponent(
        IntPtr world,
        ulong entity,
        ulong componentId,
        void* data,
        nuint size);

    [LibraryImport(Library, EntryPoint = "engine_ecs_get_component")]
    public static unsafe partial int EngineEcsGetComponent(
        IntPtr world,
        ulong entity,
        ulong componentId,
        void* outData,
        nuint size);
}
