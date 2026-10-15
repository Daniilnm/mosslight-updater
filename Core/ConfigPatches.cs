using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Mosslight.Update;

public static class ConfigPatches
{
    public static readonly HashSet<string> AllowedConfigs = new(StringComparer.Ordinal)
    {
        "config/improvedmobs/common.toml", "config/dimensional_tears-common.toml", "config/netherexp-common.toml",
        "config/coldsweat/client.toml", "config/jade/jade.json", "config/coldsweat/item.toml",
        "config/submarine_hull.json", "config/modernfix-mixins.properties", "config/immediatelyfast.json"
    };
    public static FilePolicy PolicyFor(string path) => path switch
    {
        "config/immediatelyfast.json" => FilePolicy.HudPatch,
        "config/modernfix-mixins.properties" => FilePolicy.ModernFixPatch,
        "config/coldsweat/client.toml" => FilePolicy.ColdSweatPatch,
        "config/jade/jade.json" => FilePolicy.Seed,
        _ => FilePolicy.Managed
    };
    public static bool IsPatch(FilePolicy policy) => policy is FilePolicy.HudPatch or FilePolicy.ModernFixPatch or FilePolicy.ColdSweatPatch;
    private static string Text(byte[] bytes) => Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');
    public static byte[] Apply(FilePolicy policy, byte[] source) => Change(policy, source, null, false);
    public static byte[] Restore(FilePolicy policy, byte[] current, byte[] original) => Change(policy, current, original, true);
    private static byte[] Change(FilePolicy policy, byte[] bytes, byte[]? original, bool undo)
    {
        string text = Text(bytes), result;
        if (policy == FilePolicy.HudPatch)
        {
            JsonObject root;
            try { root = JsonNode.Parse(text) as JsonObject ?? throw new FormatException(); }
            catch (Exception e) when (e is System.Text.Json.JsonException or FormatException) { throw new UpdateRejectedException("Не удалось прочитать настройки ImmediatelyFast. Исходный файл сохранён."); }
            JsonNode? previous = undo ? (JsonNode.Parse(Text(original!)) as JsonObject)?["hud_batching"]?.DeepClone() : JsonValue.Create(false);
            if (!undo && root["hud_batching"] is not null && !root["hud_batching"]!.AsValue().TryGetValue<bool>(out _))
                throw new UpdateRejectedException("Неожиданный тип hud_batching. Настройки не изменены.");
            if (previous is null) root.Remove("hud_batching"); else root["hud_batching"] = previous;
            result = root.ToJsonString(new() { WriteIndented = true }) + "\n";
            // Preserve bytes when the required value is already present.
            if (!undo && JsonNode.Parse(text)?["hud_batching"]?.ToString() == "false") return bytes;
        }
        else if (policy == FilePolicy.ModernFixPatch)
        {
            const string pattern = @"(?m)^[\t ]*mixin\.perf\.faster_item_rendering[\t ]*=[^\r\n]*";
            if (!undo && Regex.IsMatch(text, @"(?m)^[\t ]*mixin\.perf\.faster_item_rendering[\t ]*=[\t ]*false[\t ]*\r?$")) return bytes;
            var old = undo ? Regex.Match(Text(original!), pattern) : null;
            string? replacement = undo ? (old!.Success ? old.Value : null) : "mixin.perf.faster_item_rendering=false";
            result = Regex.IsMatch(text, pattern) ? Regex.Replace(text, pattern, _ => replacement ?? "")
                : replacement is null ? text : text.TrimEnd() + "\n" + replacement + "\n";
        }
        else if (policy == FilePolicy.ColdSweatPatch)
        {
            const string header = @"(?m)^[\t ]*\[Preferences\][\t ]*\r?$";
            var section = Regex.Match(text, header);
            if (!section.Success) throw new UpdateRejectedException("В настройках Cold Sweat не найден раздел Preferences.");
            int start = section.Index + section.Length;
            var next = Regex.Match(text[start..], @"(?m)^[\t ]*\[[^\r\n]+\]");
            int end = next.Success ? start + next.Index : text.Length;
            string body = text[start..end];
            const string key = @"(?m)^[\t ]*""Show Water Effect""[\t ]*=[^\r\n]*";
            if (!undo && Regex.Match(body, key) is { Success: true } already && Regex.IsMatch(already.Value, @"=[\t ]*0[\t ]*(?:#.*)?$")) return bytes;
            string? desired = "\t\"Show Water Effect\" = 0";
            if (undo)
            {
                var match = Regex.Match(Text(original!), key); desired = match.Success ? match.Value : null;
            }
            else if (Regex.Match(body, key) is { Success: true } current && !Regex.IsMatch(current.Value, @"=[\t ]*[0-3][\t ]*(?:#.*)?$"))
                throw new UpdateRejectedException("Неожиданное значение Show Water Effect. Настройки не изменены.");
            body = Regex.IsMatch(body, key) ? Regex.Replace(body, key, _ => desired ?? "")
                : desired is null ? body : "\n" + desired + "\n" + body;
            result = text[..start] + body + text[end..];
        }
        else return bytes;
        return result == text ? bytes : Encoding.UTF8.GetBytes(result);
    }
}
