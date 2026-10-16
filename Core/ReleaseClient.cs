using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;

namespace Mosslight.Update;

public sealed class ReleaseClient(HttpClient http, SignatureTrust trust)
{
    private readonly Dictionary<string, byte[]> _immutableManifests = new(StringComparer.Ordinal);
    public SignatureTrust Trust => trust;
    public async Task<T> ReadSignedAsync<T>(string url, CancellationToken token)
        => trust.Verify<T>(await ReadEnvelopeAsync(url, token));
    public async Task<byte[]> ReadEnvelopeAsync(string url, CancellationToken token)
    {
        trust.ValidateAssetUrl(url);
        using var response = await SendDownloadAsync(new("manifest", "", 0, FilePolicy.Managed, url), 0, token);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var bytes = new MemoryStream(); byte[] buffer = new byte[65536]; int count;
        while ((count = await stream.ReadAsync(buffer, token)) > 0)
        { if (bytes.Length + count > 8 * 1024 * 1024) throw new UpdateRejectedException("Слишком большой манифест."); bytes.Write(buffer, 0, count); }
        return bytes.ToArray();
    }
    public async Task<(UpdateChannel Channel, PackRelease Pack, AppRelease App)> CheckAsync(CancellationToken token)
    {
        var (channel, app) = await CheckAppAsync(token);
        return (channel, await CheckPackAsync(channel, token), app);
    }
    public async Task<(UpdateChannel Channel, AppRelease App)> CheckAppAsync(CancellationToken token)
    {
        UpdateChannel channel;
        try { channel = await ReadSignedAsync<UpdateChannel>(trust.Settings.InstallChannelUrl, token); }
        catch (HttpRequestException e) when (e.StatusCode == HttpStatusCode.NotFound)
        { channel = await ReadSignedAsync<UpdateChannel>(trust.Settings.ChannelUrl, token); }
        if (channel.Schema != 1 || channel.Repository != trust.Settings.Repository) throw new UpdateRejectedException("Неизвестный канал обновлений.");
        var app = await ReadImmutableAsync<AppRelease>(channel.AppUrl, token);
        if (channel.AppVersion != app.Version || app.Schema != 1
            || !Version.TryParse(app.Version, out _) || !Hashes.IsSha256(app.Sha256) || app.Size is <= 0 or > 2_000_000_000)
            throw new UpdateRejectedException("Несогласованные версии выпуска.");
        trust.ValidateAssetUrl(app.Url); return (channel, app);
    }
    public async Task<PackRelease> CheckPackAsync(UpdateChannel channel, CancellationToken token)
    {
        var pack = await ReadImmutableAsync<PackRelease>(channel.PackUrl, token); Validate(pack);
        if (channel.PackVersion != pack.Version) throw new UpdateRejectedException("Несогласованная версия сборки.");
        return pack;
    }
    private async Task<T> ReadImmutableAsync<T>(string url, CancellationToken token)
    {
        trust.ValidateAssetUrl(url);
        if (!_immutableManifests.TryGetValue(url, out var bytes)) bytes = await ReadEnvelopeAsync(url, token);
        var result = trust.Verify<T>(bytes); _immutableManifests[url] = bytes; return result;
    }
    public void Validate(PackRelease pack)
    {
        if (pack.Schema != 1 || pack.PackId != "mosslight-friends" || pack.Version <= 0 || pack.Minecraft != "1.21.1"
            || pack.Loader != "NeoForge" || !Version.TryParse(pack.MinimumUpdater, out _) || pack.Files.Length is < 1 or > 5000)
            throw new UpdateRejectedException("Этот выпуск предназначен для другой сборки.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in pack.Files)
        {
            Paths.RequireManaged(file.Path);
            if (!names.Add(file.Path) || !Hashes.IsSha256(file.Sha256) || file.Size is < 0 or > 2_000_000_000
                || file.DownloadSize is < 0 or > 2_000_000_000 || file.Policy != ConfigPatches.PolicyFor(file.Path))
                throw new UpdateRejectedException("Некорректный список файлов выпуска.");
            if (file.Url is not null) trust.ValidatePackFileUrl(file);
        }
        foreach (string path in pack.LegacyPaths) Paths.RequireManaged(path);
    }
    public async Task<string> GetFileAsync(PackFile file, string cache, IProgress<ProgressInfo>? progress, CancellationToken token)
    {
        if (!Hashes.IsSha256(file.Sha256) || file.Size < 0) throw new UpdateRejectedException("Некорректный хеш файла.");
        Paths.RejectLinks(cache); Directory.CreateDirectory(cache);
        string complete = Path.Combine(cache, file.Sha256 + ".bin"); Paths.RejectLinks(complete, cache);
        if (File.Exists(complete) && new FileInfo(complete).Length == file.Size && await Hashes.FileAsync(complete, token) == file.Sha256) return complete;
        if (file.Url is null) throw new UpdateRejectedException("Для восстановления файла «" + file.Path + "» укажите полный архив сборки через настройки.");
        trust.ValidatePackFileUrl(file);
        string part = Path.Combine(cache, file.Sha256 + ".part"); Paths.RejectLinks(part, cache);
        long existing = File.Exists(part) ? new FileInfo(part).Length : 0;
        if (file.DownloadSize > 0 && existing > file.DownloadSize) { File.Delete(part); existing = 0; }
        if (file.DownloadSize == 0 || existing != file.DownloadSize)
        {
            using var response = await SendDownloadAsync(file, existing, token);
            response.EnsureSuccessStatusCode();
            bool append = response.StatusCode == HttpStatusCode.PartialContent;
            if (append && response.Content.Headers.ContentRange?.From != existing) throw new UpdateRejectedException("Сервер вернул неверный диапазон загрузки.");
            if (!append) existing = 0;
            await using var target = new FileStream(part, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 131072, true);
            await using var input = await response.Content.ReadAsStreamAsync(token);
            byte[] buffer = new byte[131072]; int n; long received = existing;
            while ((n = await input.ReadAsync(buffer, token)) > 0)
            {
                received += n;
                if (received > (file.DownloadSize > 0 ? file.DownloadSize : 2_000_000_000)) throw new UpdateRejectedException("Размер загрузки превышает заявленный.");
                await target.WriteAsync(buffer.AsMemory(0, n), token);
                progress?.Report(new("Скачиваем " + Path.GetFileName(file.Path), received, file.DownloadSize));
            }
            await target.FlushAsync(token);
        }
        if (file.DownloadSize > 0 && new FileInfo(part).Length != file.DownloadSize) throw new IOException("Загрузка оборвалась. Повторное нажатие продолжит её.");
        string temporary = complete + ".new"; Paths.RejectLinks(temporary, cache);
        try
        {
            await using var packed = File.OpenRead(part);
            await using var target = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 131072, true);
            using Stream input = file.Gzip ? new GZipStream(packed, CompressionMode.Decompress, true) : packed;
            byte[] buffer = new byte[131072]; long length = 0; int n;
            while ((n = await input.ReadAsync(buffer, token)) > 0)
            { length += n; if (length > file.Size) throw new UpdateRejectedException("Распакованный файл превышает заявленный размер."); await target.WriteAsync(buffer.AsMemory(0, n), token); }
            if (length != file.Size) throw new UpdateRejectedException("Не совпал размер файла после загрузки.");
        }
        catch (Exception e) when (e is InvalidDataException or UpdateRejectedException)
        { if (File.Exists(temporary)) File.Delete(temporary); File.Delete(part); throw; }
        if (await Hashes.FileAsync(temporary, token) != file.Sha256) { File.Delete(temporary); File.Delete(part); throw new UpdateRejectedException("Не совпала контрольная сумма загрузки. Повторите обновление."); }
        File.Move(temporary, complete, true); File.Delete(part); return complete;
    }
    private async Task<HttpResponseMessage> SendDownloadAsync(PackFile file, long offset, CancellationToken token)
    {
        string url = file.Url!;
        for (int redirects = 0; redirects <= 5; redirects++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (offset > 0) request.Headers.Range = new RangeHeaderValue(offset, null);
            var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if (response.StatusCode is not (HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.RedirectMethod or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)) return response;
            var location = response.Headers.Location; response.Dispose();
            if (location is null) throw new UpdateRejectedException("Источник не указал адрес перенаправления.");
            url = new Uri(new Uri(url), location).AbsoluteUri; trust.ValidateDownloadRedirect(file, url);
        }
        throw new UpdateRejectedException("Слишком много перенаправлений загрузки.");
    }
}
