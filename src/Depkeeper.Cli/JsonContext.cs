using System.Text.Json.Serialization;

namespace Depkeeper.Cli;

/// <summary>
/// Supplies generated JSON metadata for configuration and persistent state.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(DeploymentConfiguration))]
[JsonSerializable(typeof(MaintenanceState))]
[JsonSerializable(typeof(string[]))]
internal sealed partial class JsonContext : JsonSerializerContext;
