param(
 [Parameter(Mandatory)][string]$Repository,
 [Parameter(Mandatory)][string]$PrivateKey,
 [Parameter(Mandatory)][string]$Archive,
 [Parameter(Mandatory)][string]$LegacyArchiveFolder,
 [string]$AppVersion = '1.0.0', [int]$PackVersion = 21,
 [string]$Output = "$PSScriptRoot/artifacts/release", [string]$PreviousPack = '',
 [string[]]$Notes = @('Рюкзак открывается штатным интерфейсом как по B.','Дополнительные функции появляются только от установленных улучшений.','Shift переносит предметы между открытым рюкзаком и сундуком.')
)
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
 function Run-Dotnet { & dotnet @args; if ($LASTEXITCODE -ne 0) { throw 'Сборка остановлена: dotnet завершился с ошибкой.' } }
 Run-Dotnet restore ReleaseTool/Mosslight.Update.ReleaseTool.csproj --configfile NuGet.Config
 Run-Dotnet run --project ReleaseTool/Mosslight.Update.ReleaseTool.csproj --no-restore -- configure $Repository $PrivateKey App/ReleaseTrust.json
 Run-Dotnet restore App/Mosslight.Update.App.csproj --configfile NuGet.Config
 Run-Dotnet publish App/Mosslight.Update.App.csproj --no-restore -c Release "-p:Version=$AppVersion" '-p:DebugType=None' -o artifacts/app
 $legacy = @(Get-ChildItem -LiteralPath $LegacyArchiveFolder -Filter 'BMC5-mods-*-v*.zip' | Select-Object -ExpandProperty FullName)
 $config = @{
  repository=$Repository; privateKey=[IO.Path]::GetFullPath($PrivateKey); archive=[IO.Path]::GetFullPath($Archive)
  executable="$PSScriptRoot/artifacts/app/Mosslight.Update.exe"; output=[IO.Path]::GetFullPath($Output)
  packVersion=$PackVersion; appVersion=$AppVersion
  notes=$Notes
  legacyArchives=$legacy; approvedJarHashes=@(Get-Content approved-jars.json -Raw | ConvertFrom-Json)
  previousPack=$(if ($PreviousPack) {[IO.Path]::GetFullPath($PreviousPack)} else {$null})
 }
 New-Item -ItemType Directory -Force "$PSScriptRoot/.private" | Out-Null
 $config | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath "$PSScriptRoot/.private/release-build.json" -Encoding utf8
 # Avoid a UTF-8 BOM so the same strict JSON reader works on Windows PowerShell and PowerShell 7.
 [IO.File]::WriteAllText("$PSScriptRoot/.private/release-build.json", ($config | ConvertTo-Json -Depth 10), [Text.UTF8Encoding]::new($false))
 Run-Dotnet run --project ReleaseTool/Mosslight.Update.ReleaseTool.csproj --no-restore -- build "$PSScriptRoot/.private/release-build.json"
 Write-Host "Готово локально: $Output. Для публикации используйте ReleaseTool publish."
} finally { Pop-Location }
