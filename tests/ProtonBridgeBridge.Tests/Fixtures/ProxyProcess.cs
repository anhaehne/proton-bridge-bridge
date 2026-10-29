using System.Diagnostics;
using System.Net;
using System.Net.Http;
using ProtonBridgeBridge;

namespace ProtonBridgeBridge.Tests;

internal sealed class ProxyProcess : IAsyncDisposable
{
    private readonly Process _process;
    private readonly Task<string> _output;
    private readonly Task<string> _error;

    private ProxyProcess(Process process)
    {
        _process = process;
        _output = process.StandardOutput.ReadToEndAsync();
        _error = process.StandardError.ReadToEndAsync();
    }

    public int Id => _process.Id;

    public static async Task<ProxyProcess> StartAsync(string configPath, int healthPort, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        info.ArgumentList.Add(typeof(ProxyHost).Assembly.Location);
        info.ArgumentList.Add("--experimental");
        info.ArgumentList.Add(configPath);
        info.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        info.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        var proxy = new ProxyProcess(Process.Start(info) ?? throw new InvalidOperationException("Test proxy did not start."));

        try
        {
            using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false })
            {
                BaseAddress = new Uri($"http://127.0.0.1:{healthPort}"),
                Timeout = TimeSpan.FromSeconds(1)
            };
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(50));
            while (true)
            {
                if (proxy._process.HasExited)
                {
                    throw new InvalidOperationException($"Test proxy exited before startup with code {proxy._process.ExitCode}.");
                }

                try
                {
                    // Liveness avoids generating unrelated upstream requests at a COPY barrier.
                    using var response = await client.GetAsync("/health/live", cancellationToken);
                    if (response.StatusCode == HttpStatusCode.OK)
                    {
                        return proxy;
                    }
                }
                catch (HttpRequestException)
                {
                }
                catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                }

                await timer.WaitForNextTickAsync(cancellationToken);
            }
        }
        catch
        {
            await proxy.DisposeAsync();
            throw;
        }
    }

    public async Task CrashAsync(CancellationToken cancellationToken)
    {
        await EnsureExitedAsync(cancellationToken);
        Assert.NotEqual(0, _process.ExitCode);
    }

    private async Task EnsureExitedAsync(CancellationToken cancellationToken)
    {
        // Kill only the child created by this fixture, without a graceful host shutdown.
        if (!_process.HasExited)
        {
            _process.Kill();
        }

        await _process.WaitForExitAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await EnsureExitedAsync(timeout.Token);
            await Task.WhenAll(_output, _error);
        }
        finally
        {
            _process.Dispose();
        }
    }
}
