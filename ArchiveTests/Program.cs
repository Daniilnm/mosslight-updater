using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using Mosslight.Update;

if (args.Length < 5) throw new ArgumentException("pack.json public.pem old.zip local-assets-directory test-directory [additional-old.zip ...]");
var trust = new SignatureTrust(new(Environment.GetEnvironmentVariable("MOSSLIGHT_TEST_REPOSITORY") ?? "local-preview/mosslight-updater", await File.ReadAllTextAsync(args[1])));
var pack = trust.Verify<PackRelease>(await File.ReadAllBytesAsync(args[0]));
var handler = new LocalAssets(args[3]); using var http = new HttpClient(handler); var engine = new UpdateEngine(new(http, trust), new Closed());
string home = Paths.Root(args[4]); Directory.CreateDirectory(home); var results = new List<object>();
foreach (string archive in new[] {args[2]}.Concat(args.Skip(5)))
{
    string root = Path.Combine(home, Path.GetFileNameWithoutExtension(archive)); if (Directory.Exists(root)) throw new IOException("Test directory already exists");
    Console.WriteLine("Extract fixture: " + Path.GetFileName(archive)); ZipFile.ExtractToDirectory(archive, root);
    await File.WriteAllTextAsync(Path.Combine(root, "options.txt"), "personal options"); string jade = Paths.Resolve(root, "config/jade/jade.json");
    await File.WriteAllTextAsync(jade, "personal Jade"); // Seed policy must not parse, reset or overwrite this.
    var plan = await engine.AnalyzeAsync(root, pack, null, default); long before = handler.Bytes;
    await engine.ApplyAsync(plan, false, null, default);
    if ((await engine.AnalyzeAsync(root, pack, null, default)).Changes.Length != 0) throw new Exception("Not idempotent");
    if (await File.ReadAllTextAsync(jade) != "personal Jade" || await File.ReadAllTextAsync(Path.Combine(root, "options.txt")) != "personal options") throw new Exception("Personal setting changed");
    long downloaded = handler.Bytes - before; if (downloaded != plan.DownloadBytes) throw new Exception($"Wrong download accounting: actual={downloaded}, expected={plan.DownloadBytes}");
    await engine.RollbackAsync(root, null, default);
    using var zip = ZipFile.OpenRead(archive);
    foreach (var entry in zip.Entries.Where(e => !string.IsNullOrEmpty(e.Name) && e.FullName != "config/jade/jade.json"))
    {
        using var source = entry.Open(); string expected = Convert.ToHexStringLower(await SHA256.HashDataAsync(source));
        if (await Hashes.FileAsync(Paths.Resolve(root, entry.FullName)) != expected) throw new Exception("Rollback mismatch: " + entry.FullName);
    }
    results.Add(new {archive = Path.GetFileName(archive), target = pack.Version, changes = plan.Changes.Length, deleted = plan.Changes.Count(c => c.Delete), downloadedBytes = downloaded, metadataBytes = new FileInfo(args[0]).Length, passed = true});
    Console.WriteLine($"PASS {Path.GetFileName(archive)} → v{pack.Version}: {plan.Changes.Length} changes, {downloaded} bytes; full rollback verified.");
}
await File.WriteAllBytesAsync(Path.Combine(home, "archive-results.json"), Wire.Encode(results));
// This is read-only process discovery; it must allow the running dedicated server.
await new WindowsInstanceGuard().EnsureClosedAsync(home, default); Console.WriteLine("PASS read-only Windows process guard; no Java process was started/stopped.");

sealed class Closed : IInstanceGuard { public Task EnsureClosedAsync(string root, CancellationToken token) => Task.CompletedTask; }
sealed class LocalAssets(string root) : HttpMessageHandler
{
    public long Bytes;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        var uri = request.RequestUri!; string[] parts = uri.AbsolutePath.Split('/'); string path = Path.Combine(root, parts[^2], parts[^1]);
        byte[] data = File.ReadAllBytes(path); Bytes += data.LongLength;
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {Content = new ByteArrayContent(data)});
    }
}
