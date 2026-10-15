using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Mosslight.Update;

public sealed partial class WindowsInstanceGuard : IInstanceGuard
{
    [GeneratedRegex("(?:^|\\s)--gameDir(?:=|\\s+)(?:\"([^\"]+)\"|(\\S+))", RegexOptions.IgnoreCase)]
    private static partial Regex GameDirectory();
    public static bool? IsTargetClient(string? command, string root)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        string lower = command.ToLowerInvariant();
        if (lower.Contains("win_args.txt") || lower.Contains("neoforgeserver") || lower.Contains("forgeserver") || lower.Contains("server.jar")) return false;
        var directory = GameDirectory().Match(command);
        if (directory.Success)
        {
            string value = directory.Groups[1].Success ? directory.Groups[1].Value : directory.Groups[2].Value;
            try { return Path.GetFullPath(value).TrimEnd('\\', '/').Equals(Path.GetFullPath(root).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase); }
            catch (ArgumentException) { return null; }
        }
        if (lower.Contains("minecraft") || lower.Contains("neoforgeclient") || lower.Contains("forgeclient")) return null;
        return false;
    }
    public async Task EnsureClosedAsync(string root, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows()) throw new UpdateRejectedException("Обновлятор предназначен для Windows.");
        const string script = "[Console]::OutputEncoding=[Text.UTF8Encoding]::new(); @((Get-CimInstance Win32_Process -Filter \"Name='java.exe' OR Name='javaw.exe'\" -ErrorAction Stop) | Select-Object ProcessId,CommandLine) | ConvertTo-Json -Compress";
        var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8 };
        start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive"); start.ArgumentList.Add("-EncodedCommand"); start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
        using var process = Process.Start(start) ?? throw new UpdateRejectedException("Не удалось проверить, закрыта ли игра.");
        string output = await process.StandardOutput.ReadToEndAsync(token); string error = await process.StandardError.ReadToEndAsync(token);
        await process.WaitForExitAsync(token);
        if (process.ExitCode != 0) throw new UpdateRejectedException("Windows не разрешила проверить процессы. Файлы не изменены. Закройте Minecraft и повторите проверку." + (error.Length > 0 ? "" : ""));
        if (string.IsNullOrWhiteSpace(output)) return;
        JsonNode? json;
        try { json = JsonNode.Parse(output); } catch (System.Text.Json.JsonException) { throw new UpdateRejectedException("Не удалось прочитать список процессов Windows."); }
        IEnumerable<JsonNode?> rows = json is JsonArray list ? list : [json];
        foreach (var row in rows)
        {
            if (row is null) continue;
            bool? target = IsTargetClient(row["CommandLine"]?.GetValue<string>(), root);
            if (target == true) throw new UpdateRejectedException("Сначала полностью закройте Minecraft этой сборки. Обновлятор не будет закрывать его за вас.");
            if (target is null) throw new UpdateRejectedException("Найден Java-процесс, принадлежность которого не удалось проверить. Закройте игровые клиенты и повторите проверку.");
        }
    }
}
