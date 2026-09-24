using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Soenneker.JsonSchema.ToCSharp.Abstract;
using Soenneker.Utils.File.Registrars;

namespace Soenneker.JsonSchema.ToCSharp.Registrars;

/// <summary>
/// Generates System.Text.Json C# models from JSON Schema documents
/// </summary>
public static class JsonSchemaToCSharpRegistrar
{
    /// <summary>
    /// Adds <see cref="IJsonSchemaToCSharp"/> as a singleton service. <para/>
    /// </summary>
    public static IServiceCollection AddJsonSchemaToCSharpAsSingleton(this IServiceCollection services)
    {
        services.AddFileUtilAsSingleton().TryAddSingleton<IJsonSchemaToCSharp, JsonSchemaToCSharp>();

        return services;
    }

    /// <summary>
    /// Adds <see cref="IJsonSchemaToCSharp"/> as a scoped service. <para/>
    /// </summary>
    public static IServiceCollection AddJsonSchemaToCSharpAsScoped(this IServiceCollection services)
    {
        services.AddFileUtilAsScoped().TryAddScoped<IJsonSchemaToCSharp, JsonSchemaToCSharp>();

        return services;
    }
}
