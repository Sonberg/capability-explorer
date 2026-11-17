using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using CapabilityExplorer.Explorer;
using Xunit;

namespace CapabilityExplorer.Tests;

public sealed partial class ProcessLandscapeApiTests
{
    private delegate void DocumentByteLimit(ReadOnlySpan<byte> raw, long maximumBytes);
    private static readonly JsonSerializerOptions CompactDocumentJson = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
    private static readonly JsonSerializerOptions IndentedDocumentJson = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    [Theory]
    [InlineData("a b")]
    [InlineData("trailing\\")]
    [InlineData("escaped\" quote \\ and spaces")]
    [InlineData("é 中")]
    public void DocumentByteLimitIgnoresOnlyUnquotedJsonWhitespace(string value)
    {
        var input = new[] { new { text = value } };
        var compact = JsonSerializer.SerializeToUtf8Bytes(input, CompactDocumentJson);
        var indented = JsonSerializer.SerializeToUtf8Bytes(input, IndentedDocumentJson);
        var validate = typeof(ProcessLandscapeLoader)
            .GetMethod("ValidateCompactDocumentBytes", BindingFlags.NonPublic | BindingFlags.Static)!
            .CreateDelegate<DocumentByteLimit>();

        validate(indented, compact.Length);
        Assert.Throws<ProcessLandscapeRejectedException>(() => validate(indented, compact.Length - 1));
    }

    [Theory]
    [InlineData("cycle", "cyclic-document-parent")]
    [InlineData("self-cycle", "cyclic-document-parent")]
    [InlineData("dangling-parent", "dangling-document-parent")]
    [InlineData("projection-core", "conflicting-document-projection")]
    [InlineData("projection-reparent", "conflicting-document-projection")]
    [InlineData("projection-omission", "conflicting-document-projection")]
    [InlineData("missing-canonical", "missing-canonical-document-projection")]
    [InlineData("duplicate-id", "duplicate-document-authored-id")]
    [InlineData("duplicate-identity", "duplicate-element-identity")]
    [InlineData("semantic-copy", "ambiguous-document-element")]
    [InlineData("structural-content", "structural-document-content")]
    public async Task CanonicalDocumentsRejectCyclesAndConflictingProcessProjections(string failure, string reason)
    {
        await using var fixture = await Host.CreateAsync();
        var snapshot = DocumentSnapshot();
        var document = snapshot.Documents![0];
        switch (failure)
        {
            case "cycle":
            case "self-cycle":
            case "dangling-parent":
                snapshot = ChangeDocumentElement(snapshot, "Definitions", element => element with
                {
                    Parent = DocId(failure switch
                    {
                        "cycle" => "Collaboration", "self-cycle" => "Definitions", _ => "Missing",
                    }),
                });
                break;
            case "projection-core":
            case "projection-reparent":
            case "projection-omission":
                var process = snapshot.Processes[0];
                snapshot = snapshot with { Processes = [process with
                {
                    Elements = process.Elements.Select(element => element.Id != "Shape" ? element : failure switch
                    {
                        "projection-core" => element with { Attributes = new Dictionary<string, string> { ["bpmnElement"] = "Task_B" } },
                        "projection-reparent" => element with { Parent = DocId("Diagram") },
                        _ => element with { Parent = null },
                    }).ToArray(),
                }, snapshot.Processes[1]] };
                break;
            case "missing-canonical":
                snapshot = snapshot with { Documents = [document with
                {
                    Elements = document.Elements.Where(element => element.Id != "Bounds").ToArray(),
                }] };
                break;
            case "duplicate-id":
                snapshot = snapshot with { Documents = [document with { Elements = [.. document.Elements,
                    DocumentElement("Task_A", "extension") with { Identity = DocId("DifferentIdentity") }] }] };
                break;
            case "duplicate-identity":
                snapshot = snapshot with { Documents = [document with { Elements = [.. document.Elements, document.Elements[0]] }] };
                break;
            case "semantic-copy":
                snapshot = snapshot with { Documents = [document with { Elements = [.. document.Elements, snapshot.Processes[0].Elements[1]] }] };
                break;
            case "structural-content":
                snapshot = ChangeDocumentElement(snapshot, "Definitions", element => element with { Content = new("<bpmn:definitions/>") });
                break;
        }
        await fixture.WriteAsync(snapshot);
        using var response = await fixture.Client.GetAsync("/api/process-landscape/v1/snapshot");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(reason, (await response.Content.ReadFromJsonAsync<ProcessLandscapeResponse>(Json))!.Reason);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DocumentModelContentRequiresExistingServerOptInAndVerifiedSourceBytes(bool allow)
    {
        await using var fixture = await Host.CreateAsync(allowModelContent: allow);
        var bytes = Encoding.UTF8.GetBytes("<bpmn/>");
        var hash = Snapshots.Sha256("<bpmn/>");
        var snapshot = ChangeDocumentElement(DocumentSnapshot(), "Definitions", element => element with { Content = new("Synthetic content") });
        snapshot = snapshot with
        {
            ContentProfile = "model-content",
            Processes = snapshot.Processes.Select(process => process with { ByteLength = bytes.Length, Sha256 = hash }).ToArray(),
            Sources = [new(DocumentPath, bytes.Length, hash, Convert.ToBase64String(bytes))],
        };
        await fixture.WriteAsync(snapshot);
        using var response = await fixture.Client.GetAsync("/api/process-landscape/v1/snapshot");
        Assert.Equal(allow ? HttpStatusCode.OK : HttpStatusCode.Forbidden, response.StatusCode);
        if (!allow) Assert.Equal("model-content-disabled",
            (await response.Content.ReadFromJsonAsync<ProcessLandscapeResponse>(Json))!.Reason);
    }

    [Theory]
    [InlineData("documents", "document-count-limit")]
    [InlineData("elements", "document-graph-count-limit")]
    [InlineData("relationships", "document-graph-count-limit")]
    [InlineData("attributes", "document-attribute-limit")]
    [InlineData("attribute-bytes", "document-text-limit")]
    public void DocumentGraphBudgetsFailBeforeUnboundedExpansion(string collection, string reason)
    {
        var snapshot = DocumentSnapshot();
        var document = snapshot.Documents![0];
        snapshot = collection switch
        {
            "documents" => snapshot with
            {
                Documents = Enumerable.Repeat(document, ProcessLandscapeDocumentLimits.MaxDocuments + 1).ToArray(),
            },
            "elements" => snapshot with { Documents = [document with
            {
                Elements = Enumerable.Repeat(document.Elements[0], ProcessLandscapeDocumentLimits.MaxElements + 1).ToArray(),
            }] },
            "relationships" => snapshot with { Documents = [document with
            {
                Relationships = Enumerable.Repeat(document.Relationships[0], ProcessLandscapeDocumentLimits.MaxRelationships + 1).ToArray(),
            }] },
            "attributes" => ChangeDocumentElement(snapshot, "Definitions", element => element with
            {
                Attributes = Enumerable.Range(0, ProcessLandscapeDocumentLimits.MaxAttributes + 1)
                    .ToDictionary(index => "attribute-" + index, _ => "value"),
            }),
            _ => ChangeDocumentElement(snapshot, "Definitions", element => element with
            {
                Attributes = new Dictionary<string, string>
                {
                    ["padding"] = new('\u0100', ProcessLandscapeDocumentLimits.MaxAttributeBytes / 2 + 1),
                },
            }),
        };
        var rejection = Assert.Throws<ProcessLandscapeRejectedException>(() => ProcessLandscapeValidation.Validate(snapshot, false));
        Assert.Equal("limit", rejection.Status);
        Assert.Equal(reason, rejection.Reason);
    }

    [Theory]
    [InlineData(128, true)]
    [InlineData(129, false)]
    public void DocumentParentDepthIsBoundedEvenWhenNodesAreFlatJsonRecords(int depth, bool accepted)
    {
        var snapshot = DocumentSnapshot();
        var chain = Enumerable.Range(0, depth).Select(index =>
            DocumentElement("chain-" + index, "extension", index == 0 ? null : "chain-" + (index - 1))).ToArray();
        snapshot = snapshot with { Documents = [snapshot.Documents![0] with
        {
            Elements = [.. snapshot.Documents[0].Elements, .. chain],
        }] };
        if (accepted) ProcessLandscapeValidation.Validate(snapshot, false);
        else
        {
            var rejection = Assert.Throws<ProcessLandscapeRejectedException>(() => ProcessLandscapeValidation.Validate(snapshot, false));
            Assert.Equal("document-depth-limit", rejection.Reason);
        }
    }
}
