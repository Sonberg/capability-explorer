using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CapabilityExplorer.Capabilities;

namespace CapabilityExplorer.Explorer;

public sealed record HostGitHubAccount(string Login, bool Active);

internal static class HostGitHubAccounts
{
    public static async Task<IReadOnlyList<HostGitHubAccount>> ListAsync(CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("gh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in new[] { "auth", "status", "--hostname", "github.com", "--no-refresh", "--json", "hosts" })
            start.ArgumentList.Add(argument);

        using var process = Process.Start(start) ?? throw new Win32Exception("GitHub CLI could not start.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        async Task<string> ReadBounded(StreamReader reader)
        {
            var result = new StringBuilder();
            var buffer = new char[2048];
            while (await reader.ReadAsync(buffer, deadline.Token) is var length && length > 0)
            {
                if (result.Length + length > 65536) throw new InvalidDataException("GitHub CLI account list exceeds bounds.");
                result.Append(buffer, 0, length);
            }
            return result.ToString();
        }

        try
        {
            var output = ReadBounded(process.StandardOutput);
            var error = ReadBounded(process.StandardError);
            await process.WaitForExitAsync(deadline.Token);
            var json = await output;
            _ = await error;
            if (process.ExitCode != 0) throw new InvalidOperationException("GitHub CLI could not read account status.");
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("hosts", out var hosts) ||
                !hosts.TryGetProperty("github.com", out var accounts) || accounts.ValueKind != JsonValueKind.Array)
                return [];
            var result = new List<HostGitHubAccount>();
            foreach (var account in accounts.EnumerateArray())
            {
                if (account.GetProperty("state").GetString() != "success") continue;
                var login = account.GetProperty("login").GetString();
                if (login is null || !GitHubAuth.IsValidMode("account:" + login)) continue;
                result.Add(new HostGitHubAccount(login, account.GetProperty("active").GetBoolean()));
            }
            return result.OrderByDescending(account => account.Active).ThenBy(account => account.Login, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw;
        }
    }
}
