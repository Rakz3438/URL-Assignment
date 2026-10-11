using System.Reflection;
using UrlShortener.Application.Links;
using UrlShortener.Domain.Links;

namespace UrlShortener.UnitTests.Architecture;

/// <summary>Enforces the Clean Architecture dependency rule from CLAUDE.md, so AI-generated code
/// (or anyone else's) cannot quietly break layering.</summary>
public class LayerDependencyTests
{
    [Fact]
    public void Domain_ReferencesOnlyTheBaseClassLibrary()
    {
        var references = typeof(ShortLink).Assembly.GetReferencedAssemblies().Select(a => a.Name!);

        references.Should().OnlyContain(n => n == "System.Runtime" || n.StartsWith("System.", StringComparison.Ordinal) || n == "netstandard",
            "Domain must not depend on frameworks or other layers");
    }

    [Fact]
    public void Application_DoesNotReferenceInfrastructureOrFrameworks()
    {
        var references = typeof(LinkService).Assembly.GetReferencedAssemblies().Select(a => a.Name!).ToList();

        references.Should().NotContain(n => n.StartsWith("UrlShortener.Infrastructure", StringComparison.Ordinal));
        references.Should().NotContain(n => n.StartsWith("UrlShortener.Api", StringComparison.Ordinal));
        references.Should().NotContain(n => n.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal));
        references.Should().NotContain(n => n.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal));
        references.Should().NotContain(n => n.StartsWith("Npgsql", StringComparison.Ordinal));
    }

    [Fact]
    public void Infrastructure_InternalsAreNotPublic()
    {
        var publicTypes = Assembly.Load("UrlShortener.Infrastructure").GetExportedTypes()
            .Where(t => t.Namespace?.Contains(".Migrations", StringComparison.Ordinal) != true)
            .Select(t => t.Name);

        // Only composition and DbContext types are public; implementations stay internal behind ports.
        publicTypes.Should().BeSubsetOf(
            ["DependencyInjection", "DatabaseProvider", "LinkDbContext", "SqliteLinkDbContext", "PostgresLinkDbContext"]);
    }
}
