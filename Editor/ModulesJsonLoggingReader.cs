// SPDX-License-Identifier: MIT
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Engine.Editor;

/// <summary>
/// Reads the `logging` block from `<project>/.eeproj/modules.json`.
/// Defaults to log_mode 2 (INFO), no per-module overrides, default paths.
internal static class ModulesJsonLoggingReader
{
    public sealed record Result(
        int GlobalLevel,
        uint RingCapacity,
        uint MaxMsgBytes,
        bool EnableCrashDump,
        string CrashDumpPath,
        List<KeyValuePair<string, int>> ModuleOverrides);

    public static Result Read(string projectRoot)
    {
        var path = Path.Combine(projectRoot, ".eeproj", "modules.json");
        if (!File.Exists(path))
            return Default(projectRoot);

        try
        {
            using var stream = File.OpenRead(path);
            using var doc = JsonDocument.Parse(stream);
            if (!doc.RootElement.TryGetProperty("logging", out var logging))
                return Default(projectRoot);

            int logMode = logging.TryGetProperty("log_mode", out var lm)
                ? lm.GetInt32()
                : 2;

            int globalLevel = logMode switch
            {
                0 => 2, // ERROR  -> EngineLogError (only ERROR+FATAL pass)
                1 => 3, // WARN+ERROR
                2 => 4, // INFO
                3 => 5, // DEBUG
                4 => 6, // TRACE
                _ => 4,
            };

            uint ring = 1024;
            if (logging.TryGetProperty("ring_capacity_records", out var rc))
                ring = rc.GetUInt32();

            uint maxBytes = 512;
            if (logging.TryGetProperty("max_msg_bytes", out var mb))
                maxBytes = mb.GetUInt32();

            bool enableCrash = true;
            if (logging.TryGetProperty("enable_crash_dump", out var cd))
                enableCrash = cd.GetBoolean();

            string crashPath = Path.Combine(projectRoot, "out", "logs", "crash.json");
            if (logging.TryGetProperty("crash_dump_path", out var cp))
                crashPath = Path.Combine(projectRoot, cp.GetString() ?? crashPath);

            var overrides = new List<KeyValuePair<string, int>>();
            if (logging.TryGetProperty("module_overrides", out var mo)
                && mo.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in mo.EnumerateObject())
                {
                    int level = prop.Value.GetInt32() switch
                    {
                        0 => 2,
                        1 => 3,
                        2 => 4,
                        3 => 5,
                        4 => 6,
                        _ => 4,
                    };
                    overrides.Add(new(prop.Name, level));
                }
            }

            return new Result(globalLevel, ring, maxBytes, enableCrash, crashPath, overrides);
        }
        catch
        {
            return Default(projectRoot);
        }
    }

    private static Result Default(string projectRoot) =>
        new(5, 1024, 512, true,
            Path.Combine(projectRoot, "out", "logs", "crash.json"),
            new List<KeyValuePair<string, int>>());
}
