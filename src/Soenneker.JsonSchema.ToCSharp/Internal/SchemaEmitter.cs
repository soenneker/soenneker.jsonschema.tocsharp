using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Text.RegularExpressions;

namespace Soenneker.JsonSchema.ToCSharp.Internal;

internal sealed class SchemaEmitter
{
    private const string _element = "global::System.Text.Json.JsonElement";
    private readonly JsonNode _root;
    private readonly JsonSchemaToCSharpOptions _options;
    private readonly CancellationToken _ct;
    private readonly Dictionary<string, string> _files = new(StringComparer.Ordinal);
    private readonly List<string> _diagnostics = [];
    private readonly Dictionary<JsonNode, string> _types = new();
    private readonly HashSet<JsonNode> _resolving = [];
    private readonly HashSet<string> _names = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<JsonNode, Uri> _scopes = new();
    private readonly Dictionary<string, JsonNode> _ids = new(StringComparer.Ordinal);
    private readonly Uri _baseUri = new("https://jsonschema.invalid/schema");
    private readonly HashSet<string> _optionalTypes = new(StringComparer.Ordinal);
    private readonly HashSet<string> _valueTypes = new(StringComparer.Ordinal) { "long", "ulong", "decimal", "bool" };
    private readonly SchemaMatcherEmitter _matchers;

    internal SchemaEmitter(JsonNode document, JsonSchemaToCSharpOptions options, CancellationToken ct)
    {
        CSharpNames.Validate(options.Namespace, nameof(options.Namespace), true);
        _options = options;
        _ct = ct;
        _root = document;
        _matchers = new SchemaMatcherEmitter(options.Namespace, Resolve, ct);
        _names.UnionWith(["Optional", "OptionalConverterFactory", "Models", "SchemaJsonContext", "JsonTypes", "JsonTypeCache", "UnionMatchers"]);
        _names.UnionWith([
            "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        ]);
        Index(_root, _baseUri);
        _ids.TryAdd(_baseUri.AbsoluteUri, _root);
        foreach ((string uri, string json) in options.ExternalSchemas)
        {
            var resource = new Uri(uri, UriKind.Absolute);
            JsonNode extra = JsonNode.Parse(json) ?? throw new ArgumentException("External schema cannot be null: " + uri);
            Index(extra, resource);
            if (!_ids.TryAdd(resource.AbsoluteUri, extra) && !ReferenceEquals(_ids[resource.AbsoluteUri], extra))
                throw new ArgumentException("Duplicate schema resource: " + uri);
        }
    }

    internal JsonSchemaToCSharpResult Finish(string rootType, IReadOnlyDictionary<string, string> namedTypes)
    {
        if (_options.FailOnUntypedSchemas && _diagnostics.Count > 0)
            throw new NotSupportedException(string.Join(Environment.NewLine, _diagnostics));
        EmitSerialization();
        if (_matchers.HasMatchers) _files["UnionMatchers.cs"] = _matchers.Finish();
        return new JsonSchemaToCSharpResult(new ReadOnlyDictionary<string, string>(_files),
            rootType, namedTypes, _diagnostics.AsReadOnly());
    }

    internal void RegisterOptional(string type) => _optionalTypes.Add(RuntimeType(type));

    // Nullable reference annotations are not runtime types; nullable value types are.
    private string RuntimeType(string type) => Regex.Replace(type, @"(global::[A-Za-z0-9_.]+|[A-Za-z0-9_]+)\?",
        match => _valueTypes.Contains(match.Groups[1].Value) ? "global::System.Nullable<" + match.Groups[1].Value + ">" : match.Groups[1].Value).Replace("?", "");

    private void EmitSerialization()
    {
        string prefix = "global::" + _options.Namespace + ".";
        string[] optionalTypes = _optionalTypes.OrderBy(t => t, StringComparer.Ordinal).ToArray();
        var registration = new StringBuilder(4096);
        foreach (string type in optionalTypes)
            registration.AppendLine("        if (type == typeof(Optional<" + type + ">)) return OptionalConverter<" + type + ">.Instance;");
        _files["Optional.cs"] = Template("Optional").Replace("@@NAMESPACE@@", _options.Namespace).Replace("@@CONVERTERS@@", registration.ToString());

        foreach (string type in new[] { "OptionalConverterFactory", "OptionalConverter", "JsonTypes", "JsonTypeCache" })
            _files[type + ".cs"] = Template(type).Replace("@@NAMESPACE@@", _options.Namespace)
                .Replace("@@CONVERTERS@@", registration.ToString());

        HashSet<string> types = _types.Values.Select(RuntimeType).ToHashSet(StringComparer.Ordinal);
        types.UnionWith(optionalTypes.Select(t => prefix + "Optional<" + t + ">"));
        types.Add(_element);
        var attributes = new StringBuilder(16384);
        var propertyNames = new HashSet<string>(StringComparer.Ordinal) { "Default", "Options", "GetTypeInfo", "GeneratedSerializerOptions", "Boolean", "Int64", "UInt64", "Decimal", "String", "JsonElement" };
        foreach (string type in types.OrderBy(t => t, StringComparer.Ordinal))
        {
            string propertyName = CSharpNames.Unique(type.Replace(prefix + "Models.", "").Replace(prefix, "")
                .Replace("global::System.Collections.Generic.", "").Replace("global::System.", ""), propertyNames);
            attributes.AppendLine("[global::System.Text.Json.Serialization.JsonSerializable(typeof(" + type + ")" +
                (type is "bool" or "long" or "ulong" or "decimal" or "string" or _element ? "" : ", TypeInfoPropertyName = \"" + propertyName + "\"") + ")]");
        }
        _files["SchemaJsonContext.cs"] = Template("SchemaJsonContext").Replace("@@NAMESPACE@@", _options.Namespace)
            .Replace("@@TYPES@@", attributes.ToString());
    }

    internal string GetType(JsonNode schema, string name)
    {
        if (!_scopes.ContainsKey(schema)) Index(schema, _baseUri);
        return Type(schema, name);
    }
    internal static string Template(string name)
    {
        using Stream stream =
            typeof(SchemaEmitter).Assembly.GetManifestResourceStream("JsonSchema.Templates." + name + ".txt") ??
            throw new InvalidOperationException("Missing generation template: " + name);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private void Index(JsonNode schema, Uri scope)
    {
        _ct.ThrowIfCancellationRequested();
        if (schema is JsonObject obj)
        {
            if (obj["$schema"] is JsonValue dialect && dialect.TryGetValue(out string? dialectText) &&
                dialectText?.TrimEnd('#') is not ("http://json-schema.org/draft-07/schema" or "http://json-schema.org/draft-06/schema"))
                throw new NotSupportedException("Supported JSON Schema dialects are draft 6 and draft 7; found " + dialectText);
            if (obj["$id"] is JsonValue id && id.TryGetValue(out string? idText))
            {
                scope = new Uri(scope, idText);
                if (!_ids.TryAdd(scope.AbsoluteUri, schema))
                    throw new ArgumentException("Duplicate schema $id: " + scope);
            }
        }

        _scopes[schema] = scope;
        foreach (JsonNode child in Children(schema))
            Index(child, scope);
    }

    private static IEnumerable<JsonNode> Children(JsonNode schema)
    {
        if (schema is not JsonObject obj)
            yield break;
        foreach (string key in new[] { "properties", "patternProperties", "definitions", "$defs", "dependencies" })
            if (obj[key] is JsonObject map)
                foreach (JsonNode? child in map.Select(x => x.Value))
                    if (child is JsonObject || child?.GetValueKind() is JsonValueKind.True or JsonValueKind.False)
                        yield return child;
        foreach (string key in new[]
                 {
                     "additionalProperties", "additionalItems", "contains", "propertyNames", "not", "if", "then",
                     "else", "items"
                 })
            if (obj[key] is JsonObject || obj[key]?.GetValueKind() is JsonValueKind.True or JsonValueKind.False)
                yield return obj[key]!;
        foreach (string key in new[] { "allOf", "anyOf", "oneOf", "items" })
            if (obj[key] is JsonArray array)
                foreach (JsonNode? child in array)
                    if (child != null)
                        yield return child;
    }

    private JsonNode Resolve(JsonNode schema, string reference)
    {
        Uri target = new(_scopes.GetValueOrDefault(schema, _baseUri), reference);
        if (_ids.TryGetValue(target.AbsoluteUri, out JsonNode? direct))
            return direct;
        string resource = target.GetLeftPart(UriPartial.Query);
        if (!_ids.TryGetValue(resource, out JsonNode? root))
        {
            // A root $id may contain an empty fragment.
            if (!_ids.TryGetValue(resource + "#", out root))
                throw new NotSupportedException(
                    $"Reference '{reference}' is not in this document. Bundle external schemas before generating.");
        }

        string fragment = Uri.UnescapeDataString(target.Fragment);
        if (fragment.Length == 0 || fragment == "#")
            return root;
        if (!fragment.StartsWith("#/", StringComparison.Ordinal))
            throw new NotSupportedException("Unresolved schema anchor: " + reference);
        JsonNode? current = root;
        foreach (string part in fragment[2..].Split('/'))
        {
            string key = part.Replace("~1", "/").Replace("~0", "~");
            current = current switch
            {
                JsonObject map => map[key],
                JsonArray list when int.TryParse(key, out int index) && index >= 0 && index < list.Count => list[index],
                _ => null
            };
            if (current == null)
                throw new ArgumentException("Unresolved $ref: " + reference);
        }

        return current!;
    }

    private string Type(JsonNode schema, string suggestion)
    {
        _ct.ThrowIfCancellationRequested();
        if (_types.TryGetValue(schema, out string? cached))
            return cached;
        if (!_resolving.Add(schema))
            return Fallback(schema, "Recursive alias without an object boundary");
        try
        {
            string result = BuildType(schema, suggestion);
            _types[schema] = result;
            return result;
        }
        finally
        {
            _resolving.Remove(schema);
        }
    }

    private string BuildType(JsonNode schema, string suggestion)
    {
        if (schema is not JsonObject obj)
            return schema.GetValueKind() == JsonValueKind.True ? _element : Fallback(schema, "Unsatisfiable boolean schema");
        if (obj["$ref"] is JsonValue reference)
        {
            string text = reference.GetValue<string>();
            JsonNode target = Resolve(schema, text);
            // Draft 7 ignores all siblings of $ref.
            return Type(target, ReferenceName(target, text, suggestion));
        }

        foreach (string key in new[] { "anyOf", "oneOf" })
            if (obj[key] is JsonArray union)
            {
                JsonNode[] nonNull = union.Where(x => x != null && !IsNull(x)).Select(x => x!).ToArray();
                if (nonNull.Length == 1 && union.Count == 2 && !obj.ContainsKey("type"))
                    return Nullable(Type(nonNull[0], suggestion));
                if (union.Count == 1) return Type(union[0]!, suggestion);
                return EmitUnion(schema, suggestion, union, key == "oneOf");
            }

        if (obj["allOf"] is JsonArray allOf)
        {
            if (allOf.Count == 1 && !obj.ContainsKey("properties") && !obj.ContainsKey("type"))
                return Type(allOf[0]!, suggestion);
            var parts = new List<JsonObject>();
            if (ObjectParts(schema, parts, []) && parts.Any(p => HasObjectType(p)))
                return EmitObject(schema, suggestion, obj, parts);
            return Fallback(schema, "allOf intersection without a single object shape");
        }

        List<string> types = obj["type"] switch
        {
            JsonValue value => [value.GetValue<string>()],
            JsonArray array => array.Select(x => x!.GetValue<string>()).ToList(),
            _ => []
        };
        bool nullable = types.Remove("null");
        if (types.Count > 1)
            {
                var alternatives = new JsonArray();
                foreach (string type in types)
                {
                    JsonObject branch = obj.DeepClone().AsObject();
                    branch["type"] = type;
                    alternatives.Add(branch);
                }
                if (nullable) alternatives.Add(new JsonObject { ["type"] = "null" });
                return EmitUnion(schema, suggestion, alternatives, false);
            }
        string? kind = types.SingleOrDefault();
        if (obj["enum"] is JsonArray values && values.Count > 0 &&
            values.All(x => x == null || x.GetValueKind() == JsonValueKind.String) && values.Any(x => x != null) &&
            (kind is null or "string"))
        {
            nullable = kind == null ? values.Any(x => x == null) : nullable && values.Any(x => x == null);
            string enumType = EmitEnum(schema, suggestion, values);
            return nullable ? Nullable(enumType) : enumType;
        }

        string result = kind switch
        {
            "object" => EmitObject(schema, suggestion, obj),
            "array" => EmitArray(schema, suggestion, obj),
            "string" => "string",
            "integer" => obj["format"]?.ToString() is "uint64" or "uint" ? "ulong" : "long",
            "number" => "decimal",
            "boolean" => "bool",
            _ when nullable || obj.Count == 0 || obj.All(p => p.Key is "description" or "title" or "default" or "$schema" or "$id" or "id" or "definitions" or "$defs" or "examples") => _element,
            _ when _options.InferTypesFromKeywords && obj.ContainsKey("pattern") => "string",
            _ when _options.InferTypesFromKeywords && obj.ContainsKey("properties") => EmitObject(schema, suggestion, obj),
            _ when obj["const"] is JsonValue constant && constant.TryGetValue<string>(out _) => EmitEnum(schema, suggestion, new JsonArray(obj["const"]!.DeepClone())),
            _ => Fallback(schema, "Unconstrained or inferred schema")
        };
        return nullable && result != _element ? Nullable(result) : result;
    }

    private string ReferenceName(JsonNode target, string reference, string fallback)
    {
        if (ReferenceEquals(target, _root))
            return "Root";
        string tail = reference.Split('/').Last();
        return tail is "#" or "" ? fallback : Uri.UnescapeDataString(tail).Replace("~1", "/").Replace("~0", "~");
    }

    private string EmitUnion(JsonNode schema, string suggestion, JsonArray union, bool exclusive)
    {
        string name = CSharpNames.Unique(suggestion, _names);
        string converter = CSharpNames.Unique(name + "JsonConverter", _names);
        string full = "global::" + _options.Namespace + ".Models." + name;
        _types[schema] = full;
        JsonObject common = schema.DeepClone().AsObject();
        common.Remove("oneOf");
        common.Remove("anyOf");
        bool hasCommonShape = common.ContainsKey("properties") || common.ContainsKey("required");
        (JsonNode Node, int Index, string Type)[] variants = union.Select((node, index) =>
        {
            JsonNode variant = hasCommonShape
                ? new JsonObject { ["allOf"] = new JsonArray(common.DeepClone(), node!.DeepClone()) }
                : node!;
            return (Node: variant, Index: index + 1, Type: GetType(variant, name + "Variant" + (index + 1)));
        }).ToArray();
        var code = new StringBuilder(4096);
        code.AppendLine("// <auto-generated/>\n#nullable enable\nnamespace " + _options.Namespace + ".Models;");
        code.AppendLine("/// <summary>A schema union. Variant identifies the selected alternative; AsVariant methods expose typed values.</summary>");
        code.AppendLine("[global::System.Text.Json.Serialization.JsonConverter(typeof(" + converter + "))]");
        code.AppendLine("public sealed class " + name + "\n{");
        code.AppendLine("    private readonly object? _value;\n    public int Variant { get; }\n    internal " + name + "(int variant, object? value) { Variant = variant; _value = value; }");
        foreach ((JsonNode Node, int Index, string Type) variant in variants)
        {
            code.AppendLine("    public static " + name + " FromVariant" + variant.Index + "(" + variant.Type + " value) => new(" + variant.Index + ", value);");
            code.AppendLine("    public " + variant.Type + " AsVariant" + variant.Index + "() => Variant == " + variant.Index + " ? (" + variant.Type + ")_value! : throw new global::System.InvalidOperationException(\"Different union variant.\");");
        }
        code.AppendLine("}");
        _files["Models/" + name + ".cs"] = code.ToString();
        code.Clear();
        code.AppendLine("// <auto-generated/>\n#nullable enable\nnamespace " + _options.Namespace + ".Models;");
        code.AppendLine("internal sealed class " + converter + " : global::System.Text.Json.Serialization.JsonConverter<" + name + ">\n{");
        code.AppendLine("    public override bool HandleNull => true;");
        code.AppendLine("    public override " + name + " Read(ref global::System.Text.Json.Utf8JsonReader reader, global::System.Type type, global::System.Text.Json.JsonSerializerOptions options)\n    {\n        var document = global::System.Text.Json.JsonDocument.ParseValue(ref reader);\n        var value = document.RootElement;\n        int selected = 0, matches = 0;");
        foreach ((JsonNode Node, int Index, string Type) variant in variants)
            code.AppendLine("        if (global::" + _options.Namespace + ".UnionMatchers." + _matchers.Register(variant.Node) + "(value)) { if (selected == 0) selected = " + variant.Index + "; matches++; }");
        code.AppendLine("        if (matches == 0" + (exclusive ? " || matches != 1" : "") + ") throw new global::System.Text.Json.JsonException(\"Value does not match the schema union.\");\n        return selected switch\n        {");
        foreach ((JsonNode Node, int Index, string Type) variant in variants)
            code.AppendLine("            " + variant.Index + " => " + name + ".FromVariant" + variant.Index + "(global::System.Text.Json.JsonSerializer.Deserialize(value, global::" + _options.Namespace + ".JsonTypes.Get<" + variant.Type + ">(options))!),");
        code.AppendLine("            _ => throw new global::System.Text.Json.JsonException()\n        };\n    }");
        code.AppendLine("    public override void Write(global::System.Text.Json.Utf8JsonWriter writer, " + name + " value, global::System.Text.Json.JsonSerializerOptions options)\n    {\n        global::System.ArgumentNullException.ThrowIfNull(value);\n        switch (value.Variant)\n        {");
        foreach ((JsonNode Node, int Index, string Type) variant in variants)
            code.AppendLine("            case " + variant.Index + ": global::System.Text.Json.JsonSerializer.Serialize(writer, value.AsVariant" + variant.Index + "(), global::" + _options.Namespace + ".JsonTypes.Get<" + variant.Type + ">(options)); return;");
        code.AppendLine("            default: throw new global::System.Text.Json.JsonException();\n        }\n    }\n}");
        _files["Models/" + converter + ".cs"] = code.ToString();
        return full;
    }

    private string EmitArray(JsonNode schema, string suggestion, JsonObject obj)
    {
        string item = obj["items"] switch
        {
            JsonObject child => Type(child, suggestion + "Item"),
            JsonValue child => Type(child, suggestion + "Item"),
            _ => _element
        };
        if (obj["items"] is JsonArray)
            _diagnostics.Add(Location(schema) + ": Tuple items are preserved as JsonElement values.");
        return "global::System.Collections.Generic.List<" + item + ">";
    }

    private bool ObjectParts(JsonNode schema, List<JsonObject> parts, HashSet<JsonNode> visiting)
    {
        if (!visiting.Add(schema))
            return false;
        try
        {
            if (schema is not JsonObject obj)
                return schema.GetValueKind() == JsonValueKind.True;
            if (obj["$ref"] is JsonValue reference)
                return ObjectParts(Resolve(schema, reference.GetValue<string>()), parts, visiting);
            if (obj.ContainsKey("anyOf") || obj.ContainsKey("oneOf"))
                return false;
            if (obj["type"] != null && !HasObjectType(obj))
                return false;
            parts.Add(obj);
            return obj["allOf"] is not JsonArray all || all.All(x => x != null && ObjectParts(x, parts, visiting));
        }
        finally
        {
            visiting.Remove(schema);
        }
    }

    private static bool HasObjectType(JsonObject obj) =>
        obj["type"] is JsonValue value && value.TryGetValue(out string? type) && type == "object";

    private string EmitObject(JsonNode schema, string suggestion, JsonObject obj, List<JsonObject>? parts = null)
    {
        if (parts == null && !ReferenceEquals(schema, _root) && obj["properties"] == null &&
            obj["patternProperties"] == null && obj["additionalProperties"] is JsonObject items)
            return "global::System.Collections.Generic.Dictionary<string, " + Type(items, suggestion + "Value") + ">";
        string name = CSharpNames.Unique(suggestion, _names);
        string fullName = "global::" + _options.Namespace + ".Models." + name;
        _types[schema] = obj["type"] is JsonArray types && types.Any(x => x?.GetValue<string>() == "null")
            ? Nullable(fullName)
            : fullName;
        var code = new StringBuilder(2048);
        code.Append("// <auto-generated/>\n#nullable enable\nnamespace " + _options.Namespace +
                                     ".Models;\n\n");
        string description = obj["description"]?.GetValue<string>() ?? "A generated schema model.";
        code.AppendLine("/// <summary>" + CSharpNames.Xml(description) + "</summary>");
        code.AppendLine("public sealed class " + name + "\n{");
        var memberNames = new HashSet<string>(StringComparer.Ordinal) { name, "AdditionalProperties" };
        var required = new HashSet<string>(StringComparer.Ordinal);
        var properties = new SortedDictionary<string, List<JsonNode>>(StringComparer.Ordinal);
        foreach (JsonObject part in parts ?? [obj])
        {
            if (part["required"] is JsonArray requiredNames)
                required.UnionWith(requiredNames.Select(x => x!.GetValue<string>()));
            if (part["properties"] is not JsonObject map)
                continue;
            foreach ((string key, JsonNode? value) in map)
            {
                if (value == null)
                    throw new ArgumentException("Property schema cannot be null: " + key);
                if (!properties.TryGetValue(key, out List<JsonNode>? list))
                    properties[key] = list = [];
                list.Add(value);
            }
        }

        foreach ((string jsonName, List<JsonNode> candidates) in properties)
        {
            List<JsonNode> constrained = candidates.Where(x => x is not JsonObject o || o.Any(p => p.Key is not ("title" or "description" or "default" or "examples" or "version" or "features"))).ToList();
            JsonNode property = constrained.FirstOrDefault() ?? candidates[0];
            string member = CSharpNames.Unique(jsonName, memberNames);
            // Resolve aliases before comparing inherited declarations. Different constraints must not be silently discarded.
            List<JsonNode> resolved = constrained.Select(x => x is JsonObject o && o["$ref"] is JsonValue r ? Resolve(x, r.GetValue<string>()) : x).ToList();
            string type = resolved.Count < 2 || resolved.All(x => JsonNode.DeepEquals(resolved[0], x))
                ? Type(property, name + member)
                : Fallback(property, "Intersecting property schemas");
            bool isRequired = required.Contains(jsonName);
            if (property is JsonObject p && p["description"] is JsonValue desc)
                code.AppendLine("    /// <summary>" + CSharpNames.Xml(desc.GetValue<string>()) + "</summary>");
            code.AppendLine("    [global::System.Text.Json.Serialization.JsonPropertyName(" +
                            CSharpNames.Literal(jsonName) + ")]");
            if (!isRequired)
            {
                RegisterOptional(type);
                code.AppendLine(
                    "    [global::System.Text.Json.Serialization.JsonIgnore(Condition = global::System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]");
                type = "global::" + _options.Namespace + ".Optional<" + type + ">";
            }

            code.AppendLine("    public " + (isRequired ? "required " : "") + type + " " + member + " { get; set; }\n");
        }

        // Preserve additional and pattern properties without dropping payload data.
        code.AppendLine("    [global::System.Text.Json.Serialization.JsonExtensionData]");
        code.AppendLine(
            "    public global::System.Collections.Generic.Dictionary<string, global::System.Text.Json.JsonElement>? AdditionalProperties { get; set; }");
        code.AppendLine("}");
        _files["Models/" + name + ".cs"] = code.ToString();
        return fullName;
    }

    private string EmitEnum(JsonNode schema, string suggestion, JsonArray values)
    {
        string name = CSharpNames.Unique(suggestion, _names);
        string converter = CSharpNames.Unique(name + "JsonConverter", _names);
        var code = new StringBuilder(2048);
        code.Append("// <auto-generated/>\n#nullable enable\nnamespace " + _options.Namespace +
                                     ".Models;\n\n");
        code.AppendLine("[global::System.Text.Json.Serialization.JsonConverter(typeof(" + converter + "))]");
        code.AppendLine("public enum " + name + "\n{");
        var members = new HashSet<string>(StringComparer.Ordinal) { name, "value__" };
        var mapping = new List<(string Json, string Member)>();
        foreach (JsonNode value in values.Where(x => x != null).Select(x => x!))
        {
            string text = value.GetValue<string>();
            if (mapping.Any(x => x.Json == text))
                throw new ArgumentException("Duplicate enum value at " + Location(schema));
            string member = CSharpNames.Unique(text, members);
            mapping.Add((text, member));
            code.AppendLine("    [global::System.Text.Json.Serialization.JsonStringEnumMemberName(" +
                            CSharpNames.Literal(text) + ")]");
            code.AppendLine("    " + member + ",");
        }

        code.AppendLine("}");
        _files["Models/" + name + ".cs"] = code.ToString();
        code.Clear();
        code.AppendLine("// <auto-generated/>\n#nullable enable\nnamespace " + _options.Namespace + ".Models;");
        code.AppendLine("internal sealed class " + converter +
                        " : global::System.Text.Json.Serialization.JsonConverter<" + name + ">\n{");
        code.AppendLine("    public override " + name +
                        " Read(ref global::System.Text.Json.Utf8JsonReader reader, global::System.Type typeToConvert, global::System.Text.Json.JsonSerializerOptions options)\n    {");
        code.AppendLine(
            "        if (reader.TokenType != global::System.Text.Json.JsonTokenType.String) throw new global::System.Text.Json.JsonException(\"Expected an enum string.\");");
        foreach ((string json, string member) in mapping)
            code.AppendLine("        if (reader.ValueTextEquals(" + CSharpNames.Literal(json) + "u8)) return " + name + "." + member + ";");
        code.AppendLine("        throw new global::System.Text.Json.JsonException(\"Unknown enum value.\");\n    }");
        code.AppendLine("    public override void Write(global::System.Text.Json.Utf8JsonWriter writer, " + name +
                        " value, global::System.Text.Json.JsonSerializerOptions options)\n    {");
        code.AppendLine("        writer.WriteStringValue(value switch\n        {");
        foreach ((string json, string member) in mapping)
            code.AppendLine("            " + name + "." + member + " => " + CSharpNames.Literal(json) + ",");
        code.AppendLine(
            "            _ => throw new global::System.Text.Json.JsonException(\"Unknown enum value.\")\n        });\n    }\n}");
        _files["Models/" + converter + ".cs"] = code.ToString();
        string fullName = "global::" + _options.Namespace + ".Models." + name;
        _valueTypes.Add(fullName);
        return fullName;
    }

    private string Fallback(JsonNode schema, string reason)
    {
        string diagnostic = Location(schema) + ": " + reason +
                            " uses JsonElement; constraints remain in the source schema.";
        if (!_diagnostics.Contains(diagnostic))
            _diagnostics.Add(diagnostic);
        return _element;
    }

    private static string Location(JsonNode node) => node.GetPath();

    private static bool IsNull(JsonNode node) => node is JsonObject obj && obj["type"] is JsonValue value &&
                                                 value.TryGetValue(out string? type) && type == "null";

    private static string Nullable(string type) => type.EndsWith('?') || type == _element ? type : type + "?";
}



