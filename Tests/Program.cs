using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Mosslight.Update;

string home = Path.Combine(Path.GetFullPath(args.FirstOrDefault() ?? "test-results"), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(home);
var results = new List<object>(); int passed = 0, failed = 0;
async Task Test(string name, Func<Task> body)
{
    try { await body(); passed++; results.Add(new {name, passed = true}); Console.WriteLine("PASS " + name); }
    catch (Exception e) { failed++; results.Add(new {name, passed = false, error = e.ToString()}); Console.WriteLine("FAIL " + name + ": " + e.Message); }
}
static void Assert(bool value, string message = "Assertion failed") { if (!value) throw new Exception(message); }
static async Task Reject(Func<Task> action)
{ try { await action(); } catch (Exception e) when (e is UpdateRejectedException or InvalidDataException or IOException or CryptographicException or FormatException) { return; } throw new Exception("Expected rejection"); }
using var key = RSA.Create(2048); var trust = new SignatureTrust(new("fixture/mosslight-updater", key.ExportSubjectPublicKeyInfoPem()));
var handler = new FakeHttp(); using var http = new HttpClient(handler); var client = new ReleaseClient(http, trust);
var guard = new Guard(); var engine = new UpdateEngine(client, guard);
string Asset(string name) => "https://github.com/fixture/mosslight-updater/releases/download/test/" + name;
PackFile FileData(string path, string text, FilePolicy policy = FilePolicy.Managed, string[]? ids = null)
{
    byte[] data = Encoding.UTF8.GetBytes(text); using var memory = new MemoryStream(); using (var gzip = new GZipStream(memory, CompressionLevel.SmallestSize, true)) gzip.Write(data);
    string url = Asset(Hashes.Bytes(data) + ".gz"); handler.Assets[url] = memory.ToArray(); return new(path, Hashes.Bytes(data), data.Length, policy, url, memory.Length, true, ids);
}
PackRelease Pack(params PackFile[] files) => new(1, "mosslight-friends", 20, "1.21.1", "NeoForge", "1.0.0", ["test"], files, ["mods/old.jar"]);
string Instance()
{ string root = Path.Combine(home, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Path.Combine(root, "mods")); System.IO.File.WriteAllText(Path.Combine(root, "mods/kubejs-fixture.jar"), "anchor"); System.IO.File.WriteAllText(Path.Combine(root, "mods/sophisticatedbackpacks-fixture.jar"), "anchor"); return root; }
async Task Put(string root, string path, string content) { string target = Paths.Resolve(root, path); Directory.CreateDirectory(Path.GetDirectoryName(target)!); await System.IO.File.WriteAllTextAsync(target, content); }
string Read(string root, string path) => System.IO.File.ReadAllText(Paths.Resolve(root, path));
await Test("RSA signature and tampering", async () =>
{
    var p = Pack(FileData("kubejs/data/test.json", "one")); byte[] signed = SignatureTrust.Sign(p, key); Assert(trust.Verify<PackRelease>(signed).Version == 20);
    var envelope = Wire.Decode<SignedEnvelope>(signed); await Reject(() => Task.FromResult(trust.Verify<PackRelease>(Wire.Encode(envelope with {Payload = Convert.ToBase64String(Wire.Encode(p with {Version = 999}))}))));
    using var other = RSA.Create(); await Reject(() => Task.FromResult(trust.Verify<PackRelease>(SignatureTrust.Sign(p, other))));
});
await Test("HTTPS origin, path traversal and personal files", async () =>
{
    foreach (string url in new[] {"http://github.com/fixture/mosslight-updater/releases/download/x/a", "https://evil.example/a", "https://github.com/fixture/other/releases/download/x/a", Asset("a") + "?token=x", "https://github.com/fixture/mosslight-updater/releases/download/x/../../../../other/a"})
        await Reject(() => { trust.ValidateAssetUrl(url); return Task.CompletedTask; });
    foreach (string path in new[] {"../world/level.dat", "mods/../a", "mods/CON.jar", "mods/x.jar:stream", "mods\\a.jar", "mods/x.jar.", "options.txt", "saves/x.dat", "config/unknown.json", "kubejs/config/client.properties"})
        await Reject(() => { Paths.RequireManaged(path); return Task.CompletedTask; });
    await Reject(() => { Paths.DeleteOwnedTree(home, home); return Task.CompletedTask; });
    var file = FileData("mods/x.jar", "x"); await Reject(() => { client.Validate(Pack(file, file)); return Task.CompletedTask; });
});
await Test("Range resume, cache verification, ignored Range", async () =>
{
    var f = FileData("mods/new.jar", new string('x', 3000)); string cache = Path.Combine(home, "download"); Directory.CreateDirectory(cache);
    await System.IO.File.WriteAllBytesAsync(Path.Combine(cache, f.Sha256 + ".part"), handler.Assets[f.Url!][..8]);
    string path = await client.GetFileAsync(f, cache, null, default); Assert(handler.LastRange == 8); Assert(await Hashes.FileAsync(path) == f.Sha256);
    int count = handler.RequestCount; await client.GetFileAsync(f, cache, null, default); Assert(handler.RequestCount == count);
    await System.IO.File.WriteAllTextAsync(path, "bad"); handler.IgnoreRange = true;
    await System.IO.File.WriteAllBytesAsync(Path.Combine(cache, f.Sha256 + ".part"), handler.Assets[f.Url!][..8]);
    await client.GetFileAsync(f, cache, null, default); handler.IgnoreRange = false; Assert(await Hashes.FileAsync(path) == f.Sha256);
});
await Test("Corrupt gzip can be retried, bad hashes and expansion limits", async () =>
{
    var f = FileData("mods/corrupt.jar", "payload"); string cache = Path.Combine(home, "corrupt"); byte[] correct = handler.Assets[f.Url!]; handler.Assets[f.Url!] = new byte[correct.Length];
    await Reject(() => client.GetFileAsync(f, cache, null, default)); Assert(!System.IO.File.Exists(Path.Combine(cache, f.Sha256 + ".part")));
    handler.Assets[f.Url!] = correct; Assert(await Hashes.FileAsync(await client.GetFileAsync(f, cache, null, default)) == f.Sha256);
    await Reject(() => client.GetFileAsync(f with {Sha256 = new string('a', 64)}, Path.Combine(home, "wrong-sha"), null, default));
    await Reject(() => client.GetFileAsync(f with {Size = 2}, Path.Combine(home, "bomb"), null, default));
});
await Test("Partial response with wrong range is rejected", async () =>
{
    var f = FileData("mods/range.jar", "range"); handler.WrongRange = true;
    await Reject(() => client.GetFileAsync(f, Path.Combine(home, "range-error"), null, default)); handler.WrongRange = false;
});
await Test("Signed complete channel and app consistency", async () =>
{
    var pack = Pack(FileData("mods/one.jar", "one")); var app = new AppRelease(1, "1.0.1", Asset("app.exe"), new string('a', 64), 100);
    var channel = new UpdateChannel(1, trust.Settings.Repository, 20, Asset("pack.json"), app.Version, Asset("app.json"));
    handler.Assets[trust.Settings.ChannelUrl] = SignatureTrust.Sign(channel, key); handler.Assets[channel.PackUrl] = SignatureTrust.Sign(pack, key); handler.Assets[channel.AppUrl] = SignatureTrust.Sign(app, key);
    Assert((await client.CheckAsync(default)).Pack.Version == 20);
    int count = handler.RequestCount; await client.CheckAsync(default); Assert(handler.RequestCount == count + 1, "Immutable manifests fetched twice");
    channel = channel with {AppUrl = Asset("changed-app.json")}; handler.Assets[trust.Settings.ChannelUrl] = SignatureTrust.Sign(channel, key);
    handler.Assets[channel.AppUrl] = SignatureTrust.Sign(app with {Version = "1.0.2"}, key); await Reject(() => client.CheckAsync(default));
});
await Test("Skip versions, delete old jar, preserve extras and personal settings, rollback", async () =>
{
    string root = Instance(); await Put(root, "mods/old.jar", "old"); await Put(root, "mods/personal.jar", "extra"); await Put(root, "options.txt", "personal"); await Put(root, "saves/world/level.dat", "world");
    await Put(root, "config/immediatelyfast.json", "{\"hud_batching\":true,\"personal\":73}"); await Put(root, "config/jade/jade.json", "personal Jade");
    var pack = Pack(FileData("mods/new.jar", "new"), FileData("config/immediatelyfast.json", "{}", FilePolicy.HudPatch), FileData("config/jade/jade.json", "default", FilePolicy.Seed), FileData("kubejs/server_scripts/bmc5.js", "new script"));
    var plan = await engine.AnalyzeAsync(root, pack, null, default); Assert(plan.Changes.Length == 4); await engine.ApplyAsync(plan, false, null, default);
    Assert(!System.IO.File.Exists(Paths.Resolve(root, "mods/old.jar"))); Assert(Read(root, "mods/new.jar") == "new"); Assert(Read(root, "mods/personal.jar") == "extra");
    Assert(Read(root, "options.txt") == "personal" && Read(root, "saves/world/level.dat") == "world" && Read(root, "config/jade/jade.json") == "personal Jade");
    Assert(Read(root, "config/immediatelyfast.json").Contains("73")); Assert((await engine.AnalyzeAsync(root, pack, null, default)).Changes.Length == 0);
    await Put(root, "config/immediatelyfast.json", "{\"hud_batching\":false,\"personal\":99}"); await engine.RollbackAsync(root, null, default);
    Assert(Read(root, "mods/old.jar") == "old"); Assert(!System.IO.File.Exists(Paths.Resolve(root, "mods/new.jar"))); Assert(Read(root, "config/immediatelyfast.json").Contains("99") && Read(root, "config/immediatelyfast.json").Contains("true"));
});
await Test("All three patches preserve personal keys and are idempotent", () =>
{
    foreach (var (policy, text, expected) in new[] {
        (FilePolicy.HudPatch, "{\"hud_batching\":true,\"mine\":7}", "7"),
        (FilePolicy.ModernFixPatch, "# personal\nmine=true\nmixin.perf.faster_item_rendering=true\n", "mine=true"),
        (FilePolicy.ColdSweatPatch, "[Preferences]\n\"Show Water Effect\" = 3\nmine = 7\n[Other]\nx=2\n", "mine = 7")})
    { byte[] old = Encoding.UTF8.GetBytes(text), updated = ConfigPatches.Apply(policy, old); Assert(Encoding.UTF8.GetString(updated).Contains(expected)); Assert(updated.SequenceEqual(ConfigPatches.Apply(policy, updated))); Assert(Encoding.UTF8.GetString(ConfigPatches.Restore(policy, updated, old)).Contains(expected)); }
    byte[] existing = Encoding.UTF8.GetBytes("[Preferences]\r\n  \"Show Water Effect\" = 0 # my comment\r\n");
    Assert(ConfigPatches.Apply(FilePolicy.ColdSweatPatch, existing).SequenceEqual(existing));
    return Task.CompletedTask;
});
await Test("Fault during transaction rolls everything back", async () =>
{
    string root = Instance(); await Put(root, "mods/old.jar", "old"); await Put(root, "kubejs/data/a.json", "before");
    var pack = Pack(FileData("kubejs/data/a.json", "after"), FileData("mods/new.jar", "new"));
    engine.AfterMutationForTest = _ => throw new IOException("Injected disk failure");
    await Reject(() => engine.ApplyAsync(engine.AnalyzeAsync(root, pack, null, default).GetAwaiter().GetResult(), false, null, default)); engine.AfterMutationForTest = null;
    Assert(Read(root, "kubejs/data/a.json") == "before" && Read(root, "mods/old.jar") == "old"); Assert(!System.IO.File.Exists(Paths.Resolve(root, "mods/new.jar")));
});
await Test("Recovery after interrupted mutation restores from journal", async () =>
{
    string root = Instance(); string transaction = Paths.Resolve(root, ".mosslight/transactions/interrupted"); Directory.CreateDirectory(Path.Combine(transaction, "backup/mods"));
    await Put(root, "mods/old.jar", "partial"); await System.IO.File.WriteAllTextAsync(Path.Combine(transaction, "backup/mods/old.jar"), "original");
    var journal = new UpdateJournal(1, "interrupted", "Applying", 20, null, [new("mods/old.jar", Hashes.Bytes(Encoding.UTF8.GetBytes("original")), Hashes.Bytes(Encoding.UTF8.GetBytes("partial")), FilePolicy.Managed, false)], DateTimeOffset.UtcNow);
    await System.IO.File.WriteAllBytesAsync(Path.Combine(transaction, "journal.json"), Wire.Encode(journal)); await engine.RecoverAsync(root, null, default); Assert(Read(root, "mods/old.jar") == "original");
    Assert(Wire.Decode<UpdateJournal>(await System.IO.File.ReadAllBytesAsync(Path.Combine(transaction, "journal.json"))).Phase == "RolledBack");
});
await Test("Bad backup prevents destructive recovery", async () =>
{
    string root = Instance(); string transaction = Paths.Resolve(root, ".mosslight/transactions/bad"); Directory.CreateDirectory(transaction); await Put(root, "mods/old.jar", "current");
    var journal = new UpdateJournal(1, "bad", "Applying", 20, null, [new("mods/old.jar", new string('a', 64), new string('b', 64), FilePolicy.Managed)], DateTimeOffset.UtcNow);
    await System.IO.File.WriteAllBytesAsync(Path.Combine(transaction, "journal.json"), Wire.Encode(journal)); await Reject(() => engine.RecoverAsync(root, null, default)); Assert(Read(root, "mods/old.jar") == "current");
});
await Test("Interrupted anchor-jar deletion does not prevent recovery", async () =>
{
    string root = Instance(); string anchor = "mods/kubejs-fixture.jar"; System.IO.File.Delete(Paths.Resolve(root, anchor));
    string transaction = Paths.Resolve(root, ".mosslight/transactions/anchor"); Directory.CreateDirectory(Path.Combine(transaction, "backup/mods"));
    await System.IO.File.WriteAllTextAsync(Path.Combine(transaction, "backup/mods/kubejs-fixture.jar"), "anchor");
    var journal = new UpdateJournal(1, "anchor", "Applying", 20, null, [new(anchor, Hashes.Bytes(Encoding.UTF8.GetBytes("anchor")), null, FilePolicy.Managed)], DateTimeOffset.UtcNow);
    await System.IO.File.WriteAllBytesAsync(Path.Combine(transaction, "journal.json"), Wire.Encode(journal)); await engine.RecoverAsync(root, null, default); Assert(Read(root, anchor) == "anchor");
});
await Test("Running client, stale plan, cancellation and server-root guards", async () =>
{
    string root = Instance(); var pack = Pack(FileData("mods/new.jar", "new")); guard.Blocked = true;
    await Reject(() => engine.AnalyzeAsync(root, pack, null, default)); guard.Blocked = false;
    var plan = await engine.AnalyzeAsync(root, pack, null, default); await Put(root, "mods/new.jar", "outside change"); await Reject(() => engine.ApplyAsync(plan, false, null, default)); Assert(Read(root, "mods/new.jar") == "outside change");
    using var cts = new CancellationTokenSource(); cts.Cancel(); bool canceled = false; try { await engine.AnalyzeAsync(root, pack, null, cts.Token); } catch (OperationCanceledException) { canceled = true; } Assert(canceled);
    await Put(root, "server.properties", "server"); await Reject(() => engine.AnalyzeAsync(root, pack, null, default));
});
await Test("Personal config created by update survives later edits on rollback", async () =>
{
    string root = Instance(); var pack = Pack(FileData("config/immediatelyfast.json", "{}", FilePolicy.HudPatch)); await engine.ApplyAsync(await engine.AnalyzeAsync(root, pack, null, default), false, null, default);
    await Put(root, "config/immediatelyfast.json", "{\"hud_batching\":false,\"personal\":123}"); await engine.RollbackAsync(root, null, default); Assert(Read(root, "config/immediatelyfast.json").Contains("123"));
});
await Test("Additional mod ID conflicts are quarantined only when selected", async () =>
{
    string root = Instance(); string extra = Paths.Resolve(root, "mods/duplicate.jar"); using (var zip = ZipFile.Open(extra, ZipArchiveMode.Create)) using (var writer = new StreamWriter(zip.CreateEntry("META-INF/neoforge.mods.toml").Open())) writer.Write("[[mods]]\nmodId=\"my_mod\"\n");
    var pack = Pack(FileData("mods/new.jar", "new", ids: ["my_mod"])); var plan = await engine.AnalyzeAsync(root, pack, null, default); Assert(plan.Conflicts.Length == 1);
    await Reject(() => engine.ApplyAsync(plan, false, null, default)); Assert(System.IO.File.Exists(extra)); await engine.ApplyAsync(plan, true, null, default); Assert(!System.IO.File.Exists(extra)); await engine.RollbackAsync(root, null, default); Assert(System.IO.File.Exists(extra));
});
await Test("Full-archive recovery imports by exact hash without altering instance", async () =>
{
    string root = Instance(); var file = FileData("mods/new.jar", "new") with {Url = null}; var pack = Pack(file); string archive = Path.Combine(home, "recovery.zip");
    using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create)) using (var writer = new StreamWriter(zip.CreateEntry(file.Path).Open(), new UTF8Encoding(false))) writer.Write("new");
    await Reject(async () => await engine.ApplyAsync(await engine.AnalyzeAsync(root, pack, null, default), false, null, default));
    await engine.ImportArchiveAsync(root, archive, pack, null, default); Assert(!System.IO.File.Exists(Paths.Resolve(root, file.Path))); await engine.ApplyAsync(await engine.AnalyzeAsync(root, pack, null, default), false, null, default); Assert(Read(root, file.Path) == "new");
});
await Test("Backup retention keeps three recent transactions and preserves personal copies", async () =>
{
    string root = Instance();
    async Task ApplyVersion(int version)
    { var pack = Pack(FileData("kubejs/data/retention.json", "version " + version)) with {Version = version}; await engine.ApplyAsync(await engine.AnalyzeAsync(root, pack, null, default), false, null, default); }
    await ApplyVersion(20); await Put(root, "kubejs/data/retention.json", "personal edited script"); await engine.RollbackAsync(root, null, default);
    for (int version = 21; version <= 25; version++) await ApplyVersion(version);
    string transactions = Paths.Resolve(root, ".mosslight/transactions"); string[] folders = Directory.GetDirectories(transactions); Assert(folders.Length == 4);
    Assert(Directory.GetFiles(transactions, "retention.json", SearchOption.AllDirectories).Any(p => p.Contains("user-changes") && System.IO.File.ReadAllText(p) == "personal edited script"));
    await engine.RollbackAsync(root, null, default); Assert(Read(root, "kubejs/data/retention.json") == "version 24");
});
await Test("Client-process recognition never selects dedicated server", () =>
{
    string root = Path.Combine(home, "client"); Assert(WindowsInstanceGuard.IsTargetClient("java @D:/server/win_args.txt nogui", root) == false);
    Assert(WindowsInstanceGuard.IsTargetClient("java --launchTarget neoforgeclient --gameDir \"" + root + "\"", root) == true);
    Assert(WindowsInstanceGuard.IsTargetClient("java --gameDir \"" + root + "2\"", root) == false); Assert(WindowsInstanceGuard.IsTargetClient(null, root) is null); return Task.CompletedTask;
});
await Test("Self-update rejects corrupted candidate and forged signature before exit/swap", async () =>
{
    string session = Path.Combine(home, "self-invalid"); Directory.CreateDirectory(session); string candidate = Path.Combine(session, "candidate.exe"); await System.IO.File.WriteAllTextAsync(candidate, "candidate");
    var app = new AppRelease(1, "1.0.1", Asset("new.exe"), new string('a', 64), 9); using var current = Process.GetCurrentProcess();
    var ticket = new SelfUpdateTicket(current.Id, current.StartTime.ToUniversalTime().Ticks, Path.Combine(session, "Mosslight.Update.exe"), new string('a', 64), Convert.ToBase64String(SignatureTrust.Sign(app, key)), candidate, Path.Combine(session, "healthy")); string path = Path.Combine(session, "ticket.json");
    await System.IO.File.WriteAllBytesAsync(path, Wire.Encode(ticket)); await Reject(() => SelfUpdate.ApplyDetachedAsync(path, trust, new(1,0,0))); Assert(!System.IO.File.Exists(Path.Combine(session, "ready")));
    using var other = RSA.Create(); await System.IO.File.WriteAllBytesAsync(path, Wire.Encode(ticket with {SignedReleaseBase64 = Convert.ToBase64String(SignatureTrust.Sign(app, other))})); await Reject(() => SelfUpdate.ApplyDetachedAsync(path, trust, new(1,0,0)));
});
await System.IO.File.WriteAllBytesAsync(Path.Combine(home, "results.json"), Wire.Encode(new {passed, failed, results})); Console.WriteLine($"{passed} passed, {failed} failed. {home}"); return failed == 0 ? 0 : 1;

sealed class Guard : IInstanceGuard { public bool Blocked; public Task EnsureClosedAsync(string root, CancellationToken token) { token.ThrowIfCancellationRequested(); if (Blocked) throw new UpdateRejectedException("Running fixture client"); return Task.CompletedTask; } }
sealed class FakeHttp : HttpMessageHandler
{
    public readonly Dictionary<string, byte[]> Assets = []; public int RequestCount; public long? LastRange; public bool IgnoreRange, WrongRange;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        RequestCount++; string url = request.RequestUri!.AbsoluteUri; if (!Assets.TryGetValue(url, out var data)) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        LastRange = request.Headers.Range?.Ranges.First().From; long offset = IgnoreRange ? 0 : LastRange ?? 0;
        var response = new HttpResponseMessage(offset > 0 || WrongRange ? HttpStatusCode.PartialContent : HttpStatusCode.OK) {Content = new ByteArrayContent(data[(int)offset..])};
        if (offset > 0 || WrongRange) response.Content.Headers.ContentRange = new ContentRangeHeaderValue(WrongRange ? offset + 1 : offset, data.Length - 1, data.Length);
        return Task.FromResult(response);
    }
}
