using System.Threading;
using System.Threading.Tasks;
namespace Soenneker.JsonSchema.ToCSharp.Abstract;
/// <summary>Generates System.Text.Json C# models from JSON Schema draft 6 or 7, without protocol assumptions.</summary>
public interface IJsonSchemaToCSharp
{
    /// <summary>Generates models, converters, optional-value support, and serialization metadata in memory.
    /// Supports local and explicitly supplied external references. Does not enforce every schema validation constraint.</summary>
    /// <param name="schemaJson">An object or boolean schema.</param>
    /// <param name="options">Naming, reference resolution, and fallback policies.</param>
    /// <param name="cancellationToken">Cancels generation.</param>
    /// <returns>Inspectable C# source, type mappings, and diagnostics.</returns>
    JsonSchemaToCSharpResult Generate(string schemaJson, JsonSchemaToCSharpOptions? options = null, CancellationToken cancellationToken = default);
    /// <summary>Reads a schema and writes generated sources. Existing files require Overwrite; the input cannot be overwritten.</summary>
    /// <param name="schemaPath">The input JSON Schema file.</param>
    /// <param name="outputDirectory">The destination directory.</param>
    /// <param name="options">Generation and overwrite policies.</param>
    /// <param name="cancellationToken">Cancels generation or I/O.</param>
    /// <returns>The generated result.</returns>
    ValueTask<JsonSchemaToCSharpResult> GenerateFile(string schemaPath, string outputDirectory,
        JsonSchemaToCSharpOptions? options = null, CancellationToken cancellationToken = default);
}
