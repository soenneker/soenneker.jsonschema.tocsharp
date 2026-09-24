using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Soenneker.JsonSchema.ToCSharp.Internal;

[JsonSerializable(typeof(JsonNode))]
[JsonSerializable(typeof(string))]
internal partial class GeneratorJsonContext : JsonSerializerContext;
