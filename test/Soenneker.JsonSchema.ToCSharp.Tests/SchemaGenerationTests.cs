using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Soenneker.JsonSchema.ToCSharp.Tests;

public sealed class SchemaGenerationTests
{
    private readonly JsonSchemaToCSharp _generator = new();

    [Test]
    public async Task Unions_use_string_and_numeric_constraints()
    {
        var result = _generator.Generate("""
            {"title":"Choice","oneOf":[
              {"type":"string","pattern":"^a","maxLength":3},
              {"type":"string","pattern":"^b","minLength":2},
              {"type":"number","exclusiveMinimum":0,"maximum":10,"multipleOf":2},
              {"type":"number","minimum":20,"exclusiveMaximum":30}]}
            """, new() { Namespace = "Example" });
        await CompileAndRun(result, """
            using System.Text.Json;
            using Example;
            using Example.Models;
            foreach (string json in new[] { "\"abc\"", "\"bb\"", "2", "20" })
                JsonSerializer.Deserialize<Choice>(json, SchemaJsonContext.Default.Options);
            foreach (string json in new[] { "\"abcd\"", "\"b\"", "\"zzz\"", "3", "0", "30" })
            {
                try { JsonSerializer.Deserialize<Choice>(json, SchemaJsonContext.Default.Options); }
                catch (JsonException) { continue; }
                throw new System.Exception("Invalid union accepted: " + json);
            }
            """);
    }

    [Test]
    public async Task Definitions_maps_allOf_and_nullable_enums_compile()
    {
        var result = _generator.Generate("""
            {"definitions":{
              "Base":{"type":"object","properties":{"name":{"type":"string"}},"required":["name"]},
              "Derived":{"type":"object","allOf":[{"$ref":"#/definitions/Base"}],"properties":{"name":{},"state":{"type":["string","null"],"enum":["a","b",null]}}},
              "Map":{"type":"object","additionalProperties":{"$ref":"#/definitions/Derived"}},
              "a/b~c":{"type":"object","properties":{"mixed":{"type":["string","integer","null"]}}},
              "Alias":{"$ref":"#/definitions/a~1b~0c"}}}
            """, new() { Namespace = "Example" });
        Check(result.RootType == "global::System.Text.Json.JsonElement", "Definitions-only root must remain unconstrained");
        Check(result.NamedTypes["#/definitions/a~1b~0c"] == result.NamedTypes["#/definitions/Alias"], "Escaped reference mismatch");
        await CompileAndRun(result, """
            using System.Text.Json;
            using Example;
            using Example.Models;
            var value = JsonSerializer.Deserialize<Derived>("{\"name\":\"test\",\"state\":null}", SchemaJsonContext.Default.Options)!;
            if (value.Name != "test" || !value.State.IsDefined || value.State.Value != null) throw new System.Exception("Inherited/nullable property");
            """);
    }

    [Test]
    public async Task AdaptiveCards_compile_and_round_trip()
    {
        // Microsoft AdaptiveCards, MIT: schemas/1.5.0/adaptive-card.json, retrieved 2026-09-24.
        string json = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "adaptive-card-1.5.json"));
        JsonSchemaToCSharpResult result = _generator.Generate(json, new() { Namespace = "Cards" });
        Check(result.Diagnostics.Count == 0, string.Join('\n', result.Diagnostics));
        string root = result.NamedTypes["#/definitions/AdaptiveCard"];
        await CompileAndRun(result, $$""""
            using System.Text.Json;
            const string json = """{"type":"AdaptiveCard","version":"1.5","body":[{"type":"TextBlock","text":"Hello","wrap":true},{"type":"ColumnSet","columns":[{"type":"Column","items":[{"type":"FactSet","facts":[{"title":"Status","value":"Ready"}]}]}]}],"actions":[{"type":"Action.OpenUrl","title":"Open","url":"https://example.com"}]}""";
            var value = JsonSerializer.Deserialize<{{root}}>(json, Cards.SchemaJsonContext.Default.Options)!;
            string serialized = JsonSerializer.Serialize(value, Cards.SchemaJsonContext.Default.Options);
            if (!JsonElement.DeepEquals(JsonDocument.Parse(json).RootElement, JsonDocument.Parse(serialized).RootElement))
                throw new System.Exception(serialized);
            """");
    }

    [Test]
    public async Task Recursive_models_optional_null_enums_and_unions_round_trip()
    {
        const string schema = """
            {"type":"object","title":"Document","required":["name","choice"],"properties":{
              "name":{"type":"string"},"note":{"type":["string","null"]},
              "state":{"enum":["in-progress","done"]},"child":{"$ref":"#"},
              "choice":{"oneOf":[{"type":"string"},{"type":"integer"}]},
              "values":{"type":"array","items":{"type":"boolean"}}}}
            """;
        var result = _generator.Generate(schema, new() { Namespace = "Example" });
        await CompileAndRun(result, """"
            using System.Text.Json;
            using Example;
            using Example.Models;
            const string json = """{"name":"root","note":null,"state":"in-progress","choice":42,"child":{"name":"child","choice":"text"}}""";
            var value = JsonSerializer.Deserialize<Document>(json, SchemaJsonContext.Default.Options)!;
            if (!value.Note.IsDefined || value.Note.Value != null || value.Values.IsDefined) throw new System.Exception("Optional semantics");
            string output = JsonSerializer.Serialize(value, SchemaJsonContext.Default.Options);
            if (!JsonElement.DeepEquals(JsonDocument.Parse(json).RootElement, JsonDocument.Parse(output).RootElement)) throw new System.Exception(output);
            try { JsonSerializer.Deserialize<Document>("{\"name\":\"bad\",\"choice\":true}", SchemaJsonContext.Default.Options); }
            catch (JsonException) { return; }
            throw new System.Exception("Invalid union accepted");
            """");
    }

    [Test]
    public void References_dialects_and_diagnostics()
    {
        var result = _generator.Generate("""{"$ref":"https://example.com/types.json#/$defs/Value"}""", new()
        {
            ExternalSchemas = new Dictionary<string, string> { ["https://example.com/types.json"] = """{"$defs":{"Value":{"type":"object","properties":{"id":{"type":"integer"}}}}}""" }
        });
        Check(result.RootType.EndsWith(".Value", StringComparison.Ordinal), result.RootType);
        Expect<NotSupportedException>(() => _generator.Generate("""{"$schema":"https://json-schema.org/draft/2020-12/schema","type":"object"}"""));
        Expect<NotSupportedException>(() => _generator.Generate("""{"$ref":"https://example.com/missing"}"""));
        Expect<NotSupportedException>(() => _generator.Generate("""{"type":"array","items":[{"type":"string"}]}"""));
        var fallback = _generator.Generate("""{"type":"array","items":[{"type":"string"}]}""", new() { FailOnUntypedSchemas = false });
        Check(fallback.Diagnostics.Count == 1, "Expected tuple diagnostic");
        Expect<OperationCanceledException>(() => _generator.Generate("{}", cancellationToken: new CancellationToken(true)));
    }

    [Test]
    public async Task Files_require_explicit_overwrite()
    {
        string directory = Path.Combine(Path.GetTempPath(), "schema-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string input = Path.Combine(directory, "schema.json");
            string output = Path.Combine(directory, "output");
            await File.WriteAllTextAsync(input, "{\"type\":\"object\"}");
            var first = await _generator.GenerateFile(input, output);
            string path = Path.Combine(output, first.Files.Keys.First());
            await File.WriteAllTextAsync(path, "sentinel");
            try { await _generator.GenerateFile(input, output); throw new Exception("Overwrite accepted"); }
            catch (IOException) { }
            Check(await File.ReadAllTextAsync(path) == "sentinel", "Existing output changed");
            await _generator.GenerateFile(input, output, new() { Overwrite = true });
            Check(await File.ReadAllTextAsync(path) != "sentinel", "Explicit overwrite failed");
        }
        finally { Directory.Delete(directory, true); }
    }

    private static async Task CompileAndRun(JsonSchemaToCSharpResult result, string program)
    {
        string directory = Path.Combine(Path.GetTempPath(), "schema-consumer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            foreach ((string relative, string source) in result.Files)
            {
                string path = Path.Combine(directory, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllTextAsync(path, source);
            }
            await File.WriteAllTextAsync(Path.Combine(directory, "Program.cs"), program);
            await File.WriteAllTextAsync(Path.Combine(directory, "Consumer.csproj"), """"
                <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType>
                <Nullable>enable</Nullable><JsonSerializerIsReflectionEnabledByDefault>false</JsonSerializerIsReflectionEnabledByDefault>
                </PropertyGroup></Project>
                """");
            using var process = Process.Start(new ProcessStartInfo("dotnet", "run --project Consumer.csproj --verbosity quiet")
            { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true })!;
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch { process.Kill(true); await process.WaitForExitAsync(); throw; }
            Check(process.ExitCode == 0, await stdout + await stderr);
        }
        finally { Directory.Delete(directory, true); }
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Expect<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }
}

