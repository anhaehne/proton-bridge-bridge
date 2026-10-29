using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using ProtonBridgeBridge;

if (args is ["--version"])
{
    Console.WriteLine(typeof(ProxyHost).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion);
    return 0;
}

if (args.Length != 2 || args[0] is not ("--experimental" or "--healthcheck" or "--check-config" or "--preflight" or "--discover"))
{
    Console.Error.WriteLine("Usage: ProtonBridgeBridge --experimental|--healthcheck|--check-config|--preflight|--discover config.json | --version");
    return 2;
}

try
{
    var options = Options.Load(args[1]);
    options.Validate();
    if (args[0] == "--healthcheck")
    {
        return await HealthProbe.CheckAsync(options, CancellationToken.None) ? 0 : 1;
    }

    if (args[0] == "--check-config")
    {
        using var certificate = X509Certificate2.CreateFromPemFile(options.Certificate, options.PrivateKey);
        if (options.BridgeTrust is { } path)
        {
            using var anchor = X509Certificate2.CreateFromPem(File.ReadAllText(path));
        }

        Console.WriteLine("Configuration and certificate files are valid. No mailbox connection was made.");
        return 0;
    }

    if (args[0] is "--preflight" or "--discover")
    {
        var username = Environment.GetEnvironmentVariable("PROTON_BRIDGE_USERNAME");
        var password = Environment.GetEnvironmentVariable("PROTON_BRIDGE_PASSWORD");
        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
        {
            Console.Error.WriteLine("Preflight requires PROTON_BRIDGE_USERNAME and PROTON_BRIDGE_PASSWORD in the process environment.");
            return 2;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(options.SessionTimeoutSeconds));
        if (args[0] == "--discover")
        {
            var subject = Environment.GetEnvironmentVariable("PROTON_BRIDGE_TEST_SUBJECT") ?? "";
            var discovery = await BridgePreflight.DiscoverAsync(options, username, password, subject, timeout.Token);
            Console.WriteLine(JsonSerializer.Serialize(discovery));
            return discovery.Success ? 0 : 1;
        }

        var report = await BridgePreflight.RunAsync(options, username, password, timeout.Token);
        Console.WriteLine(JsonSerializer.Serialize(report));
        return report.Success ? 0 : 1;
    }

    await using var app = ProxyHost.Create(options);
    await app.RunAsync();
    return 0;
}
catch (Exception)
{
    Console.Error.WriteLine("Operation failed. Check configuration, trust, certificate paths, limits and exclusive state-directory access.");
    return 1;
}
