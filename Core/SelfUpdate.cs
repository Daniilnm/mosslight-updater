using System.Diagnostics;

namespace Mosslight.Update;

public sealed record SelfUpdateTicket(int ParentPid, long ParentStartedUtcTicks, string Target, string OriginalSha256,
    string SignedReleaseBase64, string Candidate, string HealthFile);

public static class SelfUpdate
{
    public const string ExecutableName = "Mosslight.Update.exe";
    public static async Task<string> PrepareAsync(ReleaseClient client, string signedReleaseUrl, string executable,
        string privateWork, Version current, IProgress<ProgressInfo>? progress, CancellationToken token)
    {
        client.Trust.ValidateAssetUrl(signedReleaseUrl);
        // Keep the original signed envelope for independent verification by the detached helper.
        byte[] signed = await client.ReadEnvelopeAsync(signedReleaseUrl, token);
        var release = client.Trust.Verify<AppRelease>(signed); client.Trust.ValidateAssetUrl(release.Url);
        if (release.Schema != 1 || !Version.TryParse(release.Version, out var version) || version <= current
            || !Hashes.IsSha256(release.Sha256) || release.Size is <= 0 or > 2_000_000_000)
            throw new UpdateRejectedException("Не удалось подтвердить новую версию обновлятора.");
        executable = Path.GetFullPath(executable); Paths.RejectLinks(executable);
        if (Path.GetFileName(executable) != ExecutableName) throw new UpdateRejectedException("Для самообновления сохраните программу под именем Mosslight.Update.exe.");
        Directory.CreateDirectory(privateWork); Paths.RejectLinks(privateWork);
        string session = Path.Combine(privateWork, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(session);
        string downloaded = await client.GetFileAsync(new("application", release.Sha256, release.Size, FilePolicy.Managed, release.Url, release.Size, false), Path.Combine(session, "cache"), progress, token);
        string candidate = Path.Combine(session, "candidate.exe"); File.Copy(downloaded, candidate);
        string helper = Path.Combine(session, "helper.exe"); File.Copy(executable, helper);
        using var parent = Process.GetCurrentProcess();
        var ticket = new SelfUpdateTicket(parent.Id, parent.StartTime.ToUniversalTime().Ticks, executable,
            await Hashes.FileAsync(executable, token), Convert.ToBase64String(signed), candidate, Path.Combine(session, "healthy"));
        string path = Path.Combine(session, "ticket.json"); Paths.WriteAtomic(path, Wire.Encode(ticket));
        return path;
    }
    public static async Task StartHelperAsync(string ticketPath)
    {
        string helper = Path.Combine(Path.GetDirectoryName(ticketPath)!, "helper.exe");
        var start = new ProcessStartInfo(helper) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        start.ArgumentList.Add("--apply-self-update"); start.ArgumentList.Add(ticketPath);
        using var helperProcess = Process.Start(start) ?? throw new IOException("Не удалось запустить помощник обновления.");
        for (int i = 0; i < 120 && !File.Exists(Path.Combine(Path.GetDirectoryName(ticketPath)!, "ready")) && !helperProcess.HasExited; i++) await Task.Delay(500);
        if (!File.Exists(Path.Combine(Path.GetDirectoryName(ticketPath)!, "ready"))) throw new IOException("Помощник обновления не подтвердил запуск. Текущая программа сохранена.");
    }
    public static async Task ApplyDetachedAsync(string ticketPath, SignatureTrust trust, Version current)
    {
        ticketPath = Path.GetFullPath(ticketPath); Paths.RejectLinks(ticketPath);
        if (new FileInfo(ticketPath).Length > 1024 * 1024) throw new UpdateRejectedException("Слишком большой запрос самообновления.");
        var ticket = Wire.Decode<SelfUpdateTicket>(await File.ReadAllBytesAsync(ticketPath));
        string session = Path.GetDirectoryName(ticketPath)!;
        if (Path.GetFullPath(ticket.Candidate) != Path.Combine(session, "candidate.exe") || Path.GetFullPath(ticket.HealthFile) != Path.Combine(session, "healthy"))
            throw new UpdateRejectedException("Недопустимый путь самообновления.");
        var release = trust.Verify<AppRelease>(Convert.FromBase64String(ticket.SignedReleaseBase64)); trust.ValidateAssetUrl(release.Url);
        if (release.Schema != 1 || Version.Parse(release.Version) <= current || !Hashes.IsSha256(release.Sha256)
            || new FileInfo(ticket.Candidate).Length != release.Size || await Hashes.FileAsync(ticket.Candidate) != release.Sha256)
            throw new UpdateRejectedException("Новая программа не прошла проверку подписи и хеша.");
        Paths.RejectLinks(ticket.Candidate); Paths.RejectLinks(ticket.Target);
        if (Path.GetFileName(ticket.Target) != ExecutableName) throw new UpdateRejectedException("Неверное имя программы для замены.");
        using (var parent = Process.GetProcessById(ticket.ParentPid))
        {
            if (parent.StartTime.ToUniversalTime().Ticks != ticket.ParentStartedUtcTicks ||
                !string.Equals(parent.MainModule?.FileName, ticket.Target, StringComparison.OrdinalIgnoreCase) ||
                await Hashes.FileAsync(ticket.Target) != ticket.OriginalSha256)
                throw new UpdateRejectedException("Родительская программа не совпала с запросом обновления.");
            Paths.WriteAtomic(Path.Combine(session, "ready"), [1]);
            using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(60)); await parent.WaitForExitAsync(wait.Token);
        }
        string directory = Path.GetDirectoryName(ticket.Target)!;
        string staged = Path.Combine(directory, ".Mosslight.Update.new.exe"), backup = Path.Combine(directory, "Mosslight.Update.previous.exe");
        Paths.RejectLinks(staged); Paths.RejectLinks(backup);
        if (await Hashes.FileAsync(ticket.Target) != ticket.OriginalSha256) throw new UpdateRejectedException("Программа изменилась перед заменой.");
        File.Copy(ticket.Candidate, staged, true);
        bool swapped = false;
        try
        {
            File.Move(ticket.Target, backup, true); File.Move(staged, ticket.Target); swapped = true;
            var start = new ProcessStartInfo(ticket.Target) { UseShellExecute = false };
            start.ArgumentList.Add("--confirm-self-update"); start.ArgumentList.Add(ticketPath);
            using var child = Process.Start(start) ?? throw new IOException("Не удалось открыть новую программу.");
            for (int i = 0; i < 90 && !File.Exists(ticket.HealthFile) && !child.HasExited; i++) await Task.Delay(500);
            if (!File.Exists(ticket.HealthFile))
            {
                // Only the verified child updater started above may be terminated here; never Java/Minecraft.
                if (!child.HasExited && string.Equals(child.MainModule?.FileName, ticket.Target, StringComparison.OrdinalIgnoreCase))
                { child.Kill(); await child.WaitForExitAsync(); }
                throw new IOException("Новая программа не подтвердила запуск.");
            }
            Paths.WriteAtomic(Path.Combine(session, "result.json"), Wire.Encode(new { success = true, release.Version }));
        }
        catch
        {
            if ((swapped || !File.Exists(ticket.Target)) && File.Exists(backup)) File.Move(backup, ticket.Target, true);
            Process.Start(new ProcessStartInfo(ticket.Target) { UseShellExecute = false, ArgumentList = { "--self-update-rolled-back" } })?.Dispose();
            throw;
        }
    }
    public static void ConfirmHealthy(string ticketPath, string executable, string privateWork)
    {
        if (!Paths.IsBelow(privateWork, ticketPath)) throw new UpdateRejectedException("Неизвестное место запроса самообновления.");
        Paths.RejectLinks(ticketPath, privateWork);
        var ticket = Wire.Decode<SelfUpdateTicket>(File.ReadAllBytes(ticketPath));
        if (!string.Equals(Path.GetFullPath(ticket.Target), Path.GetFullPath(executable), StringComparison.OrdinalIgnoreCase)
            || ticket.HealthFile != Path.Combine(Path.GetDirectoryName(ticketPath)!, "healthy")) throw new UpdateRejectedException("Не совпало место запуска новой программы.");
        Paths.WriteAtomic(ticket.HealthFile, [1]);
    }
}
