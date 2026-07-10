using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.DevTunnels.Contracts;

namespace Dtssh.Json;

internal static class DtsshJson
{
    private static bool installed;

    // Injects the source-generated resolver into the SDK's shared serializer options.
    // MUST be called once at startup before any TunnelManagementClient/host/client use,
    // otherwise the SDK's reflection-based serialization throws under NativeAOT.
    //
    // The source-gen context is tried first; under a JIT build a reflection resolver
    // backstops any contract type not yet in the context. Under NativeAOT the reflection
    // resolver is a no-op at runtime, so DtsshSerializerContext must cover the full graph.
    public static void Install()
    {
        if (installed) return;
        installed = true;

        IJsonTypeInfoResolver resolver = DtsshSerializerContext.Default;
#pragma warning disable IL2026, IL3050 // DefaultJsonTypeInfoResolver is a JIT-only backstop.
        if (JsonSerializer.IsReflectionEnabledByDefault)
            resolver = JsonTypeInfoResolver.Combine(DtsshSerializerContext.Default, new DefaultJsonTypeInfoResolver());
#pragma warning restore IL2026, IL3050

        TunnelContracts.JsonOptions.TypeInfoResolver = resolver;
    }
}

