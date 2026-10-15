param([string]$ResultDirectory = "$PSScriptRoot/test-results/check")
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
 function Run-Dotnet { & dotnet @args; if ($LASTEXITCODE -ne 0) {throw 'Тест или сборка завершились ошибкой.'} }
 Run-Dotnet restore Tests/Mosslight.Update.Tests.csproj --configfile NuGet.Config
 Run-Dotnet run --project Tests/Mosslight.Update.Tests.csproj --no-restore -- $ResultDirectory
 Run-Dotnet restore SelfUpdateTests/Mosslight.Update.SelfUpdateTests.csproj --configfile NuGet.Config
 Run-Dotnet publish SelfUpdateTests/Mosslight.Update.SelfUpdateTests.csproj --no-restore -c Release '-p:Version=1.0.0' '-p:DebugType=None' -o artifacts/self-old
 Run-Dotnet publish SelfUpdateTests/Mosslight.Update.SelfUpdateTests.csproj --no-restore -c Release '-p:Version=1.0.1' '-p:DebugType=None' -o artifacts/self-new
 & ./artifacts/self-old/Mosslight.Update.exe --suite "$ResultDirectory/self-update" "$PSScriptRoot/artifacts/self-new/Mosslight.Update.exe"
 if ($LASTEXITCODE -ne 0) { throw 'Не прошёл тест настоящего самообновления.' }
} finally { Pop-Location }
