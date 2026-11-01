using System.Net.Http;
using System.Threading.Tasks;

public class HttpFixture
{
    public async Task Read(HttpClient client, string id)
    {
        await client.GetAsync($"https://graph.example/v1/{id}/events?token=redacted-fixture#fragment");
        await client.GetAsync("/relative?limit=10");
    }
}
