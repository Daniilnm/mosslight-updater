using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Mosslight.Update;

public static partial class ModMetadata
{
    [GeneratedRegex("(?m)^\\s*modId\\s*=\\s*[\"']([a-z0-9_.-]+)[\"']")]
    private static partial Regex ModId();
    public static string[] ReadIds(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            var entry = zip.GetEntry("META-INF/neoforge.mods.toml") ?? zip.GetEntry("META-INF/mods.toml");
            if (entry is { Length: < 1048576 }) { using var reader = new StreamReader(entry.Open()); return ModId().Matches(reader.ReadToEnd()).Select(m => m.Groups[1].Value).Distinct().ToArray(); }
            entry = zip.GetEntry("fabric.mod.json");
            if (entry is { Length: < 1048576 }) { using var stream = entry.Open(); using var doc = JsonDocument.Parse(stream); return [doc.RootElement.GetProperty("id").GetString()!]; }
        }
        catch (Exception e) when (e is InvalidDataException or IOException or JsonException or KeyNotFoundException) { }
        return [];
    }
}
