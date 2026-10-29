using System.Text.Json;
using ProtonBridgeProxy;

if (args.Length != 2 || args[0] is not ("--experimental" or "--healthcheck"))
{
    Console.Error.WriteLine("Usage: ProtonBridgeProxy --experimental config.json | --healthcheck config.json (disposable accounts only; stock Bridge validation pending)");
    return 2;
}

try
{
    var options = JsonSerializer.Deserialize<Options>(File.ReadAllText(args[1])) ?? throw new InvalidDataException();
    if (args[0] == "--healthcheck")
    {
        return await HealthProbe.CheckAsync(options, CancellationToken.None) ? 0 : 1;
    }

    await using var app = ProxyHost.Create(options);
    await app.RunAsync();
    return 0;
}
catch (Exception)
{
    Console.Error.WriteLine("Proxy startup failed. Check configuration, certificate paths and exclusive state-directory access.");
    return 1;
}
