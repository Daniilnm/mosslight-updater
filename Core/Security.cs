using System.Security.Cryptography;
using System.IO.Compression;
using System.Text.RegularExpressions;

namespace Mosslight.Update;

public static partial class Paths
{
    [GeneratedRegex(@"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)", RegexOptions.IgnoreCase)]
    private static partial Regex DeviceName();
    public static string Root(string path)
    {
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!Path.IsPathFullyQualified(full) || full.StartsWith(@"\\", StringComparison.Ordinal) || full.Length < 4)
            throw new UpdateRejectedException("Нужна отдельная локальная папка сборки.");
        RejectLinks(full); return full;
    }
    public static string Resolve(string root, string relative)
    {
        ValidateRelative(relative);
        string result = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsBelow(root, result)) throw new UpdateRejectedException("Путь выходит за папку сборки.");
        RejectLinks(result, root); return result;
    }
    public static bool IsBelow(string root, string path) => Path.GetFullPath(path).StartsWith(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    public static void ValidateRelative(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 220 || value.Contains('\\') || value.StartsWith('/') || value.Contains(':'))
            throw new UpdateRejectedException("Недопустимый путь в выпуске.");
        foreach (string part in value.Split('/'))
            if (part is "" or "." or ".." || part.EndsWith('.') || part.EndsWith(' ') || DeviceName().IsMatch(part)
                || part.IndexOfAny(['<', '>', '"', '|', '?', '*']) >= 0 || part.Any(char.IsControl))
                throw new UpdateRejectedException("Недопустимое имя файла в выпуске.");
    }
    public static bool IsManagedPath(string value) =>
        (value.StartsWith("mods/", StringComparison.Ordinal) && value.EndsWith(".jar", StringComparison.Ordinal) && value.Count(c => c == '/') == 1)
        || (value.StartsWith("kubejs/", StringComparison.Ordinal) && !value.StartsWith("kubejs/config/", StringComparison.Ordinal)
            && !value.StartsWith("kubejs/.cache/", StringComparison.Ordinal))
        || value.StartsWith("config/ftbquests/quests/", StringComparison.Ordinal)
        || ConfigPatches.AllowedConfigs.Contains(value);
    public static void RequireManaged(string value)
    {
        ValidateRelative(value);
        if (!IsManagedPath(value)) throw new UpdateRejectedException("Выпуск пытается изменить личные файлы.");
    }
    public static void RejectLinks(string path, string? stop = null)
    {
        string? current = Path.GetFullPath(path);
        while (current is not null)
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new UpdateRejectedException("Ссылки и перенаправленные папки не поддерживаются: " + current);
            }
            catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { }
            if (stop is not null && string.Equals(Path.GetFullPath(stop), current, StringComparison.OrdinalIgnoreCase)) break;
            current = Path.GetDirectoryName(current);
        }
    }
    public static void WriteAtomic(string path, byte[] data)
    {
        RejectLinks(path); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + ".writing-" + Guid.NewGuid().ToString("N");
        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        { stream.Write(data); stream.Flush(true); }
        File.Move(temporary, path, true);
    }
    public static void DeleteOwnedTree(string owner, string directory)
    {
        if (!IsBelow(owner, directory)) throw new UpdateRejectedException("Отказ очистки за пределами служебной папки.");
        RejectLinks(directory, owner);
        foreach (string item in Directory.EnumerateFileSystemEntries(directory))
        {
            RejectLinks(item, owner);
            if (Directory.Exists(item)) DeleteOwnedTree(owner, item); else File.Delete(item);
        }
        Directory.Delete(directory);
    }
}

public static class Hashes
{
    public static bool IsSha256(string text) => text.Length == 64 && text.All(Uri.IsHexDigit);
    public static string Bytes(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    public static async Task<string> FileAsync(string file, CancellationToken token = default)
    {
        await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token));
    }
}

public sealed class SignatureTrust(TrustSettings settings)
{
    public TrustSettings Settings => settings;
    public T Verify<T>(byte[] signed)
    {
        if (signed.Length > 8 * 1024 * 1024) throw new UpdateRejectedException("Слишком большой манифест.");
        var envelope = Wire.Decode<SignedEnvelope>(signed);
        byte[] payload, signature;
        try { payload = Convert.FromBase64String(envelope.Payload); signature = Convert.FromBase64String(envelope.Signature); }
        catch (FormatException) { throw new UpdateRejectedException("Неверный формат подписи."); }
        using var rsa = RSA.Create(); rsa.ImportFromPem(settings.PublicKeyPem);
        if (!rsa.VerifyData(payload, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
            throw new UpdateRejectedException("Подпись выпуска не совпала. Файлы не изменены.");
        if (envelope.Format == "gzip")
        {
            using var packed = new MemoryStream(payload); using var gzip = new GZipStream(packed, CompressionMode.Decompress);
            using var plain = new MemoryStream(); byte[] buffer = new byte[65536]; int n;
            while ((n = gzip.Read(buffer)) > 0) { if (plain.Length + n > 8 * 1024 * 1024) throw new UpdateRejectedException("Слишком большой распакованный манифест."); plain.Write(buffer, 0, n); }
            payload = plain.ToArray();
        }
        else if (envelope.Format != "json") throw new UpdateRejectedException("Неизвестный формат подписанного выпуска.");
        return Wire.Decode<T>(payload);
    }
    public static byte[] Sign<T>(T value, RSA key)
    {
        byte[] payload = Wire.Encode(value);
        string format = "json";
        if (payload.Length > 512)
        {
            using var memory = new MemoryStream(); using (var gzip = new GZipStream(memory, CompressionLevel.SmallestSize, true)) gzip.Write(payload);
            payload = memory.ToArray(); format = "gzip";
        }
        return Wire.Encode(new SignedEnvelope(Convert.ToBase64String(payload), Convert.ToBase64String(
            key.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)), format));
    }
    public void ValidateAssetUrl(string url)
    {
        if (!Regex.IsMatch(settings.Repository, @"^[A-Za-z0-9][A-Za-z0-9-]*/[A-Za-z0-9_.-]+$")
            || settings.Repository.Split('/')[1] is "." or ".."
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !uri.IsDefaultPort
            || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)
            || !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            || !uri.AbsolutePath.StartsWith("/" + settings.Repository + "/releases/download/", StringComparison.Ordinal))
            throw new UpdateRejectedException("Файл обновления ведёт за пределы доверенного GitHub Releases.");
    }
    public void ValidatePackFileUrl(PackFile file)
    {
        if (file.Url is null) return;
        if (Uri.TryCreate(file.Url, UriKind.Absolute, out var github) && github.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
        { ValidateAssetUrl(file.Url); return; }
        if (!file.Path.StartsWith("mods/", StringComparison.Ordinal) || !file.Path.EndsWith(".jar", StringComparison.Ordinal)
            || file.Gzip || file.DownloadSize != file.Size || !IsPublicModUrl(file.Url))
            throw new UpdateRejectedException("Недопустимый источник стороннего мода.");
    }
    private static bool IsPublicModUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !uri.IsDefaultPort
            || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0) return false;
        string path = Uri.UnescapeDataString(uri.AbsolutePath);
        if (path.Contains('\\') || path.Split('/').Any(p => p is "." or "..")) return false;
        return uri.Host.ToLowerInvariant() switch
        {
            "cdn.modrinth.com" => Regex.IsMatch(path, @"^/data/[^/]+/versions/[^/]+/[^/]+\.jar$"),
            "edge.forgecdn.net" or "mediafilez.forgecdn.net" or "media.forgecdn.net" => Regex.IsMatch(path, @"^/files/[0-9]+/[0-9]+/[^/]+\.jar$"),
            _ => false
        };
    }
    public void ValidateDownloadRedirect(PackFile file, string redirect)
    {
        if (!Uri.TryCreate(redirect, UriKind.Absolute, out var target) || target.Scheme != "https" || !target.IsDefaultPort || target.UserInfo.Length > 0)
            throw new UpdateRejectedException("Небезопасное перенаправление загрузки.");
        if (new Uri(file.Url!).Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
        {
            if (target.Host is "release-assets.githubusercontent.com" or "objects.githubusercontent.com" && target.Fragment.Length == 0) return;
            ValidateAssetUrl(redirect); return;
        }
        ValidatePackFileUrl(file with { Url = redirect });
    }
}
