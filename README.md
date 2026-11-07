# motw-preview-fix

Два независимых инструмента для Windows 10/11:

1. **MOTW auto-unblock (основное, стабильное)** — служба, которая автоматически снимает
   метку «Файл получен из Интернета» (`Zone.Identifier`, он же Mark of the Web) с новых
   файлов и с целых дисков. Именно из-за этой метки Проводник и просмотрщики отказываются
   показывать PDF/DOCX/XLSX/PPTX, пока не поставишь галку «Разблокировать».
2. **Preview handler pack (экспериментальное, по умолчанию НЕ ставится)** — собственный
   обработчик панели предпросмотра для форматов, которых в Windows 10 нет «из коробки»:
   `.png .jpg .jpeg .tif .tiff .bmp .gif .psd .psb .svg`, плюс явная привязка текстовых
   форматов к **штатному** TXT-обработчику Windows.

> ⚠️ **Про обработчик предпросмотра.** Это managed (CLR) COM-обработчик, который
> Проводник загружает в `prevhost.exe`. На практике выяснилось: на некоторых машинах он
> **подвешивает оболочку** — `explorer.exe` перезапускается, панель предпросмотра
> замирает почти на любом файле. Поэтому он вынесен за флаг `-WithPreviewHandler` и
> не устанавливается по умолчанию. Ставьте его только если готовы откатить
> `.\uninstall.ps1 -Purge`. Часть 1 (MOTW) от него не зависит и работает без него.
> Включение/выключение частей независимо: `-WithPreviewHandler`, `-SkipMotw`.

---

## 1. MOTW: почему это нужно

Windows помечает скачанные и распакованные из ZIP файлы альтернативным потоком NTFS
`Zone.Identifier` (`ZoneId=3`). Из-за этой метки:

- в свойствах файла появляется галка «Разблокировать»;
- часть программ (Acrobat, Office, некоторые просмотрщики) отказывается показывать
  содержимое, пока галку не поставить;
- ZIP-распаковщик Проводника переносит метку во **все** файлы архива.

### Задача планировщика `PreviewMotw-Unblock`

- триггер: вход в систему (задержка 1 мин), скрытый запуск, повышенные права;
- при старте — полный проход по путям через `cmd /c dir /s /r` (~5 с на 100k файлов);
- далее — `FileSystemWatcher` на каждый путь: новый файл разблокируется за 1–3 с;
- каждые `SweepMinutes` (по умолчанию 30) — контрольный проход: `FileSystemWatcher`
  не сообщает про запись только в альтернативный поток;
- файлы с атрибутом «только чтение» обрабатываются со снятием/восстановлением атрибута
  (`Unblock-File` на RO-файлах молча не работает);
- лог: `%LOCALAPPDATA%\PreviewMotw\unblock.log`.

### Настройки Windows, которые меняет `install.ps1`

| Ключ | Значение | Смысл |
|---|---|---|
| `HKCU\…\Policies\Attachments\SaveZoneInformation` | 1 | не сохранять сведения о зоне у вложений |
| `HKCU\…\Policies\Attachments\HideZoneInfoOnProperties` | 1 | убрать из свойств галку «Разблокировать» |
| `HKLM\…\Policies\Attachments\ScanWithAntiVirus` | 1 | не проверять вложения антивирусом |
| `HKCU\…\AppHost\EnableWebContentEvaluation` | 0 | SmartScreen для скачанных файлов выключен |

Снять их: `.\uninstall.ps1 -Full`.

## 2. Preview handler pack (экспериментально)

| Расширения | Как рендерится | Зависимости |
|---|---|---|
| `.png .jpg .jpeg .jpe .tif .tiff .bmp .gif` | GDI+ (`Image.FromStream`), уменьшение до 4096 px по стороне | нет |
| `.psd .psb` | полный композит PSD (big-endian, PackBits RLE / raw, RGB и CMYK); fallback — встроенная JPEG-миниатюра | нет |
| `.svg .svgz` | элемент WebBrowser (движок IE11), файлы до 25 МБ | IE11 |
| `.txt .json .md .markdown .sh .bash .log .yml .yaml .xml .css .js .cs .py .ini .cfg .conf .ts .sql .ps1 .bat .cmd` | **штатный** Windows TXT Previewer (эта часть безопасна) | нет |

`.ai .eps .ps` намеренно **не** поддерживаются: рендер через Ghostscript запускал внешний
процесс на каждый показ и вешал Проводник (см. историю в коммитах).

## Требования

- Windows 10 (19041+) или Windows 11, x64;
- .NET Framework 4.x — `csc.exe` и `regasm.exe` уже в системе, Visual Studio не нужен;
- PowerShell 5.1;
- права администратора для установки (запись в `HKLM`, создание задачи планировщика);
- скрипты не подписаны → запуск с `-ExecutionPolicy Bypass` (или `Unblock-File` на `.ps1`).

## Установка

Из **elevated PowerShell** (проще всего):

```powershell
git clone https://github.com/region39/motw-preview-fix.git
cd motw-preview-fix

Set-ExecutionPolicy -Scope Process Bypass -Force

# MOTW в Downloads (по умолчанию) — стабильная часть
.\install.ps1

# MOTW с явным списком дисков/папок
.\install.ps1 -Roots 'D:\', 'E:\Media', "$env:USERPROFILE\Downloads"

# плюс экспериментальный обработчик предпросмотра (осознанно!)
.\install.ps1 -Roots 'D:\' -WithPreviewHandler

# только автоснятие метки, без обработчика (эквивалент установки по умолчанию)
.\install.ps1 -SkipMotw:$false -Roots 'D:\'
```

Из `cmd.exe` (кавычки вокруг `-Roots` — отдельная ловушка, поэтому проще через `-Command`):

```cmd
powershell -NoProfile -ExecutionPolicy Bypass -Command "& '.\install.ps1' -Roots 'D:\','E:\Media'"
```

Что делает `install.ps1`:

1. ослабляет Attachment Manager (таблица выше);
2. пишет `roots.json` в `%LOCALAPPDATA%\PreviewMotw` и создаёт задачу планировщика
   `PreviewMotw-Unblock` (вход в систему + watcher);
3. только с `-WithPreviewHandler`: компилирует `src\PreviewMotwHandlers.cs`, делает
   `regasm /codebase`, прописывает ассоциации в
   `HKLM\Software\Classes\SystemFileAssociations\.<ext>\ShellEx\{8895B1C6-…}`;
4. сбрасывает кэш ассоциаций `SHChangeNotify(SHCNE_ASSOCCHANGED)` и печатает
   фактическое разрешение обработчиков через `AssocQueryString`.

## Проверка

MOTW: создать файл с `Zone.Identifier` в отслеживаемой папке и подождать 2–3 секунды.

```powershell
$f = "$env:USERPROFILE\Downloads\motw-test.txt"
'x' | Set-Content $f
"[ZoneTransfer]`r`nZoneId=3" | Set-Content $f -Stream Zone.Identifier
Start-Sleep 5
Get-Item $f -Stream Zone.Identifier -ErrorAction SilentlyContinue   # должно быть пусто
```

Предпросмотр: выделить файл в Проводнике и нажать `Alt+P`. Программно:

```powershell
Add-Type -TypeDefinition @'
using System; using System.Runtime.InteropServices; using System.Text;
public static class A {
  [DllImport("shlwapi.dll", CharSet=CharSet.Unicode)]
  public static extern int AssocQueryString(int f, int s, string a, string e, StringBuilder b, ref int l);
  public static string H(string ext) { int l=512; var b=new StringBuilder(l);
    AssocQueryString(0,16,ext,"{8895B1C6-B41F-4C1C-A562-0D564250836F}",b,ref l); return b.ToString(); }
}
'@
[A]::H('.pdf'); [A]::H('.psd'); [A]::H('.md')   # pdf/word/txt — штатные; psd — только если ставили -WithPreviewHandler
```

Проверка самого обработчика без Проводника (рендер в скрытое окно + скриншот):

```powershell
cd src
& "$env:SystemRoot\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /target:exe /platform:x64 `
  /out:TestHarness.exe /r:System.dll /r:System.Core.dll /r:System.Drawing.dll `
  /r:System.Windows.Forms.dll /r:"$env:LOCALAPPDATA\PreviewMotw\PreviewMotwHandlers.dll" TestHarness.cs
.\TestHarness.exe "C:\path\to\file.psd" "$env:TEMP\out.png"
# ожидаемый вывод: diag: image 3600x2600 ext=.psd
```

## Настройка

- Пути и интервал задаются при установке (`-Roots`, `-SweepMinutes`). Через командную
  строку задачи они **не** передаются — установщик пишет `roots.json`, скрипт читает
  оттуда (кавычки вокруг `"D:\"` в `powershell.exe -File` — известная ловушка: `\"`
  считается экранированной кавычкой).

- Добавить/убрать путь позже:

```powershell
# 1) переустановить задачу с новым списком
.\install.ps1 -Roots 'D:\', 'E:\Media', "$env:USERPROFILE\Downloads"

# 2) или поправить roots.json и перезапустить задачу
@('D:\', 'E:\Media') | ConvertTo-Json -Compress |
    Set-Content "$env:LOCALAPPDATA\PreviewMotw\roots.json" -Encoding UTF8
Stop-ScheduledTask  PreviewMotw-Unblock
Start-ScheduledTask PreviewMotw-Unblock
```

- Чтобы ZIP-распаковка вообще не ставила метку, используйте 7-Zip — он `Zone.Identifier`
  не переносит (штатный ZIP-проводник ставит всегда).

## Удаление

```powershell
Set-ExecutionPolicy -Scope Process Bypass -Force
.\uninstall.ps1                 # обработчики (если ставили) + задача
.\uninstall.ps1 -Full -Purge    # + вернуть настройки MOTW + удалить файлы
```

Чтобы быстро убрать только экспериментальный обработчик, оставив MOTW-задачу
(именно это рекомендуется при зависаниях Проводника):

```powershell
foreach ($e in 'png','jpg','jpeg','jpe','tif','tiff','bmp','gif','psd','psb','svg','svgz') {
  reg delete "HKLM\Software\Classes\SystemFileAssociations\.$e\ShellEx\{8895B1C6-B41F-4C1C-A562-0D564250836F}" /ve /f 2>$null
}
& "$env:SystemRoot\Microsoft.NET\Framework64\v4.0.30319\regasm.exe" /unregister `
    "$env:LOCALAPPDATA\PreviewMotw\PreviewMotwHandlers.dll"
Get-Process prevhost -ErrorAction SilentlyContinue | Stop-Process -Force
```

## Как это работает

### MOTW watcher

1. при старте — полный проход по путям через `cmd /c dir /s /r` (самый дешёвый способ
   увидеть ADS у ~100k файлов), затем снятие метки в найденных каталогах;
2. дальше — `FileSystemWatcher`: файл разблокируется через 1–3 с;
3. каждые `SweepMinutes` — повторный проход.

### Обработчик предпросмотра (экспериментальный)

Managed COM-класс, зарегистрированный через `mscoree.dll` + `regasm /codebase`; по идее
должен грузиться в `prevhost.exe` (в CLSID прописан AppID `prevhost`
`{6d2b5079-…}`), создаёт `Panel` в WinForms, перепарентит его к окну хоста
(`SetParent` + `WS_CHILD`) и кладёт внутрь `PictureBox` (GDI+), `WebBrowser` (SVG) или
`Label` с сообщением. PSD читается вручную (big-endian заголовок, RLE PackBits/raw,
сборка в 24 bpp bitmap). Именно эта конструкция и вызывала зависания оболочки —
поэтому часть выключена по умолчанию.

## ⚠️ Безопасность

Mark of the Web — реальная линия защиты: он включает Protected View в Office,
SmartScreen, ограничения в Adobe Reader. Отключение метки и SmartScreen снижает защиту
от вредоносных документов. Используйте на машинах, где вы контролируете источник файлов,
и не отключайте на общих/рабочих компьютерах без согласования с ИБ.

## Ограничения

- `.ai/.eps/.ps` предпросмотра не имеют (Ghostscript-рендер убран — подвешивал Проводник).
  Смотрите такие файлы в Illustrator/Corel или держите рядом PDF-версию;
- `.zip .rar .7z` обработчика предпросмотра нет;
- SVG рендерится движком IE11: без `mshtml.dll` останется пустая панель;
- `.psd` с ZIP-сжатием композита показывается миниатюрой 160×120;
- регистрация только 64-битная;
- исходники `.cs` должны оставаться в UTF-8 с BOM (`csc.exe` определяет кодировку по BOM);
- обработчик предпросмотра — экспериментальный: см. предупреждение в начале.

## Лицензия

MIT, см. [LICENSE](LICENSE).

---

## English summary

**MOTW auto-unblock (stable):** the `PreviewMotw-Unblock` scheduled task removes the
`Zone.Identifier` alternate data stream from a set of roots at logon, via a
`FileSystemWatcher`, with a periodic full sweep as a safety net. This is what makes the
Explorer preview pane work for downloaded PDF/Office files without the "Unblock" checkbox.

**Preview-handler pack (experimental, opt-in via `-WithPreviewHandler`):** a managed
shell preview handler for `.png .jpg .jpeg .tif .bmp .gif .psd .psb .svg`, plus explicit
associations of text formats to the built-in TXT previewer. The CLR handler loaded into
`prevhost.exe` has been observed to hang the shell on some machines — keep it off unless
you are ready to roll it back.

Install elevated: `.\install.ps1 -Roots 'D:\'`; remove with `.\uninstall.ps1 -Full -Purge`.
Disabling Mark of the Web lowers document protection (Office Protected View, SmartScreen).
