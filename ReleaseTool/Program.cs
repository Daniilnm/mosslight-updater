using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Mosslight.Update;

// Publisher tool never reads the installed game or starts any game process.
try { await Run(args); return 0; }
catch (Exception e) { Console.Error.WriteLine(e.Message); return 1; }

static async Task Run(string[] args)
{
    if (args is ["configure", var repo, var privateKey, var destination])
    {
        using var rsa = RSA.Create(); rsa.ImportFromPem(await File.ReadAllTextAsync(privateKey));
        var settings = new TrustSettings(repo, rsa.ExportSubjectPublicKeyInfoPem()); new SignatureTrust(settings).ValidateAssetUrl(settings.ChannelUrl);
        await File.WriteAllBytesAsync(destination, Wire.Encode(settings)); Console.WriteLine("Закреплены репозиторий и открытый ключ выпуска."); return;
    }
    if (args is ["keygen", var directory])
    {
        directory = Paths.Root(directory); Directory.CreateDirectory(directory);
        string key = Path.Combine(directory, "release-private.pem");
        if (File.Exists(key)) throw new IOException("Ключ уже существует. Автоматическая замена запрещена.");
        using var rsa = RSA.Create(3072); await File.WriteAllTextAsync(key, rsa.ExportPkcs8PrivateKeyPem());
        await File.WriteAllTextAsync(Path.Combine(directory, "release-public.pem"), rsa.ExportSubjectPublicKeyInfoPem());
        Console.WriteLine("Ключ создан. Закрытый ключ хранить отдельно и не публиковать."); return;
    }
    if (args is ["build", var configFile])
    {
        var config = Wire.Decode<BuildConfig>(await File.ReadAllBytesAsync(configFile));
        using var key = RSA.Create(); key.ImportFromPem(await File.ReadAllTextAsync(config.PrivateKey));
        var trust = new SignatureTrust(new(config.Repository, key.ExportSubjectPublicKeyInfoPem()));
        string packBase = $"https://github.com/{config.Repository}/releases/download/pack-v{config.PackVersion}/";
        trust.ValidateAssetUrl(packBase + "pack.json");
        string output = Paths.Root(config.Output); Directory.CreateDirectory(output);
        string packDir = Path.Combine(output, "pack-v" + config.PackVersion), appDir = Path.Combine(output, "app-v" + config.AppVersion), channelDir = Path.Combine(output, "stable");
        foreach (string dir in new[] {packDir, appDir, channelDir})
        { if (Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any()) throw new IOException("Папка выпуска не пуста: " + dir); Directory.CreateDirectory(dir); }
        var previous = config.PreviousPack is null ? null : trust.Verify<PackRelease>(await File.ReadAllBytesAsync(config.PreviousPack));
        var priorFiles = previous?.Files.GroupBy(f => f.Sha256).ToDictionary(g => g.Key, g => g.First()) ?? [];
        // Explicit hash approvals only. A filename or an inferred licence is not permission to mirror an arbitrary jar.
        var approved = config.ApprovedJarHashes.ToHashSet(StringComparer.Ordinal);
        var external = config.Sources is null ? new Dictionary<string, ExternalSource>(StringComparer.Ordinal) :
            Wire.Decode<ExternalSource[]>(await File.ReadAllBytesAsync(config.Sources)).ToDictionary(f => f.Path, StringComparer.Ordinal);
        var files = new List<PackFile>(); var archiveOnly = new List<string>();
        using (var zip = ZipFile.OpenRead(config.Archive))
        foreach (var entry in zip.Entries.Where(e => !string.IsNullOrEmpty(e.Name)).OrderBy(e => e.FullName, StringComparer.Ordinal))
        {
            string name = entry.FullName; Paths.RequireManaged(name);
            if (entry.Length > 2_000_000_000) throw new IOException("Слишком большой файл: " + name);
            using var bytes = new MemoryStream(); using (var stream = entry.Open()) await stream.CopyToAsync(bytes);
            byte[] data = bytes.ToArray(); string sha = Hashes.Bytes(data); string? url = null; long downloadSize = 0;
            bool mayHost = !name.StartsWith("mods/", StringComparison.Ordinal) || approved.Contains(sha);
            string[]? ids = null;
            if (name.StartsWith("mods/", StringComparison.Ordinal))
            {
                string temporary = Path.Combine(output, ".metadata-" + Guid.NewGuid().ToString("N") + ".jar");
                try { await File.WriteAllBytesAsync(temporary, data); ids = ModMetadata.ReadIds(temporary); }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
            if (mayHost && priorFiles.TryGetValue(sha, out var old) && old.Url is not null)
            { trust.ValidateAssetUrl(old.Url); url = old.Url; downloadSize = old.DownloadSize; }
            else if (mayHost)
            {
                string asset = "blob-" + sha + ".gz", path = Path.Combine(packDir, asset);
                if (!File.Exists(path))
                { await using var target = File.Create(path); await using var gzip = new GZipStream(target, CompressionLevel.SmallestSize); await gzip.WriteAsync(data); }
                url = packBase + asset; downloadSize = new FileInfo(path).Length;
            }
            else
            {
                if (external.TryGetValue(name, out var origin))
                {
                    if (origin.Sha256 != sha || origin.Size != data.LongLength) throw new IOException("Источник относится к другой версии файла: " + name);
                    var sourced = new PackFile(name, sha, data.LongLength, ConfigPatches.PolicyFor(name), origin.Url, data.LongLength, false, ids);
                    trust.ValidatePackFileUrl(sourced); files.Add(sourced); continue;
                }
                if (config.RequireComplete || previous is not null && !priorFiles.ContainsKey(sha))
                    throw new IOException("Новый сторонний JAR без подтверждённого разрешения на размещение: " + name + ". Сначала проверьте лицензию и добавьте разрешённый SHA-256; выпуск не публиковался.");
                archiveOnly.Add(name);
            }
            files.Add(new(name, sha, data.LongLength, ConfigPatches.PolicyFor(name), url, downloadSize, true, ids));
        }
        var legacy = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string archive in config.LegacyArchives)
        { using var zip = ZipFile.OpenRead(archive); foreach (var e in zip.Entries.Where(e => !string.IsNullOrEmpty(e.Name))) { Paths.ValidateRelative(e.FullName); if (Paths.IsManagedPath(e.FullName)) legacy.Add(e.FullName); } }
        if (previous is not null) foreach (string path in previous.Files.Select(f => f.Path).Concat(previous.LegacyPaths)) legacy.Add(path);
        var pack = new PackRelease(1, "mosslight-friends", config.PackVersion, "1.21.1", "NeoForge", config.MinimumUpdater, config.Notes,
            files.ToArray(), legacy.Order(StringComparer.Ordinal).ToArray(), await Hashes.FileAsync(config.Archive));
        using var http = new HttpClient(); new ReleaseClient(http, trust).Validate(pack);
        await File.WriteAllBytesAsync(Path.Combine(packDir, "pack.json"), SignatureTrust.Sign(pack, key));
        string exe = Path.Combine(appDir, SelfUpdate.ExecutableName); File.Copy(config.Executable, exe);
        var app = new AppRelease(1, config.AppVersion, $"https://github.com/{config.Repository}/releases/download/app-v{config.AppVersion}/{SelfUpdate.ExecutableName}", await Hashes.FileAsync(exe), new FileInfo(exe).Length);
        await File.WriteAllBytesAsync(Path.Combine(appDir, "app.json"), SignatureTrust.Sign(app, key));
        var channel = new UpdateChannel(1, config.Repository, config.PackVersion, packBase + "pack.json", config.AppVersion,
            $"https://github.com/{config.Repository}/releases/download/app-v{config.AppVersion}/app.json");
        if (config.CompatibilityPack is not null)
        {
            byte[] compatBytes = await File.ReadAllBytesAsync(config.CompatibilityPack);
            var compatibility = trust.Verify<PackRelease>(compatBytes); new ReleaseClient(http, trust).Validate(compatibility);
            if (compatibility.Files.Any(f => f.Url is not null && new Uri(f.Url).Host != "github.com")) throw new IOException("Каталог совместимости не подходит для старого приложения.");
            await File.WriteAllBytesAsync(Path.Combine(output, "compatibility-pack.json"), compatBytes);
            await File.WriteAllBytesAsync(Path.Combine(channelDir, "install-channel.json"), SignatureTrust.Sign(channel, key));
            channel = channel with { PackVersion = compatibility.Version, PackUrl = $"https://github.com/{config.Repository}/releases/download/pack-v{compatibility.Version}/pack.json" };
        }
        await File.WriteAllBytesAsync(Path.Combine(channelDir, "channel.json"), SignatureTrust.Sign(channel, key));
        int githubCount = files.Count(f => f.Url is not null && new Uri(f.Url).Host == "github.com");
        await File.WriteAllBytesAsync(Path.Combine(output, "catalog-report.json"), Wire.Encode(new { config.Repository, config.PackVersion, files = files.Count, hostedFiles = githubCount, externalFiles = files.Count(f => f.Url is not null) - githubCount, archiveOnly, uploadBytes = Directory.GetFiles(packDir).Sum(p => new FileInfo(p).Length), fullArchiveBytes = new FileInfo(config.Archive).Length }));
        Console.WriteLine($"Подготовлено: {files.Count} файлов; GitHub: {githubCount}; сторонние источники: {files.Count(f => f.Url is not null) - githubCount}. Публикации не было."); return;
    }
    if (args is ["publish" or "validate", var releaseDirectory, var repository, var publicKey])
    {
        var trust = new SignatureTrust(new(repository, await File.ReadAllTextAsync(publicKey)));
        string installChannelPath = Path.Combine(releaseDirectory, "stable", "install-channel.json");
        var channel = trust.Verify<UpdateChannel>(await File.ReadAllBytesAsync(File.Exists(installChannelPath) ? installChannelPath : Path.Combine(releaseDirectory, "stable", "channel.json")));
        if (channel.Repository != repository) throw new IOException("Репозиторий не совпал.");
        trust.ValidateAssetUrl(channel.PackUrl); trust.ValidateAssetUrl(channel.AppUrl);
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(30) };
        var pack = trust.Verify<PackRelease>(await File.ReadAllBytesAsync(Path.Combine(releaseDirectory, "pack-v" + channel.PackVersion, "pack.json")));
        var app = trust.Verify<AppRelease>(await File.ReadAllBytesAsync(Path.Combine(releaseDirectory, "app-v" + channel.AppVersion, "app.json")));
        new ReleaseClient(http, trust).Validate(pack); trust.ValidateAssetUrl(app.Url);
        string executable = Path.Combine(releaseDirectory, "app-v" + channel.AppVersion, SelfUpdate.ExecutableName);
        if (pack.Version != channel.PackVersion || app.Version != channel.AppVersion || app.Schema != 1
            || new FileInfo(executable).Length != app.Size || await Hashes.FileAsync(executable) != app.Sha256) throw new IOException("Выпуск программы изменён после подписания.");
        string currentPack = $"https://github.com/{repository}/releases/download/pack-v{pack.Version}/";
        var expectedAssets = new Dictionary<string, HashSet<string>> {
            ["pack-v" + pack.Version] = pack.Files.Where(f => f.Url?.StartsWith(currentPack, StringComparison.Ordinal) == true).Select(f => "blob-" + f.Sha256 + ".gz").Append("pack.json").ToHashSet(StringComparer.Ordinal),
            ["app-v" + app.Version] = ["app.json", SelfUpdate.ExecutableName], ["stable"] = File.Exists(installChannelPath) ? ["channel.json", "install-channel.json"] : ["channel.json"] };
        if (File.Exists(installChannelPath))
        {
            var legacy = trust.Verify<UpdateChannel>(await File.ReadAllBytesAsync(Path.Combine(releaseDirectory, "stable", "channel.json")));
            var compat = trust.Verify<PackRelease>(await File.ReadAllBytesAsync(Path.Combine(releaseDirectory, "compatibility-pack.json")));
            new ReleaseClient(http, trust).Validate(compat);
            if (legacy.Repository != repository || legacy.PackVersion != compat.Version || legacy.AppUrl != channel.AppUrl || legacy.AppVersion != app.Version
                || legacy.PackUrl != $"https://github.com/{repository}/releases/download/pack-v{compat.Version}/pack.json") throw new IOException("Неверный канал совместимости.");
        }
        foreach (var (tag, names) in expectedAssets)
        {
            var actual = Directory.GetFiles(Path.Combine(releaseDirectory, tag)).Select(Path.GetFileName).OfType<string>().ToHashSet(StringComparer.Ordinal);
            if (!names.SetEquals(actual)) throw new IOException("Лишний или отсутствующий файл в папке выпуска: " + tag);
        }
        foreach (var file in pack.Files.Where(f => f.Url?.StartsWith(currentPack, StringComparison.Ordinal) == true))
        {
            string path = Path.Combine(releaseDirectory, "pack-v" + pack.Version, "blob-" + file.Sha256 + ".gz");
            if (new FileInfo(path).Length != file.DownloadSize) throw new IOException("Изменён размер подготовленного файла: " + file.Path);
            await using var source = File.OpenRead(path); await using var gzip = new GZipStream(source, CompressionMode.Decompress);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); byte[] buffer = new byte[131072]; long length = 0; int n;
            while ((n = await gzip.ReadAsync(buffer)) > 0) { length += n; if (length > file.Size) throw new IOException("Неверный размер файла: " + file.Path); hash.AppendData(buffer, 0, n); }
            if (length != file.Size || Convert.ToHexStringLower(hash.GetHashAndReset()) != file.Sha256) throw new IOException("Изменён подготовленный файл: " + file.Path);
        }
        if (args[0] == "validate") { Console.WriteLine("PASS подписи, размеры, SHA-256 программы и всех новых файлов. Лишних файлов нет. Публикации не было."); return; }
        string token = Environment.GetEnvironmentVariable("GH_TOKEN") ?? throw new IOException("Для публикации нужен GH_TOKEN с доступом Contents: write к выбранному репозиторию. Не передавайте токен в командной строке.");
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mosslight-ReleaseTool/1.0.0");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2026-03-10");
        // Only existing repositories. Never silently create or change visibility of a repository.
        using (var check = await http.GetAsync($"https://api.github.com/repos/{repository}")) check.EnsureSuccessStatusCode();
        foreach (string tag in new[] { "pack-v" + channel.PackVersion, "app-v" + channel.AppVersion, "stable" })
        {
            long id; JsonObject release;
            using (var existing = await http.GetAsync($"https://api.github.com/repos/{repository}/releases/tags/{tag}"))
            {
                if (existing.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    using var content = new ByteArrayContent(Wire.Encode(new {tag_name = tag, name = tag == "stable" ? "Mosslight · канал обновлений" : "Mosslight · " + tag, draft = true, body = "Автоматический выпуск Mosslight. Файлы и манифесты проверяются обновлятором."})); content.Headers.ContentType = new("application/json");
                    using var created = await http.PostAsync($"https://api.github.com/repos/{repository}/releases", content); created.EnsureSuccessStatusCode();
                    release = JsonNode.Parse(await created.Content.ReadAsStringAsync())!.AsObject();
                }
                else { existing.EnsureSuccessStatusCode(); release = JsonNode.Parse(await existing.Content.ReadAsStringAsync())!.AsObject(); }
            }
            id = release["id"]!.GetValue<long>(); bool draft = release["draft"]!.GetValue<bool>();
            var assets = new List<(string Name, long Id, string? Digest)>();
            for (int page = 1; ; page++)
            {
                using var listed = await http.GetAsync($"https://api.github.com/repos/{repository}/releases/{id}/assets?per_page=100&page={page}"); listed.EnsureSuccessStatusCode();
                var batch = JsonNode.Parse(await listed.Content.ReadAsStringAsync())!.AsArray();
                assets.AddRange(batch.Select(a => (a!["name"]!.GetValue<string>(), a["id"]!.GetValue<long>(), a["digest"]?.GetValue<string>())));
                if (batch.Count < 100) break; if (page >= 20) throw new IOException("Слишком много файлов в выпуске.");
            }
            foreach (string path in Directory.GetFiles(Path.Combine(releaseDirectory, tag)))
            {
                string name = Path.GetFileName(path), digest = "sha256:" + await Hashes.FileAsync(path); var old = assets.FirstOrDefault(a => a.Name == name);
                if (old.Name is not null)
                {
                    if (old.Digest == digest) continue;
                    if (tag != "stable") throw new IOException("Неизменяемый файл выпуска уже существует с другим хешем: " + name);
                    using var deleted = await http.DeleteAsync($"https://api.github.com/repos/{repository}/releases/assets/{old.Id}"); deleted.EnsureSuccessStatusCode();
                }
                using var content = new StreamContent(File.OpenRead(path)); content.Headers.ContentType = new("application/octet-stream");
                using var uploaded = await http.PostAsync($"https://uploads.github.com/repos/{repository}/releases/{id}/assets?name={Uri.EscapeDataString(name)}", content); uploaded.EnsureSuccessStatusCode();
                var result = JsonNode.Parse(await uploaded.Content.ReadAsStringAsync())!;
                if (result["digest"]?.GetValue<string>() != digest) throw new IOException("GitHub не подтвердил SHA-256 загруженного файла.");
            }
            if (draft)
            { using var content = new ByteArrayContent(Wire.Encode(new {draft = false, make_latest = tag == "stable" ? "true" : "false"})); content.Headers.ContentType = new("application/json"); using var published = await http.PatchAsync($"https://api.github.com/repos/{repository}/releases/{id}", content); published.EnsureSuccessStatusCode(); }
            Console.WriteLine($"Готово: https://github.com/{repository}/releases/tag/{tag}");
        }
        return;
    }
    throw new ArgumentException("Команды: keygen <приватная-папка> | configure <owner/repo> <private.pem> <ReleaseTrust.json> | build <release-config.json> | validate/publish <папка-выпусков> <owner/repo> <public.pem>");
}

sealed record BuildConfig(string Repository, string PrivateKey, string Archive, string Executable, string Output,
    int PackVersion, string AppVersion, string[] Notes, string[] LegacyArchives, string[] ApprovedJarHashes, string? PreviousPack = null,
    string? Sources = null, string? CompatibilityPack = null, string MinimumUpdater = "1.0.0", bool RequireComplete = false);
sealed record ExternalSource(string Path, string Sha256, long Size, string Url);
