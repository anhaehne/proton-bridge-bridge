using System.Diagnostics;
using System.Text.Json;
using ProtonBridgeBridge;

namespace ProtonBridgeBridge.Tests;

public class CandidateCommandTests
{
    [Fact]
    public async Task CandidateCommandsValidateReadOnlyAndNeverPrintCredentialsAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var fixture = await RecoveryFixture.StartAsync(false, timeout.Token);
        fixture.Stock.Inbox[7] = fixture.Stock.Inbox[7] with
        {
            Raw = System.Text.Encoding.UTF8.GetBytes("Subject: ProtonBridgeBridge test rc1nonce\r\n").Concat(fixture.Stock.Inbox[7].Raw).ToArray()
        };
        var path = Path.Combine(Path.GetDirectoryName(fixture.Settings.Certificate)!, "candidate.json");
        var options = fixture.Settings with { PreflightUids = [7], AllowLabelWrites = false };
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(options), timeout.Token);

        var version = await RunAsync("--version", null, timeout.Token);
        Assert.Equal(0, version.ExitCode);
        Assert.StartsWith("0.1.0-rc.1", version.Output);
        var configuration = await RunAsync("--check-config", path, timeout.Token);
        Assert.Equal(0, configuration.ExitCode);
        Assert.Empty(fixture.Stock.Commands);
        var discovered = await RunAsync("--discover", path, timeout.Token);
        Assert.Equal(0, discovered.ExitCode);
        Assert.Equal([7u], JsonSerializer.Deserialize<DiscoveryReport>(discovered.Output)!.Uids);
        Assert.DoesNotContain("rc1nonce", discovered.Output);
        var preflight = await RunAsync("--preflight", path, timeout.Token);
        Assert.Equal(0, preflight.ExitCode);
        var report = JsonSerializer.Deserialize<PreflightReport>(preflight.Output)!;
        Assert.True(report.Success);
        Assert.False(report.LabelCopyTested);
        Assert.DoesNotContain("fixture", preflight.Output);
        Assert.DoesNotContain("secret", preflight.Output);
        Assert.DoesNotContain("%PDF", preflight.Output);
        Assert.Equal(0, fixture.Stock.Copies);
        Assert.False(Directory.Exists(options.StateDirectory));
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(string command, string? configPath, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        info.ArgumentList.Add(typeof(ProxyHost).Assembly.Location);
        info.ArgumentList.Add(command);
        if (configPath is not null)
        {
            info.ArgumentList.Add(configPath);
        }

        info.Environment["PROTON_BRIDGE_TEST_SUBJECT"] = "ProtonBridgeBridge test rc1nonce";
        info.Environment["PROTON_BRIDGE_USERNAME"] = "fixture";
        info.Environment["PROTON_BRIDGE_PASSWORD"] = "secret";
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
            await error;
            return (process.ExitCode, await output);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill();
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await process.WaitForExitAsync(cleanup.Token);
            }
        }
    }
}
