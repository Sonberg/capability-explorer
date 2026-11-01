using CapabilityExplorer.Extractors.CSharp;
using Xunit;

namespace CapabilityExplorer.Extractors.Tests;

public sealed class ProjectSelectionTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("ce-project-selection-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Test_projects_are_excluded_by_default_and_available_when_requested()
    {
        Project("src/App/App.csproj");
        Project("tests/App.Tests/App.Tests.csproj", "<PackageReference Include=\"Microsoft.NET.Test.Sdk\" Version=\"18.0.1\" />");
        Project("src/Explicit/Explicit.csproj", "<IsTestProject>true</IsTestProject>");
        Project("src/Named/Widget.IntegrationTests.csproj");
        Project("tests/OptIn/Production.Tests.csproj", "<IsTestProject>false</IsTestProject>");

        var production = ProjectModel.Discover(_root, includeTests: false);
        Assert.Equal(["src/App/App.csproj", "tests/OptIn/Production.Tests.csproj"], production.Select(p => p.RelativePath).Order(StringComparer.Ordinal));

        var all = ProjectModel.Discover(_root, includeTests: true);
        Assert.Equal(5, all.Count);
        Assert.Equal(3, all.Count(p => p.IsTestProject));
    }

    private void Project(string relativePath, string content = "")
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var item = content.StartsWith("<PackageReference", StringComparison.Ordinal) ? $"<ItemGroup>{content}</ItemGroup>" : $"<PropertyGroup>{content}</PropertyGroup>";
        File.WriteAllText(path, $"<Project Sdk=\"Microsoft.NET.Sdk\">{item}</Project>");
    }
}
