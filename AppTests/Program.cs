using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Windows;
using Mosslight.Update;
using Mosslight.Update.App;

static class Program
{
    [STAThread] static int Main(string[] args)
    {
        int result = 1; var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        application.Startup += async (_, _) =>
        {
            try { await Run(args); result = 0; } catch (Exception e) { Console.Error.WriteLine(e); }
            application.Shutdown(result);
        };
        application.Run(); return result;
    }
    static async Task Run(string[] args)
    {
        string home = Path.Combine(Path.GetFullPath(args.FirstOrDefault() ?? "test-results/app"), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(home);
        using var key = RSA.Create(2048); var trust = new TrustSettings("fixture/mosslight-updater", key.ExportSubjectPublicKeyInfoPem());
        string url = "https://github.com/fixture/mosslight-updater/releases/download/test/";
        var channel = new UpdateChannel(1, trust.Repository, 23, url + "pack.json", "1.2.0", url + "app.json");
        var handler = new Fixture(); handler.Assets[trust.InstallChannelUrl] = SignatureTrust.Sign(channel, key);
        handler.Assets[channel.AppUrl] = SignatureTrust.Sign(new AppRelease(1, "1.2.0", url + "app.exe", new string('a',64), 100), key);
        var vm = new LauncherViewModel(trust, Path.Combine(home,"no-folder"), handler: handler, guard: new Closed());
        await vm.InitializeAsync();
        if (vm.AppUpdateVisibility != Visibility.Visible || vm.InstancePath != "Папка ещё не выбрана" || handler.PackRequests != 0) throw new Exception("App update unavailable without selected folder");
        if (!vm.UpdateAppCommand.CanExecute(null)) throw new Exception("Standalone update command is disabled");
        Console.WriteLine("PASS startup without a folder discovers app update without needing a pack.");
        string root = Path.Combine(home,"versions","4"); Directory.CreateDirectory(Path.Combine(root,"mods"));
        string settings = Path.Combine(home,"empty-profile"); Directory.CreateDirectory(settings);
        await File.WriteAllBytesAsync(Path.Combine(settings,"settings.json"),Wire.Encode(new Settings(root)));
        var f = new PackFile("mods/fixture.jar", new string('a',64), 10, FilePolicy.Managed, url + "fixture.gz", 10);
        handler.Assets[channel.PackUrl] = SignatureTrust.Sign(new PackRelease(1,"mosslight-friends",23,"1.21.1","NeoForge","1.1.0",["test"],[f],[]),key);
        vm = new LauncherViewModel(trust, settings, handler: handler, guard: new Closed()); await vm.InitializeAsync();
        if (vm.MainButtonLabel != "Установить сборку" || !vm.StatusTitle.Contains("первая установка") || vm.AppUpdateVisibility != Visibility.Visible) throw new Exception("Empty-profile install not offered: " + vm.StatusDetail);
        Console.WriteLine("PASS saved empty profile offers first installation and app update.");
        handler.Assets[channel.PackUrl] = [1,2,3];
        vm = new LauncherViewModel(trust,settings,handler:handler,guard:new Closed()); await vm.InitializeAsync();
        if (vm.AppUpdateVisibility != Visibility.Visible || vm.Busy) throw new Exception("Invalid pack hid self-update");
        Console.WriteLine("PASS corrupt pack metadata does not hide app update.");
        channel = channel with { AppVersion = App.CurrentVersion.ToString(3) };
        handler.Assets[trust.InstallChannelUrl] = SignatureTrust.Sign(channel, key);
        handler.Assets[channel.AppUrl] = SignatureTrust.Sign(new AppRelease(1, App.CurrentVersion.ToString(3), url + "app.exe", new string('a',64), 100), key);
        vm = new LauncherViewModel(trust, Path.Combine(home,"current-no-folder"), handler: handler, guard: new Closed());
        await vm.InitializeAsync();
        if (!vm.UpdateAppCommand.CanExecute(null)) throw new Exception("Current app cannot check its own updates");
        int packRequests = handler.PackRequests;
        vm.UpdateAppCommand.Execute(null);
        for (int i=0; vm.Busy && i<100; i++) await Task.Delay(20);
        if (vm.Busy || vm.StatusTitle != "Mosslight обновлён" || handler.PackRequests != packRequests || !vm.AppStatusDetail.Contains("последняя")) throw new Exception("Standalone app check failed or accessed game pack: " + vm.StatusDetail);
        Console.WriteLine("PASS visible standalone button checks current app without a folder or pack.");
        await File.WriteAllBytesAsync(Path.Combine(home,"app-results.json"),Wire.Encode(new {passed=true, scenarios=4, gameProcessActions="NONE"}));
    }
    sealed record Settings(string InstancePath);
    sealed class Closed : IInstanceGuard { public Task EnsureClosedAsync(string root,CancellationToken token)=>Task.CompletedTask; }
    sealed class Fixture : HttpMessageHandler
    {
        public Dictionary<string,byte[]> Assets = []; public int PackRequests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            if (request.RequestUri!.AbsoluteUri.EndsWith("pack.json")) PackRequests++;
            return Task.FromResult(Assets.TryGetValue(request.RequestUri.AbsoluteUri,out var data)
                ? new HttpResponseMessage(HttpStatusCode.OK) {Content=new ByteArrayContent(data)} : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
