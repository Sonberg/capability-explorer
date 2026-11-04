using System.Globalization;
using System.Text;
using CapabilityExplorer.Contracts;
using CapabilityExplorer.Contracts.Collection;
using CapabilityExplorer.Store;
using Npgsql;

namespace CapabilityExplorer.Explorer;

public static class ModelApi
{
    public static void MapModelApi(this IEndpointRouteBuilder app,string mode)
    {
        foreach(var endpoint in new[]{"","/evidence","/source"})
        {
            var selectedEndpoint=endpoint;
            app.MapGet("/api/models"+endpoint,(Delegate)((HttpContext http)=>Handle(http,mode,selectedEndpoint)));
        }
    }

    private static async Task<IResult> Handle(HttpContext http,string mode,string endpoint)
    {
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        var ct=deadline.Token;
        try
        {
            var scope=await Resolve(http,mode,ct);
            if(scope.Error is {} problem) return problem;
            var context=scope.Context!;
            var cursorService=http.RequestServices.GetRequiredService<ExplorerQueryCursor>();
            var limitText=Text(http,"limit",3);
            var limit=limitText is null?20:int.TryParse(limitText,NumberStyles.None,CultureInfo.InvariantCulture,out var parsed)?parsed:0;
            if(limit is <1 or >50) return Problem(400,"invalid-query","model page limit must be 1 to 50");
            if(endpoint=="")
            {
                var cursor=Text(http,"cursor",16384);
                if(cursor is not null && (Text(http,"snapshot",36) is null || Text(http,"generation",20) is null))
                    return Problem(400,"invalid-cursor","continuation requires snapshot and generation");
                var filter="models.v1:"+limit.ToString(CultureInfo.InvariantCulture);
                var after=cursorService.Decode(context,filter,cursor);
                ModelSummary[] rows;
                if(mode=="files")
                    rows=scope.Files!.Products.Where(p=>InSelection(p,context) && string.CompareOrdinal(p.Id,after?[0]??"")>0)
                        .OrderBy(p=>p.Id,StringComparer.Ordinal).Take(limit+1).Select(ModelSummary.From).ToArray();
                else
                {
                    var store=new ModelProductStore(http.RequestServices.GetRequiredService<NpgsqlDataSource>());
                    rows=(await store.HeadsAsync(context.SnapshotId,context.Selection,after?[0],limit+1,ct))
                        .Select(p=>new ModelSummary(p.Id,p.Profile=="ce.models.v1"?"authored":"candidate",p.RepositoryId,
                            p.CommitSha,p.UnitPath,p.ModelKind,p.Coverage,p.DiagnosticCount,new(p.SourceStatus,p.SourceSha256))).ToArray();
                }
                var items=rows.Take(limit).ToArray();
                var next=rows.Length>limit?cursorService.Encode(context,filter,items[^1].Id):null;
                return new BoundedJsonResult(new ModelPage("ce.model-views.v1",items,[],
                    mode=="files"?"files":"database",context.SnapshotId.ToString(),context.Generation,context.Selection,next));
            }
            if(Text(http,"snapshot",36) is null || Text(http,"generation",20) is null)
                return Problem(400,"invalid-query","selected model requests require snapshot and generation");
            var id=Text(http,"id",70);
            if(id is null || !id.StartsWith("model:",StringComparison.Ordinal) || id.Length!=70 ||
                id[6..].Any(c=>!char.IsAsciiHexDigit(c) || char.IsAsciiLetterUpper(c)))
                return Problem(400,"invalid-id","invalid model identity");
            var product=mode=="files"?scope.Files!.Products.SingleOrDefault(p=>p.Id==id && InSelection(p,context)):
                await new ModelProductStore(http.RequestServices.GetRequiredService<NpgsqlDataSource>())
                    .ReadAsync(context.SnapshotId,context.Selection,id,ct);
            if(product is null) return Problem(404,"model-missing","model is not in the selected membership");
            if(ModelSource.Digest(product.EvidenceBytes)!=product.ArtifactSha256 ||
                product.Id!=ValidatedModelBundle.StableId(product.Profile,product.RepositoryId,product.CommitSha,product.UnitPath,product.ArtifactSha256) ||
                EvidenceProfileSchema.Validate(product.Profile,product.Evidence).Count!=0)
                return Problem(422,"invalid-product","stored model evidence is invalid");
            if(endpoint=="/evidence")
                return new BoundedJsonResult(new {schema="ce.model-evidence-view.v1",id, evidence=product.Evidence,
                    snapshot=context.SnapshotId.ToString(),context.Generation,context.Selection},maximumBytes:16_777_216);
            if(product.SourceStatus=="denied") return Problem(403,"source-denied","model-content permission is absent");
            if(product.SourceStatus!="available" || product.Source is not {} source)
                return Problem(404,"source-missing","original model source was not retained");
            var bytes=ModelSource.Decode(source);
            ModelSource.CheckCompanion(source,product.EvidenceBytes);
            return new BoundedJsonResult(new {schema="ce.model-source-view.v1",id,product.RepositoryId,product.CommitSha,
                product.UnitPath,sha256=source.GetProperty("sourceSha256").GetString(),format=source.GetProperty("sourceFormat").GetString(),
                xml=new UTF8Encoding(false,true).GetString(bytes),snapshot=context.SnapshotId.ToString(),
                context.Generation,context.Selection},maximumBytes:8_388_608);
        }
        catch(ArgumentException e) when(e is not DecoderFallbackException) { return Problem(400,"invalid-query","invalid query or continuation cursor"); }
        catch(Exception e) when(e is FormatException or System.Text.Json.JsonException or System.Xml.XmlException or DecoderFallbackException)
        { return Problem(422,"invalid-product","model product validation failed"); }
        catch(Exception e) when(e is IOException or UnauthorizedAccessException or NpgsqlException)
        { return Problem(503,"model-unavailable","model storage is unavailable"); }
        catch(InvalidOperationException) { return Problem(413,"model-limit","model scope or storage exceeds supported limits"); }
        catch(OperationCanceledException) when(!http.RequestAborted.IsCancellationRequested)
        { return Problem(504,"model-time-limit","model request exceeded its time budget"); }
    }

    private sealed record ScopeResult(ExplorerQueryContext? Context,ModelFileSnapshot? Files,IResult? Error);
    private static async Task<ScopeResult> Resolve(HttpContext http,string mode,CancellationToken ct)
    {
        var cap=Text(http,"cap",100)??"*";
        var specified=ParseSelection(Text(http,"view",16384));
        ExplorerQueryContext context;
        ModelFileSnapshot? files=null;
        if(mode=="files")
        {
            var snapshot=http.RequestServices.GetRequiredService<ExplorerState>().Current;
            if(cap!="*" && snapshot.Capabilities.Get(cap) is null)
                return new(null,null,Problem(400,"unknown-capability","unknown capability"));
            var allowed=snapshot.Capabilities.Scope(cap)?.Repositories;
            files=snapshot.Models;
            var choices=snapshot.Index.Revisions.Select(r=>(r.RepositoryId,r.CommitSha))
                .Concat(files.Products.Select(p=>(p.RepositoryId,p.CommitSha)))
                .Where(r=>allowed is null || allowed.Contains(r.RepositoryId)).ToArray();
            var preferred=snapshot.Capabilities.Scope(cap)?.Preferred;
            var selected=choices.GroupBy(r=>r.RepositoryId).ToDictionary(g=>g.Key,
                g=>preferred?.GetValueOrDefault(g.Key)??snapshot.Index.DefaultSelection.GetValueOrDefault(g.Key)??g.Select(r=>r.CommitSha).Order(StringComparer.Ordinal).Last(),
                StringComparer.Ordinal);
            foreach(var pair in specified)
            {
                if(!choices.Contains((pair.Key,pair.Value))) throw new ArgumentException("revision outside scope");
                selected[pair.Key]=pair.Value;
            }
            context=new(snapshot.ModelSnapshotId,cap,snapshot.Generation,selected);
        }
        else
        {
            using var definitions=http.RequestServices.GetRequiredService<DatabaseExplorerConfiguration>().OpenDefinitions();
            if(cap!="*" && definitions.Find(cap) is null) return new(null,null,Problem(400,"unknown-capability","unknown capability"));
            var resolved=await http.RequestServices.GetRequiredService<IExplorerQueries>().ResolveAsync(cap,specified,cancellationToken:ct);
            if(resolved is null) return new(null,null,Problem(503,"model-unavailable","no published analysis snapshot"));
            if(cap!="*" && definitions.Find(cap) is {} definition &&
                resolved.Selection.Keys.Any(repo=>!definition.Repositories.Any(r=>r.Id==repo)))
                return new(null,null,Problem(403,"scope-denied","snapshot outside current capability"));
            context=resolved;
        }
        var snapshotText=Text(http,"snapshot",36);var generationText=Text(http,"generation",20);
        if(snapshotText is not null && !Guid.TryParse(snapshotText,out _)) throw new ArgumentException("invalid snapshot");
        if(generationText is not null && !long.TryParse(generationText,NumberStyles.None,CultureInfo.InvariantCulture,out _))
            throw new ArgumentException("invalid generation");
        if((snapshotText is not null && snapshotText!=context.SnapshotId.ToString()) ||
            (generationText is not null && generationText!=context.Generation.ToString(CultureInfo.InvariantCulture)))
            return new(null,null,Problem(409,"stale-model-scope","model snapshot or generation changed"));
        return new(context,files,null);
    }

    private static bool InSelection(ModelProduct p,ExplorerQueryContext context) => context.Selection.TryGetValue(p.RepositoryId,out var sha)&&sha==p.CommitSha;
    private static Dictionary<string,string> ParseSelection(string? value)
    {
        var result=new Dictionary<string,string>(StringComparer.Ordinal);
        foreach(var part in (value??"").Split(',',StringSplitOptions.RemoveEmptyEntries))
        {
            var colon=part.IndexOf(':');
            if(colon<1 || result.Count>=100 || !result.TryAdd(part[..colon],part[(colon+1)..]) ||
                part[(colon+1)..].Length!=40 || part[(colon+1)..].Any(c=>!char.IsAsciiHexDigit(c))) throw new ArgumentException("invalid selection");
        }
        return result;
    }
    private static string? Text(HttpContext http,string name,int max)
    {
        var values=http.Request.Query[name];
        if(values.Count>1 || values.ToString().Length>max) throw new ArgumentException("invalid query");
        return values.Count==0?null:values.ToString();
    }
    private static IResult Problem(int status,string code,string detail) =>
        Results.Problem(statusCode:status,title:code,detail:detail,extensions:new Dictionary<string,object?>{{"code",code}});
}
