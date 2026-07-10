using System.Text.Json.Serialization;
using Microsoft.DevTunnels.Contracts;

namespace Dtssh.Json;

// Source-generated JSON metadata for the DevTunnels contract types dtssh touches.
// Injected into TunnelContracts.JsonOptions at startup so the SDK never falls back
// to reflection-based serialization (which crashes under NativeAOT). See DtsshJson.Install.
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Tunnel))]
[JsonSerializable(typeof(Tunnel[]))]
[JsonSerializable(typeof(TunnelPort))]
[JsonSerializable(typeof(TunnelPort[]))]
[JsonSerializable(typeof(TunnelEndpoint))]
[JsonSerializable(typeof(TunnelEndpoint[]))]
[JsonSerializable(typeof(TunnelRelayTunnelEndpoint))]
[JsonSerializable(typeof(LocalNetworkTunnelEndpoint))]
[JsonSerializable(typeof(TunnelAccessControl))]
[JsonSerializable(typeof(TunnelAccessControlEntry))]
[JsonSerializable(typeof(TunnelAccessControlEntry[]))]
[JsonSerializable(typeof(TunnelOptions))]
[JsonSerializable(typeof(TunnelStatus))]
[JsonSerializable(typeof(ProblemDetails))]
[JsonSerializable(typeof(TunnelListByRegion))]
[JsonSerializable(typeof(TunnelListByRegion[]))]
internal partial class DtsshSerializerContext : JsonSerializerContext { }
