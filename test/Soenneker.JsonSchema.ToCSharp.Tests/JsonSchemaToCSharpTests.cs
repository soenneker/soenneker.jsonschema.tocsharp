using Soenneker.JsonSchema.ToCSharp.Abstract;
using Soenneker.Tests.HostedUnit;

namespace Soenneker.JsonSchema.ToCSharp.Tests;

[ClassDataSource<Host>(Shared = SharedType.PerTestSession)]
public sealed class JsonSchemaToCSharpTests : HostedUnitTest
{
    private readonly IJsonSchemaToCSharp _util;

    public JsonSchemaToCSharpTests(Host host) : base(host)
    {
        _util = Resolve<IJsonSchemaToCSharp>(true);
    }

    [Test]
    public void Registrar_resolves_generator()
    {
        if (_util is not JsonSchemaToCSharp) throw new System.Exception("Generator registration failed.");
    }
}
