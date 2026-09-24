using System;
using System.Collections.Generic;
using System.Security;
using Soenneker.Utils.PooledStringBuilders;
using Soenneker.Utils.Json;
using System.Text.RegularExpressions;

namespace Soenneker.JsonSchema.ToCSharp.Internal;

internal static class CSharpNames
{
    private static readonly HashSet<string> _keywords = new(("abstract as base bool break byte case catch char checked class const continue decimal default " +
        "delegate do double else enum event explicit extern false finally fixed float for foreach goto if implicit in int interface internal is lock long " +
        "namespace new null object operator out override params private protected public readonly ref return sbyte sealed short sizeof stackalloc static " +
        "string struct switch this throw true try typeof uint ulong unchecked unsafe ushort using virtual void volatile while record required file").Split(' '));

    internal static void Validate(string name, string parameter, bool allowDots = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name, parameter);
        foreach (string part in allowDots ? name.Split('.') : [name])
            if (!Regex.IsMatch(part, @"\A[A-Za-z_][A-Za-z0-9_]*\z") || _keywords.Contains(part))
                throw new ArgumentException($"'{name}' is not a supported C# identifier.", parameter);
    }

    internal static string Identifier(string value)
    {
        using var result = new PooledStringBuilder(value.Length + 1);
        bool upper = true;
        foreach (char c in value)
        {
            if (!char.IsAsciiLetterOrDigit(c)) { upper = true; continue; }
            if (result.Length == 0 && char.IsAsciiDigit(c)) result.Append('_');
            result.Append(upper ? char.ToUpperInvariant(c) : c);
            upper = false;
        }
        return result.Length == 0 ? "Value" : result.ToString();
    }

    internal static string Unique(string suggestion, HashSet<string> used)
    {
        string name = Identifier(suggestion);
        string candidate = name;
        for (int i = 2; !used.Add(candidate); i++) candidate = name + i;
        return candidate;
    }

    internal static string Literal(string value) => JsonUtil.Serialize(value, GeneratorJsonContext.Default.String);
    internal static string Xml(string value) => SecurityElement.Escape(value)!.Replace("\r", " ").Replace("\n", " ");
}

