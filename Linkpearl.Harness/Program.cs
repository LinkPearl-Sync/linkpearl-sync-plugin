using Linkpearl.Harness;

// Banc de mesure du jalon 2. Ne construit rien de définitif : il répond à une
// seule question, celle de savoir si LiteNetLib peut porter 298 Mo dans un
// temps acceptable, et sous quelles conditions.

if (args.Length > 0 && args[0] == "vectors")
{
    Console.WriteLine(VectorGenerator.Build());
    return;
}

if (args.Length > 0 && args[0] == "rdv")
{
    var host = args.Length > 1 ? args[1] : "127.0.0.1";
    var rdvPort = args.Length > 2 && int.TryParse(args[2], out var p2) ? p2 : 47900;
    var role = args.Length > 3 ? args[3] : "a";

    Environment.ExitCode = await RendezvousRun.ExecuteAsync(host, rdvPort, role, CancellationToken.None) ? 0 : 1;
    return;
}

if (args.Length > 0 && args[0] == "pairing")
{
    using var identity = Linkpearl.Core.Crypto.CryptoPrimitives.GenerateIdentity();
    var key = Linkpearl.Core.Crypto.CryptoPrimitives.ExportPublicPoint(identity);
    var id = Linkpearl.Core.Identity.PeerId.Of(key);
    var code = Linkpearl.Core.Identity.PairingCode.Create(id, args.Length > 1 ? args[1] : "rdv.exemple.ch");
    var text = code.Encode();

    Console.WriteLine($"Code d'invitation ({text.Length} caractères) :");
    Console.WriteLine();
    Console.WriteLine($"  {text}");
    Console.WriteLine();
    Console.WriteLine($"Identifiant de pair : {code.Id}");
    Console.WriteLine($"Relecture : {(Linkpearl.Core.Identity.PairingCode.TryParse(text, out var back, out _) && back!.Id == id ? "l'empreinte est retrouvée" : "ÉCHEC")}");
    return;
}

if (args.Length > 0 && args[0] == "presence")
{
    var host = args.Length > 1 ? args[1] : "127.0.0.1";
    var port = args.Length > 2 && int.TryParse(args[2], out var p3) ? p3 : 47900;

    Environment.ExitCode = await PresenceRun.ExecuteAsync(host, port, CancellationToken.None) ? 0 : 1;
    return;
}

if (args.Length > 0 && args[0] == "hostile")
{
    Environment.ExitCode = await HostileRun.ExecuteAsync(CancellationToken.None) ? 0 : 1;
    return;
}

if (args.Length > 0 && args[0] == "federation")
{
    // Les deux services tournent à côté : ils vivent dans un autre dépôt, et le
    // harnais ne peut pas les instancier. Le troisième n'est jamais lancé, c'est
    // le cas « aucun lieu joignable ».
    Linkpearl.Core.Transport.Rendezvous.RendezvousAddress Address(string name, string fallback)
    {
        var text = ArgString(name, fallback);

        if (Linkpearl.Core.Transport.Rendezvous.RendezvousAddress.TryParse(text, out var parsed, out var why))
            return parsed;

        Console.WriteLine($"{name} illisible : {why}");
        Environment.Exit(2);
        return default;
    }

    Environment.ExitCode = await FederationRun.ExecuteAsync(
        new FederationSettings(
            ServiceA: Address("--rdv-a", "127.0.0.1:47901"),
            ServiceB: Address("--rdv-b", "127.0.0.1:47902"),
            Dead: Address("--rdv-mort", "127.0.0.1:47999"),
            TimeoutSeconds: Arg("--timeout", 60)),
        CancellationToken.None) ? 0 : 1;
    return;
}

if (args.Length > 0 && args[0] == "cercle-ouvert")
{
    // Six services tournent à côté : trois ouverts, un d'ancrage, une
    // autorité dont la clé publique se relève dans son journal au démarrage,
    // et un dernier lancé avec le relais coupé (relayEnabled à false dans
    // son fichier de réglages).
    Linkpearl.Core.Transport.Rendezvous.RendezvousAddress Address(string text)
    {
        if (Linkpearl.Core.Transport.Rendezvous.RendezvousAddress.TryParse(text, out var parsed, out var why))
            return parsed;

        Console.WriteLine($"{text} illisible : {why}");
        Environment.Exit(2);
        return default;
    }

    var authorityKey = ArgString("--cle-autorite", "");

    if (authorityKey.Length is 0)
    {
        Console.WriteLine("--cle-autorite manque : la clé publique que l'autorité affiche au démarrage.");
        Environment.ExitCode = 2;
        return;
    }

    Environment.ExitCode = await OpenCircleRun.ExecuteAsync(
        new OpenCircleSettings(
            Open: [.. ArgString("--rdv-ouverts", "127.0.0.1:47911,127.0.0.1:47912,127.0.0.1:47913").Split(',').Select(Address)],
            Anchor: Address(ArgString("--rdv-ancre", "127.0.0.1:47914")),
            Authority: Address(ArgString("--rdv-autorite", "127.0.0.1:47915")),
            AuthorityKey: Convert.FromHexString(authorityKey),
            TimeoutSeconds: Arg("--timeout", 60),
            NoRelay: Address(ArgString("--rdv-sans-relais", "127.0.0.1:47916"))),
        CancellationToken.None) ? 0 : 1;
    return;
}

if (args.Length > 0 && args[0] == "fakepeer")
{
    var cache = ArgString("--cache", "/mnt/c/Users/yann/AppData/Local/Linkpearl/cache");
    var manifest = ArgString("--manifest", Path.Combine(Path.GetDirectoryName(cache)!, "capture.json.br"));

    Environment.ExitCode = await FakePeerRun.ExecuteAsync(
        new FakePeerSettings(
            SourceCacheRoot: cache,
            ManifestPath: manifest,
            SynthesizeFromCache: Array.IndexOf(args, "--from-cache") >= 0,
            DataChannels: Arg("--channels", 24),
            BlockSize: Arg("--block", 16) * 1024,
            RateLimited: Array.IndexOf(args, "--no-limit") < 0,
            TimeoutSeconds: Arg("--timeout", 180),
            LatencyMs: Arg("--latency", 0),
            LossPercent: Arg("--loss", 0)),
        CancellationToken.None) ? 0 : 1;
    return;
}

if (args.Length > 0 && args[0] == "endtoend")
{
    var cache = ArgString("--cache", "/mnt/c/Users/yann/AppData/Local/Linkpearl/cache");
    var manifest = ArgString("--manifest", Path.Combine(Path.GetDirectoryName(cache)!, "capture.json.br"));

    await EndToEndRun.ExecuteAsync(
        new EndToEndSettings(
            SourceCacheRoot: cache,
            ManifestPath: manifest,
            DataChannels: Arg("--channels", 24),
            LatencyMs: Arg("--latency", 0),
            LossPercent: Arg("--loss", 0),
            RateLimited: Array.IndexOf(args, "--no-limit") < 0,
            BlockSize: Arg("--block", 16) * 1024,
            SynthesizeFromCache: Array.IndexOf(args, "--from-cache") >= 0),
        CancellationToken.None);
    return;
}

string ArgString(string name, string fallback)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : fallback;
}

var totalBytes  = Arg("--size", 298) * 1024L * 1024L;
var latencies   = ArgList("--latency", [20, 60, 150]);
var channelSets = ArgList("--channels", [1, 8, 24]);
var losses      = ArgList("--loss", [0, 1]);
var pollMs      = Arg("--poll", 15);
var blockSize   = Arg("--block", 16) * 1024;

Console.WriteLine($"Corpus {totalBytes / 1024 / 1024} Mo, blocs de {blockSize / 1024} Kio, "
                + $"PollEvents toutes les {pollMs} ms.");
Console.WriteLine();
Console.WriteLine($"{"canaux",7} {"latence",8} {"perte",6} {"durée",9} {"débit",11} {"ping",6} {"perte vue",10} {"mémoire",9}");
Console.WriteLine(new string('-', 76));

foreach (var loss in losses)
foreach (var latency in latencies)
foreach (var channels in channelSets)
{
    var settings = new RunSettings(totalBytes, channels + 1, blockSize, latency, latency / 10, loss, pollMs, Seed: 1);
    var result = ThroughputRun.Execute(settings);

    Console.WriteLine($"{channels,7} {latency + " ms",8} {loss + " %",6} "
                    + $"{result.Seconds,7:F1} s {result.MegabytesPerSecond,8:F2} Mo/s "
                    + $"{result.ReportedPing,4} ms {result.ReportedLossPercent,8:F1} % "
                    + $"{result.PeakWorkingSetDeltaBytes / 1024 / 1024,6} Mo");
}

int Arg(string name, int fallback)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length && int.TryParse(args[index + 1], out var value) ? value : fallback;
}

int[] ArgList(string name, int[] fallback)
{
    var index = Array.IndexOf(args, name);
    if (index < 0 || index + 1 >= args.Length)
        return fallback;

    return args[index + 1].Split(',').Select(int.Parse).ToArray();
}
