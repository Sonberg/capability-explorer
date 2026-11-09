using CapabilityExplorer.Extractors.CSharp;
using Xunit;

namespace CapabilityExplorer.Extractors.Tests;

public sealed class ProjectSelectionTests : IDisposable
{
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "ce-project-selection-" + Guid.NewGuid().ToString("N"));

    public ProjectSelectionTests() => Directory.CreateDirectory(_root);
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

    [Fact]
    public void Selected_projects_preserve_test_policy_and_the_explicit_false_override()
    {
        Project("src/App/App.csproj");
        Project("tests/App.Tests/App.Tests.csproj", "<PackageReference Include=\"Microsoft.NET.Test.Sdk\" Version=\"18.0.1\" />");
        Project("tests/OptIn/Production.Tests.csproj", "<IsTestProject>false</IsTestProject>");
        var paths = new[] { "src/App/App.csproj", "tests/App.Tests/App.Tests.csproj", "tests/OptIn/Production.Tests.csproj" };
        var selection = ProjectModel.ComputeSelectionContract(_root, paths);

        var production = ProjectModel.ValidateSelection(_root, selection, includeTests: false);
        var all = ProjectModel.ValidateSelection(_root, selection, includeTests: true);

        Assert.Equal(3, production.CandidateCount);
        Assert.Equal(1, production.TestExcludedCount);
        Assert.Equal(2, production.SelectedCount);
        Assert.Equal(0, all.TestExcludedCount);
        Assert.Equal(3, all.SelectedCount);
    }

    [Fact]
    public void Selection_rejects_source_mutation_and_excluded_project_references_without_reading_excluded_xml()
    {
        Write("src/App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup><ProjectReference Include="../Excluded/Excluded.csproj" /></ItemGroup>
            </Project>
            """);
        Write("src/App/App.cs", "public sealed class App {}");
        Write("src/Excluded/Excluded.csproj", "not valid XML");
        var selection = ProjectModel.ComputeSelectionContract(_root, ["src/App/App.csproj"]);
        var source = Path.Combine(_root, "src/App/App.cs");
        File.AppendAllText(source, " // mutation");

        var changed = Assert.Throws<InvalidDataException>(() =>
            ProjectModel.ValidateSelection(_root, selection, includeTests: true));
        Assert.Contains("source identity", changed.Message, StringComparison.OrdinalIgnoreCase);

        selection = ProjectModel.ComputeSelectionContract(_root, ["src/App/App.csproj"]);
        var excludedReference = Assert.Throws<InvalidDataException>(() =>
            ProjectModel.ValidateSelection(_root, selection, includeTests: true));
        Assert.Contains("selected-project-reference-excluded", excludedReference.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Selection_rejects_unsafe_and_case_ambiguous_project_paths()
    {
        Project("src/App/App.csproj");
        var unsafePath = new ProjectSelectionContract(["../src/App/App.csproj"], new string('a', 64), new string('b', 64));
        Assert.Throws<InvalidDataException>(() => ProjectModel.ValidateSelection(_root, unsafePath, includeTests: true));

        var single = ProjectModel.ComputeSelectionContract(_root, ["src/App/App.csproj"]);
        var ambiguous = single with { ProjectPaths = ["src/App/App.csproj", "src/app/app.csproj"] };
        Assert.Throws<InvalidDataException>(() => ProjectModel.ValidateSelection(_root, ambiguous, includeTests: true));
    }

    private void Project(string relativePath, string content = "")
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var item = content.StartsWith("<PackageReference", StringComparison.Ordinal) ? $"<ItemGroup>{content}</ItemGroup>" : $"<PropertyGroup>{content}</PropertyGroup>";
        File.WriteAllText(path, $"<Project Sdk=\"Microsoft.NET.Sdk\">{item}</Project>");
    }

    private void Write(string relativePath, string content)
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
