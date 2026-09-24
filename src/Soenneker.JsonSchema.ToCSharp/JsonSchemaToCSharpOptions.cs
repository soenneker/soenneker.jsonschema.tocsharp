using System.Collections.Generic;
namespace Soenneker.JsonSchema.ToCSharp;
/// <summary>Controls generation of standalone .NET 10 models using System.Text.Json.</summary>
public sealed class JsonSchemaToCSharpOptions
{
    /// <summary>The namespace for serialization infrastructure. Models use its Models child namespace.</summary>
    public string Namespace { get; init; } = "Generated.Schema";
    /// <summary>The suggested root type name; defaults to the schema title or Root.</summary>
    public string? RootTypeName { get; init; }
    /// <summary>Emit all definitions, including those not reachable from the root.</summary>
    public bool GenerateAllDefinitions { get; init; } = true;
    /// <summary>Infer object and string models from properties and pattern when type is absent, as in Adaptive Cards.
    /// Such models narrow schemas that technically allow other JSON kinds. Disable to require explicit types.</summary>
    public bool InferTypesFromKeywords { get; init; } = true;
    /// <summary>Reject lossy mappings instead of reporting a diagnostic and using JsonElement.</summary>
    public bool FailOnUntypedSchemas { get; init; } = true;
    /// <summary>Permit replacing files with matching generated names. Unrelated files are retained.</summary>
    public bool Overwrite { get; init; }
    /// <summary>Schema JSON indexed by absolute resource URI. References never cause network requests.</summary>
    public IReadOnlyDictionary<string, string> ExternalSchemas { get; init; } = new Dictionary<string, string>();
}
