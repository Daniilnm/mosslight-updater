using System.IO.Compression;

namespace Mosslight.Update;

public sealed class UpdateEngine(ReleaseClient client, IInstanceGuard guard)
{
    public Func<int, Task>? AfterMutationForTest { get; set; }
    public Func<Task>? BeforeCommit { get; set; }
    private const string StatePath = ".mosslight/state.json";
    public static string Cache(string root) => Paths.Resolve(root, ".mosslight/cache");
    public static void ValidateInstance(string root)
    {
        Paths.Root(root);
        if (!Directory.Exists(Path.Combine(root, "mods")) || File.Exists(Path.Combine(root, "server.properties")))
            throw new UpdateRejectedException("Выберите клиентскую папку сборки с папкой mods. Сервер обновлять этой программой нельзя.");
        string[] mods = Directory.GetFiles(Path.Combine(root, "mods"), "*.jar").Select(Path.GetFileName).OfType<string>().ToArray();
        bool anchors = mods.Any(n => n.StartsWith("kubejs-", StringComparison.OrdinalIgnoreCase))
            && mods.Any(n => n.StartsWith("sophisticatedbackpacks-", StringComparison.OrdinalIgnoreCase));
        // An interrupted replacement may have removed an anchor jar before moving its successor.
        string transactions = Paths.Resolve(root, ".mosslight/transactions");
        bool interrupted = !anchors && Directory.Exists(transactions) && Directory.GetDirectories(transactions).Any(d =>
        { Paths.RejectLinks(d, root); string p = Path.Combine(d, "journal.json"); return File.Exists(p) && ReadJournal(p).Phase is "Applying" or "RollingBack"; });
        if (!anchors && !interrupted)
            throw new UpdateRejectedException("В этой папке не найдена базовая сборка Minecraft. Сначала нужна первая установка.");
    }
    private static InstalledState? State(string root)
    {
        string path = Paths.Resolve(root, StatePath);
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 8 * 1024 * 1024) throw new UpdateRejectedException("Повреждён учёт установленной сборки.");
        var state = Wire.Decode<InstalledState>(File.ReadAllBytes(path));
        if (state.Schema != 1 || state.PackId != "mosslight-friends") throw new UpdateRejectedException("Это состояние другой сборки.");
        foreach (var f in state.Files) Paths.RequireManaged(f.Path);
        return state;
    }
    private static FileStream Lock(string root)
    {
        string path = Paths.Resolve(root, ".mosslight/update.lock"); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new UpdateRejectedException("Для этой сборки уже работает другое окно обновления."); }
    }
    public async Task<UpdatePlan> AnalyzeAsync(string folder, PackRelease release, IProgress<ProgressInfo>? progress, CancellationToken token)
    {
        string root = Paths.Root(folder); ValidateInstance(root); client.Validate(release);
        await guard.EnsureClosedAsync(root, token);
        using var mutex = Lock(root);
        await RecoverUnderLockAsync(root, progress, token);
        var previous = State(root); var changes = new List<Change>();
        var desired = release.Files.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase); int index = 0;
        foreach (var f in release.Files)
        {
            token.ThrowIfCancellationRequested(); string path = Paths.Resolve(root, f.Path); string? hash = null;
            if (File.Exists(path)) hash = await Hashes.FileAsync(path, token);
            if (hash is not null && f.Policy == FilePolicy.Seed) { }
            else if (hash is not null && ConfigPatches.IsPatch(f.Policy))
            {
                byte[] before = await File.ReadAllBytesAsync(path, token), after = ConfigPatches.Apply(f.Policy, before);
                if (!before.AsSpan().SequenceEqual(after)) changes.Add(new(f, f.Path, hash, after));
            }
            else if (hash != f.Sha256) changes.Add(new(f, f.Path, hash));
            progress?.Report(new("Проверяем файлы сборки", ++index, release.Files.Length));
        }
        var oldPaths = (previous?.Files.Select(f => f.Path) ?? []).Concat(release.LegacyPaths).Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (string old in oldPaths)
        {
            Paths.RequireManaged(old);
            if (desired.ContainsKey(old) || ConfigPatches.PolicyFor(old) != FilePolicy.Managed) continue;
            string path = Paths.Resolve(root, old);
            if (File.Exists(path)) changes.Add(new(null, old, await Hashes.FileAsync(path, token)));
        }
        var known = desired.Keys.Concat(changes.Select(c => c.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var modIds = release.Files.SelectMany(f => f.ModIds ?? []).ToHashSet(StringComparer.Ordinal);
        var conflicts = new List<string>(); int extras = 0;
        foreach (string jar in Directory.EnumerateFiles(Path.Combine(root, "mods"), "*.jar"))
        {
            string relative = "mods/" + Path.GetFileName(jar); if (known.Contains(relative)) continue;
            Paths.Resolve(root, relative); extras++;
            if (ModMetadata.ReadIds(jar).Any(modIds.Contains)) conflicts.Add(relative);
        }
        return new(root, release, changes.ToArray(), conflicts.ToArray(), extras, previous);
    }
    public async Task ApplyAsync(UpdatePlan plan, bool quarantineConflicts, IProgress<ProgressInfo>? progress, CancellationToken token)
    {
        string root = Paths.Root(plan.Root); ValidateInstance(root); client.Validate(plan.Release);
        using var mutex = Lock(root); await guard.EnsureClosedAsync(root, token);
        await RecoverUnderLockAsync(root, progress, token);
        if (plan.Conflicts.Length > 0 && !quarantineConflicts) throw new UpdateRejectedException("Найдены конфликтующие дополнительные моды. Отметьте перенос в резервную копию.");
        var changes = plan.Changes.ToList();
        foreach (string conflict in plan.Conflicts)
            changes.Add(new(null, conflict, await Hashes.FileAsync(Paths.Resolve(root, conflict), token)));
        if (changes.Count == 0 && State(root)?.Version == plan.Release.Version) return;
        string id = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8];
        string transaction = Paths.Resolve(root, ".mosslight/transactions/" + id); Directory.CreateDirectory(transaction);
        string journalPath = Path.Combine(transaction, "journal.json");
        var entries = new List<JournalEntry>();
        long required = changes.Sum(c => (c.File?.Size ?? 0) + (c.BeforeHash is null ? 0 : new FileInfo(Paths.Resolve(root, c.Path)).Length)) + 32 * 1024 * 1024;
        if (new DriveInfo(Path.GetPathRoot(root)!).AvailableFreeSpace < required * 2) throw new UpdateRejectedException("Не хватает места для загрузки, временных файлов и бэкапа.");
        foreach (var change in changes)
        {
            token.ThrowIfCancellationRequested(); Paths.RequireManaged(change.Path);
            string target = Paths.Resolve(root, change.Path), backup = Paths.Resolve(transaction, "backup/" + change.Path);
            string? current = File.Exists(target) ? await Hashes.FileAsync(target, token) : null;
            if (current != change.BeforeHash) throw new UpdateRejectedException("Файлы изменились после проверки. Нажмите «Проверить» ещё раз.");
            if (current is not null) { Directory.CreateDirectory(Path.GetDirectoryName(backup)!); File.Copy(target, backup, true); if (await Hashes.FileAsync(backup, token) != current) throw new IOException("Не удалось проверить резервную копию."); }
            string? afterHash = null;
            if (change.File is { } file)
            {
                string staging = Paths.Resolve(transaction, "staged/" + change.Path); Directory.CreateDirectory(Path.GetDirectoryName(staging)!);
                byte[]? bytes = change.PreparedBytes;
                if (bytes is null)
                {
                    string cached = await client.GetFileAsync(file, Cache(root), progress, token);
                    if (ConfigPatches.IsPatch(file.Policy)) bytes = ConfigPatches.Apply(file.Policy, await File.ReadAllBytesAsync(cached, token));
                    else File.Copy(cached, staging, true);
                }
                if (bytes is not null) await File.WriteAllBytesAsync(staging, bytes, token);
                afterHash = await Hashes.FileAsync(staging, token);
                if (file.Policy == FilePolicy.Managed && afterHash != file.Sha256) throw new IOException("Не совпал хеш подготовленного файла.");
            }
            entries.Add(new(change.Path, current, afterHash, change.File?.Policy ?? FilePolicy.Managed));
        }
        string? previousState = File.Exists(Paths.Resolve(root, StatePath)) ? Convert.ToBase64String(await File.ReadAllBytesAsync(Paths.Resolve(root, StatePath), token)) : null;
        var journal = new UpdateJournal(1, id, "Prepared", plan.Release.Version, previousState, entries.ToArray(), DateTimeOffset.UtcNow);
        SaveJournal(journalPath, journal);
        await guard.EnsureClosedAsync(root, token); token.ThrowIfCancellationRequested();
        foreach (var e in entries)
        {
            string path = Paths.Resolve(root, e.Path); string? current = File.Exists(path) ? await Hashes.FileAsync(path, token) : null;
            if (current != e.BeforeHash) throw new UpdateRejectedException("Сборка изменилась во время загрузки. Повторите проверку.");
        }
        if (BeforeCommit is not null) await BeforeCommit();
        token.ThrowIfCancellationRequested();
        journal = journal with { Phase = "Applying" }; SaveJournal(journalPath, journal);
        try
        {
            for (int i = 0; i < journal.Entries.Length; i++)
            {
                var e = journal.Entries[i]; progress?.Report(new("Устанавливаем файлы", i, journal.Entries.Length, true));
                string target = Paths.Resolve(root, e.Path);
                if (e.AfterHash is null) File.Delete(target);
                else { Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Move(Paths.Resolve(transaction, "staged/" + e.Path), target, true); }
                journal.Entries[i] = e with { Applied = true }; SaveJournal(journalPath, journal);
                if (AfterMutationForTest is not null) await AfterMutationForTest(i);
            }
            foreach (var file in plan.Release.Files)
            {
                string path = Paths.Resolve(root, file.Path);
                if (!File.Exists(path)) throw new IOException("Не найден итоговый файл: " + file.Path);
                if (file.Policy == FilePolicy.Managed && await Hashes.FileAsync(path) != file.Sha256) throw new IOException("Повреждён итоговый файл: " + file.Path);
                if (ConfigPatches.IsPatch(file.Policy)) { byte[] bytes = await File.ReadAllBytesAsync(path); if (!ConfigPatches.Apply(file.Policy, bytes).AsSpan().SequenceEqual(bytes)) throw new IOException("Не применена настройка: " + file.Path); }
            }
            var state = new InstalledState(1, "mosslight-friends", plan.Release.Version, plan.Release.Files.Select(f => new InstalledFile(f.Path, f.Sha256, f.Policy)).ToArray());
            Paths.WriteAtomic(Paths.Resolve(root, StatePath), Wire.Encode(state));
            SaveJournal(journalPath, journal with { Phase = "Committed" });
            progress?.Report(new("Готово — сборка v" + plan.Release.Version, 1, 1));
        }
        catch
        {
            await RestoreAsync(root, transaction, journal, false, progress); throw;
        }
        Prune(root);
    }
    private static void SaveJournal(string path, UpdateJournal journal) => Paths.WriteAtomic(path, Wire.Encode(journal));
    public async Task RecoverAsync(string folder, IProgress<ProgressInfo>? progress, CancellationToken token)
    { string root = Paths.Root(folder); ValidateInstance(root); using var mutex = Lock(root); await guard.EnsureClosedAsync(root, token); await RecoverUnderLockAsync(root, progress, token); }
    private async Task RecoverUnderLockAsync(string root, IProgress<ProgressInfo>? progress, CancellationToken token)
    {
        string home = Paths.Resolve(root, ".mosslight/transactions"); if (!Directory.Exists(home)) return;
        foreach (string directory in Directory.GetDirectories(home))
        {
            token.ThrowIfCancellationRequested(); Paths.RejectLinks(directory, root); string path = Path.Combine(directory, "journal.json");
            if (!File.Exists(path)) continue; var journal = ReadJournal(path);
            if (journal.Phase is "Applying" or "RollingBack")
            {
                if (BeforeCommit is not null) await BeforeCommit();
                await RestoreAsync(root, directory, journal, false, progress);
            }
        }
    }
    private static UpdateJournal ReadJournal(string path)
    {
        Paths.RejectLinks(path); if (new FileInfo(path).Length > 8 * 1024 * 1024) throw new IOException("Повреждён журнал обновления.");
        var journal = Wire.Decode<UpdateJournal>(File.ReadAllBytes(path));
        if (journal.Schema != 1 || journal.Entries.Length > 5000 || journal.Entries.Select(e => e.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != journal.Entries.Length) throw new IOException("Неизвестный журнал обновления.");
        if (journal.PreviousState is not null) _ = Wire.Decode<InstalledState>(Convert.FromBase64String(journal.PreviousState));
        foreach (var e in journal.Entries) { Paths.RequireManaged(e.Path); if (e.BeforeHash is not null && !Hashes.IsSha256(e.BeforeHash) || e.AfterHash is not null && !Hashes.IsSha256(e.AfterHash)) throw new IOException("Неверный хеш в журнале."); }
        return journal;
    }
    public static string? LatestBackup(string root)
    {
        string home = Paths.Resolve(root, ".mosslight/transactions"); if (!Directory.Exists(home)) return null;
        return Directory.GetDirectories(home).Where(d => { Paths.RejectLinks(d, root); return File.Exists(Path.Combine(d, "journal.json")); })
            .Select(d => (Directory: d, Journal: ReadJournal(Path.Combine(d, "journal.json"))))
            .Where(p => p.Journal.Phase == "Committed").OrderByDescending(p => p.Journal.Created).Select(p => p.Directory).FirstOrDefault();
    }
    public async Task RollbackAsync(string folder, IProgress<ProgressInfo>? progress, CancellationToken token)
    {
        string root = Paths.Root(folder); ValidateInstance(root); using var mutex = Lock(root); await guard.EnsureClosedAsync(root, token);
        await RecoverUnderLockAsync(root, progress, token);
        string transaction = LatestBackup(root) ?? throw new UpdateRejectedException("Пока нет резервной копии для отката.");
        if (BeforeCommit is not null) await BeforeCommit();
        await RestoreAsync(root, transaction, ReadJournal(Path.Combine(transaction, "journal.json")), true, progress);
    }
    private static async Task RestoreAsync(string root, string transaction, UpdateJournal journal, bool manual, IProgress<ProgressInfo>? progress)
    {
        string journalPath = Path.Combine(transaction, "journal.json"); journal = journal with { Phase = "RollingBack" }; SaveJournal(journalPath, journal);
        bool userChanges = journal.HasUserChanges;
        foreach (var e in journal.Entries.Reverse())
        {
            progress?.Report(new("Восстанавливаем предыдущую сборку", Critical: true));
            string target = Paths.Resolve(root, e.Path), backup = Paths.Resolve(transaction, "backup/" + e.Path);
            string? current = File.Exists(target) ? await Hashes.FileAsync(target) : null;
            if (current == e.BeforeHash) continue;
            if (e.BeforeHash is not null && (!File.Exists(backup) || await Hashes.FileAsync(backup) != e.BeforeHash)) throw new IOException("Резервная копия не прошла проверку: " + e.Path);
            if (current is not null && current != e.AfterHash)
            {
                string conflict = Paths.Resolve(transaction, "user-changes/" + Guid.NewGuid().ToString("N") + "/" + e.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(conflict)!); File.Copy(target, conflict); userChanges = true;
                // A newly seeded/patched personal config may have gained personal settings since installation.
                if (manual && e.BeforeHash is null && e.Policy != FilePolicy.Managed) continue;
                if (manual && e.BeforeHash is not null && ConfigPatches.IsPatch(e.Policy))
                { Paths.WriteAtomic(target, ConfigPatches.Restore(e.Policy, await File.ReadAllBytesAsync(target), await File.ReadAllBytesAsync(backup))); continue; }
            }
            if (e.BeforeHash is null) { if (File.Exists(target)) File.Delete(target); }
            else Paths.WriteAtomic(target, await File.ReadAllBytesAsync(backup));
        }
        string statePath = Paths.Resolve(root, StatePath);
        if (journal.PreviousState is null) { if (File.Exists(statePath)) File.Delete(statePath); }
        else Paths.WriteAtomic(statePath, Convert.FromBase64String(journal.PreviousState));
        SaveJournal(journalPath, journal with { Phase = "RolledBack", HasUserChanges = userChanges });
        progress?.Report(new("Предыдущая сборка восстановлена", 1, 1));
    }
    private static void Prune(string root)
    {
        string home = Paths.Resolve(root, ".mosslight/transactions");
        var backups = Directory.GetDirectories(home).Where(d => File.Exists(Path.Combine(d, "journal.json")))
            .Select(d => (Path: d, Journal: ReadJournal(Path.Combine(d, "journal.json"))))
            .Where(p => (p.Journal.Phase is "Committed" or "RolledBack") && !p.Journal.HasUserChanges).OrderByDescending(p => p.Journal.Created).Skip(3);
        foreach (var backup in backups) Paths.DeleteOwnedTree(home, backup.Path);
    }
    public async Task ImportArchiveAsync(string folder, string archive, PackRelease release, IProgress<ProgressInfo>? progress, CancellationToken token)
    {
        string root = Paths.Root(folder); ValidateInstance(root); client.Validate(release); await guard.EnsureClosedAsync(root, token); using var mutex = Lock(root);
        string cache = Cache(root); Directory.CreateDirectory(cache); using var zip = ZipFile.OpenRead(archive); int count = 0;
        foreach (var f in release.Files)
        {
            token.ThrowIfCancellationRequested(); var entry = zip.GetEntry(f.Path);
            if (entry is null || entry.Length != f.Size) continue;
            string destination = Path.Combine(cache, f.Sha256 + ".bin"); Paths.RejectLinks(destination, cache);
            await using (var source = entry.Open()) await using (var target = File.Create(destination))
            {
                byte[] buffer = new byte[131072]; long size = 0; int n;
                while ((n = await source.ReadAsync(buffer, token)) > 0) { size += n; if (size > f.Size) throw new UpdateRejectedException("Файл архива превышает ожидаемый размер."); await target.WriteAsync(buffer.AsMemory(0, n), token); }
            }
            if (await Hashes.FileAsync(destination, token) != f.Sha256) { File.Delete(destination); continue; }
            count++; progress?.Report(new("Подготавливаем восстановление из архива", count, release.Files.Length));
        }
        if (count == 0) throw new UpdateRejectedException("В архиве не найдены файлы этой сборки. Установка не изменена.");
    }
}
