using System.Text.Json.Serialization;

namespace OpenCodeTelegramBridge.Models;

public sealed class TelegramBridgeProjectConfig
{
    [JsonPropertyName("version")]
    public int Version { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("sections")]
    public List<TelegramBridgeSectionConfig> Sections { get; set; } = [];
}

public sealed class TelegramBridgeSectionConfig
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("directory")]
    public string Directory { get; set; } = "";

    [JsonPropertyName("commands")]
    public List<TelegramBridgeCommandConfig> Commands { get; set; } = [];

    [JsonIgnore]
    public string ResolvedDirectory { get; set; } = "";
}

public sealed class TelegramBridgeCommandConfig
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("command")]
    public string? Command { get; set; }

    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("askForArguments")]
    public bool AskForArguments { get; set; }
}

public sealed record ProjectConfigLoadResult(
    bool Exists,
    TelegramBridgeProjectConfig? Config,
    string? Error)
{
    public bool IsValid => Exists && Config != null && Error == null;
}
