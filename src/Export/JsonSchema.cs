using System.Text.Json.Serialization;

namespace SpawnSpotter.Export;

// DTOs used for JSONL output. Keep them flat and use snake_case via JsonPropertyName so the
// wire format matches the documented record schema names exactly.
public sealed class JsonEvent
{
    [JsonPropertyName("timestamp_utc")] public string TimestampUtc { get; init; } = string.Empty;
    [JsonPropertyName("classification")] public string Classification { get; init; } = string.Empty;
    [JsonPropertyName("monitored_via")] public string MonitoredVia { get; init; } = string.Empty;
    [JsonPropertyName("hwnd")] public string Hwnd { get; init; } = "0x0";
    [JsonPropertyName("window_class")] public string WindowClass { get; init; } = string.Empty;
    [JsonPropertyName("window_title")] public string WindowTitle { get; init; } = string.Empty;
    [JsonPropertyName("focused_pid")] public uint FocusedPid { get; init; }
    [JsonPropertyName("parent_chain")] public List<JsonChainNode> ParentChain { get; init; } = [];
    [JsonPropertyName("key_age_ms")] public long KeyAgeMs { get; init; }
    [JsonPropertyName("mouse_age_ms")] public long MouseAgeMs { get; init; }
    [JsonPropertyName("idle_time_ms")] public long IdleTimeMs { get; init; }
    [JsonPropertyName("locked_hwnd_before")] public string LockedHwndBefore { get; init; } = "0x0";
    [JsonPropertyName("locked_pid_before")] public uint LockedPidBefore { get; init; }
    [JsonPropertyName("note")] public string Note { get; init; } = string.Empty;
}

public sealed class JsonChainNode
{
    [JsonPropertyName("pid")] public uint Pid { get; init; }
    [JsonPropertyName("image_path")] public string ImagePath { get; init; } = string.Empty;
    [JsonPropertyName("basename")] public string Basename { get; init; } = string.Empty;
    [JsonPropertyName("command_line")] public string CommandLine { get; init; } = string.Empty;
    [JsonPropertyName("cwd")] public string Cwd { get; init; } = string.Empty;
    [JsonPropertyName("package_aumi")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PackageAumi { get; init; }
    [JsonPropertyName("env")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string>? Env { get; init; }
    [JsonPropertyName("note")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Note { get; init; }
}

[JsonSerializable(typeof(JsonEvent))]
[JsonSerializable(typeof(JsonChainNode))]
[JsonSerializable(typeof(List<JsonEvent>))]
[JsonSourceGenerationOptions(WriteIndented = false, DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
public partial class JsonExportContext : JsonSerializerContext
{
}
