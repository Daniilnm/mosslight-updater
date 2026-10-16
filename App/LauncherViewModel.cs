using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;

namespace Mosslight.Update.App;

public sealed class LauncherViewModel : INotifyPropertyChanged
{
    private readonly ReleaseClient _client;
    private readonly UpdateEngine _engine;
    private readonly TrustSettings _trust;
    private readonly string _data;
    private CancellationTokenSource? _operation;
    private UpdatePlan? _plan;
    private UpdateChannel? _channel;
    private AppRelease? _app;
    private bool _busy, _critical, _hasBackup, _closeWhenIdle;
    private string _path = "", _title = "Выберите папку сборки", _detail = "Новый профиль — установим полный комплект. Для готовой сборки скачаем только изменения.", _notes = "Проверяем доступный выпуск.";
    private double _progress;
    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? CloseRequested;
    public bool Busy => _busy;
    public bool Critical => _critical;
    public string InstancePath => string.IsNullOrEmpty(_path) ? "Папка ещё не выбрана" : _path;
    public string StatusTitle => _title;
    public string StatusDetail => _detail;
    public string ReleaseNotes => _notes;
    public string VersionLabel => _plan is null ? "НАША СБОРКА · NEOFORGE" : "ВЫПУСК v" + _plan.Release.Version;
    public string SourceLabel => string.IsNullOrEmpty(_trust.Repository) ? "GitHub ещё не подключён. Этот экземпляр подготовлен для проверки интерфейса." : "GitHub Releases · " + _trust.Repository + "\nМанифесты и самообновления проверяются закреплённым ключом выпуска.";
    public double Progress => _progress;
    public bool IsIndeterminate => _busy && _progress == 0;
    public Visibility AppUpdateVisibility => _app is not null && Version.Parse(_app.Version) > App.CurrentVersion ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ConflictVisibility => _plan?.Conflicts.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public bool QuarantineConflicts { get; set; }
    public string MainButtonLabel => _busy ? (_critical ? "Устанавливаем…" : "Подготовка…") : string.IsNullOrEmpty(_path) ? "Выбрать папку" : _plan is { Changes.Length: 0, Conflicts.Length: 0 } ? "Всё актуально" : _plan?.FirstInstall == true ? "Установить сборку" : "Обновить";
    public ICommand ChooseFolderCommand { get; }
    public ICommand CheckCommand { get; }
    public ICommand UpdateCommand { get; }
    public ICommand RollbackCommand { get; }
    public ICommand ImportArchiveCommand { get; }
    public ICommand UpdateAppCommand { get; }
    public LauncherViewModel(TrustSettings trust, string data, bool preview = false, HttpMessageHandler? handler = null, IInstanceGuard? guard = null)
    {
        _trust = trust; _data = data;
        var http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(30) }; http.DefaultRequestHeaders.UserAgent.ParseAdd("mosslight-updater/" + App.CurrentVersion.ToString(3));
        _client = new(http, new(trust)); _engine = new(_client, guard ?? new WindowsInstanceGuard());
        _engine.BeforeCommit = () => Application.Current.Dispatcher.InvokeAsync(() => { _critical = true; Refresh(); }).Task;
        ChooseFolderCommand = new Command(async () => { if (ChooseFolder()) await CheckAsync(); }, () => !_busy);
        CheckCommand = new Command(CheckAsync, () => !_busy);
        UpdateCommand = new Command(UpdateAsync, () => !_busy && !(_plan is { Changes.Length: 0, Conflicts.Length: 0 }));
        RollbackCommand = new Command(RollbackAsync, () => !_busy && _hasBackup);
        ImportArchiveCommand = new Command(ImportAsync, () => !_busy && _plan is not null);
        UpdateAppCommand = new Command(UpdateAppAsync, () => !_busy && AppUpdateVisibility == Visibility.Visible);
        if (preview)
        {
            _path = @"D:\Minecraft\Mosslight"; _title = "Предпросмотр интерфейса";
            _detail = "Пример: v20 → v21 · около 392 КиБ с каталогом · игровые файлы не изменяются.";
            _notes = "Нативный интерфейс рюкзака как по B. Вкладки только от улучшений. Shift переносит предметы между рюкзаком и сундуком.";
        }
        else LoadSettings();
    }
    public Task InitializeAsync() => !string.IsNullOrEmpty(_trust.Repository) ? CheckAsync() : Task.CompletedTask;
    public void SetPreviewState(string state)
    {
        if (state == "progress") { _busy = true; _progress = 63; _title = "Скачиваем изменённые файлы"; _detail = "Интерфейс рюкзака · 195 из 310 КиБ"; }
        if (state == "error") { _title = "GitHub сейчас недоступен"; _detail = "Проверьте интернет и нажмите «Проверить» ещё раз. Установленные файлы сохранены."; }
        if (state == "app") { _app = new(1, "1.0.1", "", "", 1); _title = "Сборка v21 актуальна"; _detail = "Доступно отдельное обновление самой программы."; }
        if (state == "rollback") { _title = "Предыдущая программа восстановлена"; _detail = "Новая версия не подтвердила запуск. Можно продолжать пользоваться этой версией."; }
        if (state == "install") { _path = @"C:\Users\Friend\AppData\Roaming\.minecraft\versions\4"; _title = "Готова первая установка сборки v23"; _detail = "Полный комплект · около 1,1 ГБ · Minecraft 1.21.1 / NeoForge 21.1.259 уже выбран в лаунчере."; _plan = new(_path, new(1, "mosslight-friends", 23, "1.21.1", "NeoForge", "1.1.0", [], [], []), [new(null, "preview", null)], [], 0, null, true); }
        Refresh();
    }
    private void Refresh()
    {
        foreach (string property in new[] { nameof(InstancePath), nameof(StatusTitle), nameof(StatusDetail), nameof(ReleaseNotes), nameof(VersionLabel), nameof(SourceLabel), nameof(Progress), nameof(IsIndeterminate), nameof(AppUpdateVisibility), nameof(ConflictVisibility), nameof(MainButtonLabel) }) PropertyChanged?.Invoke(this, new(property));
        CommandManager.InvalidateRequerySuggested();
    }
    private bool ChooseFolder()
    {
        var dialog = new OpenFolderDialog { Title = "Выберите отдельный профиль Minecraft с папкой mods (она может быть пустой)", Multiselect = false };
        if (dialog.ShowDialog() != true) return false;
        try { string root = Paths.Root(dialog.FolderName); UpdateEngine.ValidateInstance(root); _path = root; _plan = null; SaveSettings(); Refresh(); return true; }
        catch (Exception e) { ShowError(e); return false; }
    }
    private IProgress<ProgressInfo> Reporter() => new Progress<ProgressInfo>(p => { _title = p.Message; _progress = p.Percent; if (p.Critical) _critical = true; Refresh(); });
    private async Task RunAsync(Func<CancellationToken, IProgress<ProgressInfo>, Task> action)
    {
        if (_busy) return; _busy = true; _critical = false; _progress = 0; _operation = new(); Refresh();
        try { await action(_operation.Token, Reporter()); }
        catch (OperationCanceledException) { _title = "Загрузка приостановлена"; _detail = "Рабочие файлы сохранены. Повторное обновление продолжит загрузку."; }
        catch (Exception e) { ShowError(e); }
        finally { _busy = false; _critical = false; _operation.Dispose(); _operation = null; Refresh(); if (_closeWhenIdle) CloseRequested?.Invoke(); }
    }
    private void RequireSource()
    {
        if (string.IsNullOrEmpty(_trust.Repository) || string.IsNullOrEmpty(_trust.PublicKeyPem)) throw new UpdateRejectedException("Для публикации ещё нужно подключить GitHub владельца. Рабочий источник будет закреплён при сборке выпуска.");
    }
    private async Task LoadPlanAsync(CancellationToken token, IProgress<ProgressInfo> progress)
    {
        RequireSource(); _title = "Проверяем выпуск на GitHub"; _detail = "Сравниваем файлы — повторно скачиваться будет только необходимое."; Refresh();
        var application = await _client.CheckAppAsync(token); _channel = application.Channel; _app = application.App; Refresh();
        if (string.IsNullOrEmpty(_path))
        {
            _title = "Установить или обновить сборку"; _detail = "Выберите папку профиля с mods. Для первой установки подготовьте Minecraft 1.21.1 / NeoForge 21.1.259 в своём лаунчере.";
            _notes = "Первая установка полного комплекта. Продолжение оборванной загрузки. Проверка всех файлов перед установкой."; Refresh(); return;
        }
        _plan = null;
        var release = await _client.CheckPackAsync(_channel, token);
        if (Version.Parse(release.MinimumUpdater) > App.CurrentVersion) { Refresh(); throw new UpdateRejectedException("Сначала обновите саму программу: этот выпуск использует новый формат."); }
        _notes = string.Join(" ", release.Notes.Take(3));
        _plan = await Task.Run(() => _engine.AnalyzeAsync(_path, release, progress, token), token);
        _hasBackup = UpdateEngine.LatestBackup(_path) is not null;
        _title = _plan.Changes.Length == 0 ? "Установлена актуальная сборка v" + _plan.Release.Version : _plan.FirstInstall ? "Готова первая установка сборки v" + _plan.Release.Version : "Доступно обновление до v" + _plan.Release.Version;
        _detail = _plan.Changes.Length == 0 ? "Файлы и необходимые настройки проверены. Можно открыть свой лаунчер." :
            _plan.FirstInstall ? $"Полный комплект · загрузка до {Size(_plan.DownloadBytes)}. Нужны Minecraft 1.21.1 / NeoForge 21.1.259 в лаунчере." : $"Изменений: {_plan.Changes.Length} · загрузка до {Size(_plan.DownloadBytes)} · личные настройки сохранятся.";
        if (_plan.Conflicts.Length > 0) _detail = "Конфликтующие моды: " + string.Join(", ", _plan.Conflicts.Select(Path.GetFileName)) + ". Их можно перенести в бэкап.";
        _progress = 0; Refresh();
    }
    private async Task CheckAsync()
    {
        await RunAsync(LoadPlanAsync);
    }
    private async Task UpdateAsync()
    {
        if (string.IsNullOrEmpty(_path) && !ChooseFolder()) return;
        await RunAsync(async (token, progress) =>
        {
            await LoadPlanAsync(token, progress); if (_plan is null) return;
            await Task.Run(() => _engine.ApplyAsync(_plan, QuarantineConflicts, progress, token), token);
            bool installed = _plan.FirstInstall;
            _critical = false; _plan = _plan with { Changes = [], Conflicts = [], FirstInstall = false }; _hasBackup = UpdateEngine.LatestBackup(_path) is not null; _progress = 100;
            _title = (installed ? "Сборка установлена — v" : "Сборка обновлена до v") + _plan.Release.Version; _detail = "Готово. Теперь можно открыть CurseForge или TLauncher.";
        });
    }
    private async Task RollbackAsync() => await RunAsync(async (token, progress) =>
    {
        await Task.Run(() => _engine.RollbackAsync(_path, progress, token), token);
        _critical = false; _plan = null; _hasBackup = UpdateEngine.LatestBackup(_path) is not null;
        _title = "Предыдущая сборка восстановлена"; _detail = "При необходимости актуальную версию можно установить снова.";
    });
    private async Task ImportAsync()
    {
        if (_plan is null) return; var dialog = new OpenFileDialog { Title = "Полный архив сборки для восстановления", Filter = "Архив сборки (*.zip)|*.zip", Multiselect = false };
        if (dialog.ShowDialog() != true) return;
        await RunAsync(async (token, progress) => { await Task.Run(() => _engine.ImportArchiveAsync(_path, dialog.FileName, _plan.Release, progress, token), token); _title = "Файлы архива проверены"; _detail = "Теперь нажмите «Обновить». Будут использованы только файлы с правильными хешами."; });
    }
    private async Task UpdateAppAsync() => await RunAsync(async (token, progress) =>
    {
        if (_channel is null) return; SaveSettings();
        string ticket = await SelfUpdate.PrepareAsync(_client, _channel.AppUrl, Environment.ProcessPath!, Path.Combine(_data, "self-update"), App.CurrentVersion, progress, token);
        _critical = true; _title = "Перезапускаем обновлятор"; _detail = "Выбранная сборка и настройки будут сохранены."; Refresh();
        await SelfUpdate.StartHelperAsync(ticket); Application.Current.Shutdown();
    });
    public void CancelAndClose() { _closeWhenIdle = true; _operation?.Cancel(); }
    private void ShowError(Exception e)
    {
        _title = e is UpdateRejectedException ? "Нужен один шаг" : "Не удалось завершить обновление";
        _detail = e is HttpRequestException ? "Источник загрузки сейчас недоступен. Проверьте интернет и нажмите «Проверить» ещё раз. Установленные файлы сохранены." : e.Message;
        _progress = 0; Refresh();
    }
    private void LoadSettings()
    {
        try { string file = Path.Combine(_data, "settings.json"); if (File.Exists(file)) _path = Wire.Decode<UserSettings>(File.ReadAllBytes(file)).InstancePath; }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException) { _path = ""; }
    }
    private void SaveSettings() { Directory.CreateDirectory(_data); Paths.WriteAtomic(Path.Combine(_data, "settings.json"), Wire.Encode(new UserSettings(_path))); }
    public static string Size(long bytes) => bytes < 1024 ? bytes + " Б" : bytes < 1048576 ? (bytes / 1024d).ToString("0.#") + " КиБ" : (bytes / 1048576d).ToString("0.##") + " МиБ";
    private sealed record UserSettings(string InstancePath);
    private sealed class Command(Func<Task> action, Func<bool> enabled) : ICommand
    {
        public bool CanExecute(object? parameter) => enabled();
        public async void Execute(object? parameter) { if (enabled()) await action(); }
        public event EventHandler? CanExecuteChanged { add => CommandManager.RequerySuggested += value; remove => CommandManager.RequerySuggested -= value; }
    }
}
