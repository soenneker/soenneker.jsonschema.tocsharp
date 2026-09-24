using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Soenneker.JsonSchema.ToCSharp.Abstract;
using Soenneker.JsonSchema.ToCSharp.Internal;
using Soenneker.Utils.File.Abstract;
using Soenneker.Utils.Json;

namespace Soenneker.JsonSchema.ToCSharp;

public sealed class JsonSchemaToCSharp : IJsonSchemaToCSharp
{
    private readonly IFileUtil _fileUtil;

    public JsonSchemaToCSharp(IFileUtil fileUtil)
    {
        _fileUtil = fileUtil;
    }

    public JsonSchemaToCSharpResult Generate(string schemaJson, JsonSchemaToCSharpOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schemaJson);
        cancellationToken.ThrowIfCancellationRequested();
        options ??= new();
        JsonNode document = JsonUtil.Deserialize(schemaJson, GeneratorJsonContext.Default.JsonNode) ??
                            throw new ArgumentException("A schema cannot be null.", nameof(schemaJson));
        if (document is not JsonObject && document.GetValueKind() is not (JsonValueKind.True or JsonValueKind.False))
            throw new ArgumentException("Expected an object or boolean schema.", nameof(schemaJson));
        var emitter = new SchemaEmitter(document, options, cancellationToken);
        string name = options.RootTypeName ?? (document as JsonObject)?["title"]?.GetValue<string>() ?? "Root";
        string rootType = emitter.GetType(document, name);
        var types = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (options.GenerateAllDefinitions && document is JsonObject root)
            foreach (string keyword in new[] { "definitions", "$defs" })
                if (root[keyword] is JsonObject definitions)
                    foreach ((string key, JsonNode? schema) in definitions.OrderBy(p => p.Key, StringComparer.Ordinal))
                        if (schema != null)
                            types["#/" + keyword + "/" + key.Replace("~", "~0").Replace("/", "~1")] =
                                emitter.GetType(schema, key);
        return emitter.Finish(rootType, new ReadOnlyDictionary<string, string>(types));
    }

    public async ValueTask<JsonSchemaToCSharpResult> GenerateFile(string schemaPath, string outputDirectory,
        JsonSchemaToCSharpOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schemaPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        string input = Path.GetFullPath(schemaPath);
        string output = Path.GetFullPath(outputDirectory);
        string json = await _fileUtil.Read(input, cancellationToken: cancellationToken).ConfigureAwait(false);
        JsonSchemaToCSharpResult result = Generate(json, options, cancellationToken);
        var paths = new List<(string Path, string Source)>();
        foreach ((string relative, string source) in result.Files)
        {
            string path = Path.GetFullPath(Path.Combine(output, relative));
            if (!path.StartsWith(Path.TrimEndingDirectorySeparator(output) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
                throw new IOException("Generated path escaped the output directory.");
            if (string.Equals(path, input, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Output would overwrite the input schema.");
            if (await _fileUtil.Exists(path, cancellationToken).ConfigureAwait(false) && options?.Overwrite != true)
                throw new IOException("Output file already exists: " + path);
            paths.Add((path, source));
        }

        foreach ((string path, string source) in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _fileUtil.CreateDirectory(Path.GetDirectoryName(path)!, cancellationToken).ConfigureAwait(false);
            // Keep exclusive creation so a file appearing after the existence check cannot be overwritten.
            await using var stream = new FileStream(path,
                options?.Overwrite == true ? FileMode.Create : FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, true);
            await using var writer = new StreamWriter(stream);
            await writer.WriteAsync(source.AsMemory(), cancellationToken).ConfigureAwait(false);
        }

        return result;
    }
}