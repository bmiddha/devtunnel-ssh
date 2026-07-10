using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dtssh.Json;

// JSON shapes emitted by the `devtunnel` CLI (`-j`) and dtssh's own pairing
// metadata. All parsing goes through the source-generated AppJson context so it
// stays NativeAOT-safe.

internal sealed class DevtunnelTokenResult
{
    [JsonPropertyName("token")] public string? Token { get; set; }
}

// Subset of a tunnel object as printed by `devtunnel list/show -j`.
internal sealed class CliTunnel
{
    [JsonPropertyName("tunnelId")] public string? TunnelId { get; set; }
    [JsonPropertyName("clusterId")] public string? ClusterId { get; set; }
    [JsonPropertyName("labels")] public string[]? Labels { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("hostConnections")] public int HostConnections { get; set; }
}

// `devtunnel show <id> -j` wraps the tunnel under "tunnel".
internal sealed class CliShowResult
{
    [JsonPropertyName("tunnel")] public CliTunnel? Tunnel { get; set; }
}

// `devtunnel list -j` returns {"tunnels":[...]} (or {"value":[...]}).
internal sealed class CliListResult
{
    [JsonPropertyName("tunnels")] public CliTunnel[]? Tunnels { get; set; }
    [JsonPropertyName("value")] public CliTunnel[]? Value { get; set; }
}

// dtssh pairing metadata carried in the tunnel description (compact keys).
internal sealed class PairingMeta
{
    [JsonPropertyName("v")] public int V { get; set; }
    [JsonPropertyName("a")] public string? Alias { get; set; }
    [JsonPropertyName("p")] public int Port { get; set; }
    [JsonPropertyName("u")] public string? User { get; set; }
    [JsonPropertyName("h")] public string? HostPub { get; set; }
    [JsonPropertyName("k")] public string? Seed { get; set; }
    [JsonPropertyName("c")] public string? CreatedBy { get; set; }
}

[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(DevtunnelTokenResult))]
[JsonSerializable(typeof(CliTunnel))]
[JsonSerializable(typeof(CliTunnel[]))]
[JsonSerializable(typeof(CliShowResult))]
[JsonSerializable(typeof(CliListResult))]
[JsonSerializable(typeof(PairingMeta))]
[JsonSerializable(typeof(Discovery.HostKeyStamp))]
[JsonSerializable(typeof(JsonElement))]
internal partial class AppJsonContext : JsonSerializerContext { }
