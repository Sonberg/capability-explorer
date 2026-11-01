using System.Text.Json;
using CapabilityExplorer.Extractors.CSharp;
using Xunit;

namespace CapabilityExplorer.Extractors.Tests;

public sealed class ServiceBusSecurityTests
{
    [Theory]
    [InlineData("Endpoint=sb://demo.servicebus.windows.net/;SharedAccessKeyName=test;SharedAccessKey=synthetic-sensitive-marker", "demo.servicebus.windows.net")]
    [InlineData("demo.servicebus.windows.net", "demo.servicebus.windows.net")]
    [InlineData("sb://user:synthetic-sensitive-marker@demo.servicebus.windows.net/", null)]
    [InlineData("sb://demo.servicebus.windows.net/?token=synthetic-sensitive-marker", null)]
    [InlineData("sb://demo.servicebus.windows.net/#synthetic-sensitive-marker", null)]
    [InlineData("not-a-namespace.servicebus./synthetic-sensitive-marker", null)]
    public void Channel_identity_never_copies_connection_credentials(string value, string? expected)
    {
        var root = Directory.CreateTempSubdirectory("ce-servicebus-security-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(root, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
            File.WriteAllText(Path.Combine(root, "App.cs"), $$"""
                public class ServiceBusClient {
                    public ServiceBusClient(string value) {}
                    public void CreateSender(string name) {}
                }
                public class App {
                    public void Send() { var client = new ServiceBusClient({{JsonSerializer.Serialize(value)}}); client.CreateSender("events"); }
                }
                """);
            var packs = Path.GetFullPath(Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", "packs"));
            var result = Assert.Single(new CSharpExtractor(new ExtractorOptions(root, "security", new string('a', 40), null, packs)).Extract()).Jsonl;
            Assert.DoesNotContain("synthetic-sensitive-marker", result);
            var records = result.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(s => JsonDocument.Parse(s)).ToArray();
            try
            {
                var node = Assert.Single(records, r => r.RootElement.TryGetProperty("nodeKind", out var k) && k.GetString() == "Channel").RootElement;
                Assert.Contains(expected ?? "@unbound:", node.GetProperty("symbolKey").GetString(), StringComparison.Ordinal);
            }
            finally { foreach (var record in records) record.Dispose(); }
        }
        finally { Directory.Delete(root, true); }
    }
}
