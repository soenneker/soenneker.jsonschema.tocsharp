using System.Collections.Generic;
namespace Soenneker.JsonSchema.ToCSharp;
/// <summary>Generated sources and type mappings. Generated code requires only the .NET 10 framework.</summary>
/// <param name="Files">Relative output paths mapped to C# source.</param>
/// <param name="RootType">The C# root type, which may be a primitive or collection.</param>
/// <param name="NamedTypes">Definition JSON pointers mapped to C# types.</param>
/// <param name="Diagnostics">Lossy type mapping diagnostics. This generator is not a full schema validator.</param>
public sealed record JsonSchemaToCSharpResult(IReadOnlyDictionary<string, string> Files, string RootType,
    IReadOnlyDictionary<string, string> NamedTypes, IReadOnlyList<string> Diagnostics);
