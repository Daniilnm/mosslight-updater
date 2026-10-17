using System.IO;
using System.Reflection;
using System.Windows;

namespace Mosslight.Update.App;

public partial class App : Application
{
    public static readonly Version CurrentVersion = new(Assembly.GetExecutingAssembly().GetName().Version!.ToString(3));
    private static TrustSettings ReadTrust()
    {
        using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Mosslight.Update.App.ReleaseTrust.json") ?? throw new IOException("Не найден ключ выпуска.");
        using var bytes = new MemoryStream(); stream.CopyTo(bytes); return Wire.Decode<TrustSettings>(bytes.ToArray());
    }
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        string data = Environment.GetEnvironmentVariable("MOSSLIGHT_UPDATE_DATA") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mosslight");
        var trust = ReadTrust();
        if (e.Args is ["--apply-self-update", var ticket])
        {
            try { await SelfUpdate.ApplyDetachedAsync(ticket, new(trust), CurrentVersion); Shutdown(0); }
            catch (Exception error) { try { Paths.WriteAtomic(Path.Combine(Path.GetDirectoryName(ticket)!, "error.txt"), System.Text.Encoding.UTF8.GetBytes(error.Message)); } catch (IOException) { } Shutdown(2); }
            return;
        }
        bool render = e.Args.Length is 2 or 3 && e.Args[0] == "--render-preview";
        var vm = new LauncherViewModel(trust, data, render); var window = new MainWindow(vm); MainWindow = window;
        if (render) { string state = e.Args.Length == 3 ? e.Args[2] : "home"; vm.SetPreviewState(state); window.SavePreview(e.Args[1], state); Shutdown(); return; }
        if (e.Args is ["--self-update-rolled-back"]) vm.SetPreviewState("rollback");
        bool confirmed = false;
        window.ContentRendered += async (_, _) =>
        {
            if (!confirmed && e.Args is ["--confirm-self-update", var confirmation])
            {
                confirmed = true;
                SelfUpdate.ConfirmHealthy(confirmation, Environment.ProcessPath!, Path.Combine(data, "self-update"));
                try { await SelfUpdate.CleanupConfirmedAsync(confirmation, Environment.ProcessPath!, Path.Combine(data, "self-update"), new(trust)); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or UpdateRejectedException or System.Text.Json.JsonException) { }
            }
        };
        window.Show(); await vm.InitializeAsync();
    }
}
