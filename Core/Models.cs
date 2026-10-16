using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mosslight.Update;

public static class Wire
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter<FilePolicy>() }
    };
    public static byte[] Encode<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Json);
    public static T Decode<T>(byte[] value) => JsonSerializer.Deserialize<T>(value, Json) ?? throw new InvalidDataException("Пустой документ обновления.");
}

public enum FilePolicy { Managed, Seed, HudPatch, ModernFixPatch, ColdSweatPatch }
public sealed record PackFile(string Path, string Sha256, long Size, FilePolicy Policy, string? Url = null,
    long DownloadSize = 0, bool Gzip = true, string[]? ModIds = null);
public sealed record PackRelease(int Schema, string PackId, int Version, string Minecraft, string Loader,
    string MinimumUpdater, string[] Notes, PackFile[] Files, string[] LegacyPaths, string? FullArchiveSha256 = null);
public sealed record AppRelease(int Schema, string Version, string Url, string Sha256, long Size);
public sealed record UpdateChannel(int Schema, string Repository, int PackVersion, string PackUrl, string AppVersion, string AppUrl);
public sealed record SignedEnvelope(string Payload, string Signature, string Format = "json");
public sealed record TrustSettings(string Repository, string PublicKeyPem)
{
    public string ChannelUrl => $"https://github.com/{Repository}/releases/download/stable/channel.json";
    [JsonIgnore] public string InstallChannelUrl => $"https://github.com/{Repository}/releases/download/stable/install-channel.json";
}
public sealed record InstalledFile(string Path, string Sha256, FilePolicy Policy);
public sealed record InstalledState(int Schema, string PackId, int Version, InstalledFile[] Files);
public sealed record ProgressInfo(string Message, long Bytes = 0, long Total = 0, bool Critical = false)
{
    public double Percent => Total > 0 ? Math.Clamp(Bytes * 100d / Total, 0, 100) : 0;
}
public sealed record Change(PackFile? File, string Path, string? BeforeHash, byte[]? PreparedBytes = null)
{
    public bool Delete => File is null;
}
public sealed record UpdatePlan(string Root, PackRelease Release, Change[] Changes, string[] Conflicts,
    int ExtraMods, InstalledState? PreviousState, bool FirstInstall = false)
{
    public long DownloadBytes => Changes.Where(c => c.File is { Url: not null } && c.PreparedBytes is null)
        .GroupBy(c => c.File!.Sha256).Sum(g => g.First().File!.DownloadSize > 0 ? g.First().File!.DownloadSize : g.First().File!.Size);
}
public sealed record JournalEntry(string Path, string? BeforeHash, string? AfterHash, FilePolicy Policy, bool Applied = false);
public sealed record UpdateJournal(int Schema, string Id, string Phase, int TargetVersion, string? PreviousState,
    JournalEntry[] Entries, DateTimeOffset Created, bool HasUserChanges = false);

public interface IInstanceGuard { Task EnsureClosedAsync(string root, CancellationToken token); }
public sealed class UpdateRejectedException(string message) : Exception(message);
