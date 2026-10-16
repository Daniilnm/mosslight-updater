param([string]$Folder = '', [string]$DataDirectory = '', [switch]$NoDialog)
$ErrorActionPreference = 'Stop'
if (-not $Folder) {
 if ($NoDialog) { throw 'Укажите -Folder.' }
 Add-Type -AssemblyName System.Windows.Forms
 $picker = [System.Windows.Forms.FolderBrowserDialog]::new()
 $picker.Description = 'Выберите папку созданного профиля Minecraft с папкой mods'
 if ($picker.ShowDialog() -ne [System.Windows.Forms.DialogResult]::OK) { return }
 $Folder = $picker.SelectedPath
 $picker.Dispose()
}
$Folder = (Resolve-Path -LiteralPath $Folder).Path
if (-not (Test-Path -LiteralPath (Join-Path $Folder 'mods') -PathType Container) -or (Test-Path -LiteralPath (Join-Path $Folder 'server.properties'))) {
 throw 'Нужна клиентская папка отдельного профиля с mods.'
}
if (-not $DataDirectory) {
 $DataDirectory = if ($env:MOSSLIGHT_UPDATE_DATA) { $env:MOSSLIGHT_UPDATE_DATA } else { Join-Path $env:LOCALAPPDATA 'Mosslight' }
}
[IO.Directory]::CreateDirectory($DataDirectory) | Out-Null
$settingsPath = Join-Path $DataDirectory 'settings.json'
if (Test-Path -LiteralPath $settingsPath) { Copy-Item -LiteralPath $settingsPath -Destination ($settingsPath + '.backup-' + [Guid]::NewGuid().ToString('N')) }
$temporary = $settingsPath + '.new-' + [Guid]::NewGuid().ToString('N')
[IO.File]::WriteAllText($temporary, (@{instancePath=$Folder} | ConvertTo-Json -Compress), [Text.UTF8Encoding]::new($false))
Move-Item -LiteralPath $temporary -Destination $settingsPath -Force
Write-Host 'Готово. Откройте прежний Mosslight.Update.exe и нажмите «Обновить программу». Сообщение о базовой сборке в старой версии пока можно игнорировать.'
