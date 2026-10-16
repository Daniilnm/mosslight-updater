# Как обновить Mosslight 1.0.0, если папка ещё не выбрана

В старой версии проверка программы начинается только после сохранения папки. Пустая сборка не проходит её проверку, поэтому кнопка самообновления не появляется. Начиная с 1.1.0 это исправлено.

Без повторного скачивания EXE:

1. Закройте Mosslight.
2. Нажмите **Win+R**, вставьте команду ниже и нажмите Enter. В появившемся окне выберите папку вашего профиля, внутри которой находится `mods`, например `.minecraft/versions/4`.
3. Откройте прежний `Mosslight.Update.exe`. Нажмите **«Обновить программу»**. Сообщение о базовой сборке в старой версии пока можно игнорировать.
4. После перезапуска нажмите **«Установить сборку»**.

```powershell
powershell -NoProfile -STA -Command "Add-Type -AssemblyName System.Windows.Forms; $d=New-Object System.Windows.Forms.FolderBrowserDialog; $d.Description='Mosslight: choose your Minecraft profile folder with mods'; if($d.ShowDialog() -eq 'OK' -and (Test-Path -LiteralPath (Join-Path $d.SelectedPath 'mods')) -and -not (Test-Path -LiteralPath (Join-Path $d.SelectedPath 'server.properties'))){$p=Join-Path $env:LOCALAPPDATA 'Mosslight'; [IO.Directory]::CreateDirectory($p)|Out-Null; $f=Join-Path $p 'settings.json'; if(Test-Path -LiteralPath $f){Copy-Item -LiteralPath $f -Destination ($f+'.backup-'+[Guid]::NewGuid().ToString('N'))}; [IO.File]::WriteAllText($f,(@{instancePath=$d.SelectedPath}|ConvertTo-Json -Compress),[Text.UTF8Encoding]::new($false))}"
```

Команда работает локально: сохраняет выбранный путь в настройках Mosslight, предварительно копирует старые настройки, если они были. Она ничего не скачивает и не меняет файлы Minecraft. Администраторские права и изменение политики выполнения PowerShell не требуются.

Эквивалентный скрипт для владельца и автоматических проверок: `enable-legacy-update.ps1`. Он также поддерживает `-Folder`, `-DataDirectory` и `-NoDialog` для проверки на изолированной копии.
