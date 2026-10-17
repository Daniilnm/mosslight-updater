using System.Diagnostics;
using System.Security.Cryptography;
using Mosslight.Update;

if (args.Length is 3 or 4 && args[0] == "--suite")
{
    string folder = args[1], candidateSource = args[2];
    string home = Paths.Root(folder); Directory.CreateDirectory(home); var results = new List<object>();
    foreach (string mode in args.Length == 4 ? new[] {"good", "bad", "legacy-good", "legacy-bad"} : new[] {"good", "bad"})
    {
        string root = Path.Combine(home, mode + "-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        using var key = RSA.Create(2048); await File.WriteAllTextAsync(Path.Combine(root, "fixture-key.pem"), key.ExportPkcs8PrivateKeyPem());
        await File.WriteAllBytesAsync(Path.Combine(root, "trust.json"), Wire.Encode(new TrustSettings("fixture/mosslight-updater", key.ExportSubjectPublicKeyInfoPem())));
        string original = mode.StartsWith("legacy-") ? args[3] : Environment.ProcessPath!;
        string target = Path.Combine(root, "Mosslight.Update.exe"); File.Copy(original, target); File.Copy(candidateSource, Path.Combine(root, "source.exe"));
        var start = new ProcessStartInfo(target) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        start.ArgumentList.Add("--parent"); start.ArgumentList.Add(mode.EndsWith("good") ? "good" : "bad"); start.Environment["MOSSLIGHT_TEST_SELF_ROOT"] = root;
        using var parent = Process.Start(start)!; using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(55)); await parent.WaitForExitAsync(timeout.Token);
        if (parent.ExitCode != 0) throw new Exception("Fixture parent failed: " + root);
        bool success = mode.EndsWith("good");
        string marker = success ? "child-healthy" : "old-restored";
        for (int i = 0; i < 100 && !File.Exists(Path.Combine(root, marker)); i++) await Task.Delay(300);
        if (!File.Exists(Path.Combine(root, marker))) throw new Exception("Self-update did not finish: " + root);
        string expected = success ? candidateSource : original;
        if (await Hashes.FileAsync(target) != await Hashes.FileAsync(expected)) throw new Exception("Wrong final executable");
        if (success && File.Exists(Path.Combine(root, "Mosslight.Update.previous.exe"))) throw new Exception("Previous executable was not removed");
        Console.WriteLine("PASS detached self-update: " + mode); results.Add(new {mode, passed = true, root});
    }
    await File.WriteAllBytesAsync(Path.Combine(home, "self-update-results.json"), Wire.Encode(results)); return 0;
}
string fixture = Environment.GetEnvironmentVariable("MOSSLIGHT_TEST_SELF_ROOT") ?? throw new Exception("Missing fixture root");
var trust = new SignatureTrust(Wire.Decode<TrustSettings>(await File.ReadAllBytesAsync(Path.Combine(fixture, "trust.json"))));
try
{
    if (args is ["--parent", var mode])
    {
        await File.WriteAllTextAsync(Path.Combine(fixture, "mode"), mode); using var key = RSA.Create(); key.ImportFromPem(await File.ReadAllTextAsync(Path.Combine(fixture, "fixture-key.pem")));
        byte[] candidate = await File.ReadAllBytesAsync(Path.Combine(fixture, "source.exe")); string url = "https://github.com/fixture/mosslight-updater/releases/download/app/app.exe", manifest = "https://github.com/fixture/mosslight-updater/releases/download/app/app.json";
        var release = new AppRelease(1, "1.0.1", url, Hashes.Bytes(candidate), candidate.Length);
        using var http = new HttpClient(new FixtureHttp(new() {[url] = candidate, [manifest] = SignatureTrust.Sign(release, key)}));
        string requestFile = await SelfUpdate.PrepareAsync(new(http, trust), manifest, Environment.ProcessPath!, Path.Combine(fixture, "self"), new(1,0,0), null, default);
        await SelfUpdate.StartHelperAsync(requestFile); return 0;
    }
    if (args is ["--apply-self-update", var ticket])
    { await SelfUpdate.ApplyDetachedAsync(ticket, trust, new(1,0,0)); return 0; }
    if (args is ["--confirm-self-update", var confirmation])
    {
        if (await File.ReadAllTextAsync(Path.Combine(fixture, "mode")) == "bad") return 9;
        SelfUpdate.ConfirmHealthy(confirmation, Environment.ProcessPath!, Path.Combine(fixture, "self"));
        if (!await SelfUpdate.CleanupConfirmedAsync(confirmation, Environment.ProcessPath!, Path.Combine(fixture, "self"), trust)) throw new Exception("Previous executable cleanup failed");
        await File.WriteAllTextAsync(Path.Combine(fixture, "child-healthy"), "1.0.1"); return 0;
    }
    if (args is ["--self-update-rolled-back"])
    { await File.WriteAllTextAsync(Path.Combine(fixture, "old-restored"), "1.0.0"); return 0; }
    throw new Exception("Unknown test mode");
}
catch (Exception e) { await File.WriteAllTextAsync(Path.Combine(fixture, "error-" + Environment.ProcessId + ".txt"), e.ToString()); return 2; }

sealed class FixtureHttp(Dictionary<string, byte[]> assets) : HttpMessageHandler
{ protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) {Content = new ByteArrayContent(assets[request.RequestUri!.AbsoluteUri])}); }
