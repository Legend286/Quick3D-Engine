// SPDX-License-Identifier: MIT
using System;
using System.Runtime.InteropServices;

namespace Engine.CBindings;

/// <summary>
/// P/Invoke surface mirroring engine_c/engine_log.h. Stable C ABI.
public static partial class EngineLog
{
    [StructLayout(LayoutKind.Sequential)]
    public struct EngineLogConfig
    {
        public uint Abi;
        public int GlobalLevel;
        public uint RingCapacityRecords;
        public uint MaxMsgBytes;
        public int EnableCrashDump;
        public IntPtr CrashDumpPath;       // const char*
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct EngineLogRecord
    {
        public long TimestampNs;
        public int Level;
        public int ThreadId;
        public IntPtr File;               // const char*
        public int Line;
        public IntPtr Msg;                // const char*
        public uint MsgLen;
        public IntPtr Module;             // const char*
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct EngineLogSink
    {
        public uint Abi;
        public IntPtr Write;              // delegate (EngineLogSinkFn) -> void
        public IntPtr Userdata;
        public IntPtr Name;               // const char*
    }

    public const int EngineLogOff       = 0;
    public const int EngineLogFatal     = 1;
    public const int EngineLogError     = 2;
    public const int EngineLogWarn      = 3;
    public const int EngineLogInfo      = 4;
    public const int EngineLogDebug     = 5;
    public const int EngineLogTrace     = 6;

    [LibraryImport("EngineC")]
    public static partial int EngineLogInit(in EngineLogConfig config);

    [LibraryImport("EngineC")]
    public static partial void EngineLogShutdown();

    [LibraryImport("EngineC")]
    public static partial void EngineLogSetGlobalLevel(int level);

    [LibraryImport("EngineC")]
    public static partial int EngineLogGlobalLevelGet();

    [LibraryImport("EngineC")]
    public static partial void EngineLogSetModuleLevel(IntPtr module, int level);

    [LibraryImport("EngineC")]
    public static unsafe partial int EngineLogModuleLevelGet(byte* module);

    [LibraryImport("EngineC")]
    public static partial void EngineLogFlushBlocking();

    [LibraryImport("EngineC")]
    public static partial void EngineLogDumpDiagnostics();

    [LibraryImport("EngineC")]
    public static unsafe partial int EngineLogDrain(EngineLogRecord* outRecords, int maxRecords);
}

/// <summary>
/// Free helper for any AllocHGlobal-backed config field, called at shutdown.
public static class EngineLogConfigDisposer
{
    public static void Release(ref Engine.CBindings.EngineLog.EngineLogConfig config)
    {
        if (config.CrashDumpPath != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(config.CrashDumpPath);
            config.CrashDumpPath = IntPtr.Zero;
        }
    }
}

/// <summary>
/// Helpers that translate Avalonia / C# world to the EngineLog ABI.
/// Owns a tiny pinned byte buffer for the module name so we don't allocate per call.
public static class EngineLogConfigBuilder
{
    public static Engine.CBindings.EngineLog.EngineLogConfig Build(
        int globalLevel,
        uint ringCapacityRecords,
        uint maxMsgBytes,
        bool enableCrashDump,
        string? crashDumpPath)
    {
        IntPtr pathPtr = IntPtr.Zero;
        if (!string.IsNullOrEmpty(crashDumpPath))
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(crashDumpPath);
            pathPtr = Marshal.AllocHGlobal(bytes.Length + 1);
            Marshal.Copy(bytes, 0, pathPtr, bytes.Length);
            Marshal.WriteByte(pathPtr, bytes.Length, 0);
        }

        return new Engine.CBindings.EngineLog.EngineLogConfig
        {
            Abi                 = (uint)Marshal.SizeOf<Engine.CBindings.EngineLog.EngineLogConfig>(),
            GlobalLevel         = globalLevel,
            RingCapacityRecords = ringCapacityRecords,
            MaxMsgBytes         = maxMsgBytes,
            EnableCrashDump     = enableCrashDump ? 1 : 0,
            CrashDumpPath       = pathPtr,
        };
    }
}


