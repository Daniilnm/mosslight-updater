using System.IO.Compression;
using System.Net;
using Mosslight.Update;

if (args.Length != 5 || args[0] is not ("--candidate" or "--online"))
    throw new ArgumentException("--candidate/--online trust.json candidate-pack.json local-release-directory test-parent");
var trust = new SignatureTrust(Wire.Decode<TrustSettings>(await File.ReadAllBytesAsync(args[1])));
var handler = new CandidateOrNetwork(args[0] == "--candidate" ? Path.GetFullPath(args[3]) : null);
using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(30) }; http.DefaultRequestHeaders.UserAgent.ParseAdd("Mosslight-InstallVerification/1.1.0");
var client = new ReleaseClient(http, trust);
byte[] expected = await File.ReadAllBytesAsync(args[2]); var pack = trust.Verify<PackRelease>(expected); client.Validate(pack);
if (args[0] == "--online")
{
    var channel = await client.CheckAsync(default);
    if (channel.Pack.Version != 24 || channel.App.Version != "1.1.1") throw new Exception("Unexpected published versions");
    byte[] actual = await client.ReadEnvelopeAsync(channel.Channel.PackUrl, default);
    if (!actual.AsSpan().SequenceEqual(expected)) throw new Exception("Published catalog differs from tested candidate");
    var oldChannel = await client.ReadSignedAsync<UpdateChannel>(trust.Settings.ChannelUrl, default);
    var compatibility = await client.ReadSignedAsync<PackRelease>(oldChannel.PackUrl, default);
    if (oldChannel.PackVersion != 22 || oldChannel.AppVersion != "1.1.1" || compatibility.Files.Any(f => f.Url is not null && new Uri(f.Url).Host != "github.com"))
        throw new Exception("Legacy app cannot discover new app safely");
    string exe = await client.GetFileAsync(new("application", channel.App.Sha256, channel.App.Size, FilePolicy.Managed, channel.App.Url, channel.App.Size, false), Path.Combine(args[4], "exe-cache"), null, default);
    if (await Hashes.FileAsync(exe) != channel.App.Sha256) throw new Exception("Published EXE mismatch");
}
string home = Path.Combine(Paths.Root(args[4]), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(home);
string root = Path.Combine(home, "versions", "4"); Directory.CreateDirectory(Path.Combine(root, "mods"));
await File.WriteAllTextAsync(Path.Combine(root, "options.txt"), "personal options");
Directory.CreateDirectory(Path.Combine(root, "saves", "personal-world")); await File.WriteAllTextAsync(Path.Combine(root, "saves", "personal-world", "level.dat"), "personal world");
await File.WriteAllTextAsync(Path.Combine(root, "4.json"), "{\"id\":\"4\"}");
var engine = new UpdateEngine(client, new ClosedFixture()); var plan = await engine.AnalyzeAsync(root, pack, null, default);
if (!plan.FirstInstall || plan.Changes.Length != pack.Files.Length || pack.Files.Any(f => f.Url is null)) throw new Exception("Incomplete first-install plan");
Console.WriteLine($"Install {pack.Files.Length} files, {pack.Files.Count(f => f.Path.StartsWith("mods/"))} mods, {plan.DownloadBytes} download bytes.");
await engine.ApplyAsync(plan, false, new InstallProgress(), default);
foreach (var f in pack.Files)
{
    string path = Paths.Resolve(root, f.Path);
    if (f.Policy == FilePolicy.Managed && await Hashes.FileAsync(path) != f.Sha256) throw new Exception("Installed hash mismatch: " + f.Path);
}
if ((await engine.AnalyzeAsync(root, pack, null, default)).Changes.Length != 0) throw new Exception("Not idempotent");
if (await File.ReadAllTextAsync(Path.Combine(root, "options.txt")) != "personal options" || await File.ReadAllTextAsync(Path.Combine(root, "saves", "personal-world", "level.dat")) != "personal world") throw new Exception("Personal data changed");
Console.WriteLine("PASS complete first installation, exact hashes, personal files, no-op recheck.");
await engine.RollbackAsync(root, null, default);
if (Directory.GetFiles(Path.Combine(root, "mods")).Length != 0 || File.Exists(Paths.Resolve(root, ".mosslight/state.json"))) throw new Exception("Incomplete rollback");
if (await File.ReadAllTextAsync(Path.Combine(root, "options.txt")) != "personal options") throw new Exception("Rollback changed personal options");
Console.WriteLine("PASS complete first-install rollback to empty mods.");
// Apply from the verified cache again; this also proves a fresh profile can be reinstalled after rollback.
int before = handler.RequestCount; await engine.ApplyAsync(await engine.AnalyzeAsync(root, pack, null, default), false, null, default);
if (handler.RequestCount != before) throw new Exception("Reinstallation unexpectedly downloaded files");
await File.WriteAllBytesAsync(Path.Combine(home, "install-results.json"), Wire.Encode(new { passed = true, packVersion = pack.Version, files = pack.Files.Length, mods = pack.Files.Count(f => f.Path.StartsWith("mods/")), downloadBytes = plan.DownloadBytes, requests = handler.RequestCount, root, gameProcessActions = "NONE" }));
Console.WriteLine("PASS reinstall from cache with zero downloads. " + home);

sealed class ClosedFixture : IInstanceGuard { public Task EnsureClosedAsync(string root, CancellationToken token) => Task.CompletedTask; }
sealed class InstallProgress : IProgress<ProgressInfo>
{
    private long last;
    public void Report(ProgressInfo info)
    {
        long now = Environment.TickCount64, previous = Interlocked.Read(ref last);
        if (now - previous < 5000 || Interlocked.CompareExchange(ref last, now, previous) != previous) return;
        Console.WriteLine($"{info.Message}: {info.Percent:0}%");
    }
}
sealed class CandidateOrNetwork(string? localRelease) : DelegatingHandler(new HttpClientHandler { AllowAutoRedirect = false })
{
    public int RequestCount;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        Interlocked.Increment(ref RequestCount);
        if (localRelease is not null && request.RequestUri is { Host: "github.com" } uri)
        {
            string[] parts = uri.AbsolutePath.Split('/'); string file = Path.Combine(localRelease, parts[^2], parts[^1]);
            if (File.Exists(file)) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(File.OpenRead(file)), RequestMessage = request });
        }
        return base.SendAsync(request, token);
    }
}
