# motw-preview-fix

Набор из двух независимых инструментов для Windows 10/11, которые убирают самую
раздражающую связку «скачал файл → он не открывается/не предпросматривается»:

1. **Preview handler pack** — собственный обработчик панели предпросмотра Проводника
   для форматов, которых в Windows 10 нет «из коробки»:
   `.png .jpg .jpeg .tif .tiff .bmp .gif .psd .psb .svg .ai .eps .ps`, плюс явная
   привязка текстовых форматов к встроенному TXT-обработчику.
2. **MOTW auto-unblock** — служба, которая автоматически снимает метку
   «Файл получен из Интернета» (`Zone.Identifier`, он же Mark of the Web)
   с новых файлов и с целых дисков.

Обе части можно ставить по отдельности (`-SkipPreviewHandler` / `-SkipMotw`).

---

## Проблема

Windows помечает скачанные и распакованные из ZIP файлы альтернативным потоком NTFS
`Zone.Identifier` (`ZoneId=3`). Из-за этой метки:

- в свойствах файла появляется галка «Разблокировать»;
- часть программ (Acrobat, Office, некоторые просмотрщики) отказывается
  показывать содержимое, пока галку не поставить;
- ZIP-распаковщик Проводника переносит метку во **все** файлы архива.

При этом для картинок, PSD, SVG, AI и EPS в Windows 10 обработчика предпросмотра
нет вообще — панель предпросмотра пуста даже без всякой метки. Этот репозиторий
закрывает и то, и другое.

## Поддерживаемые форматы предпросмотра

| Расширения | Как рендерится | Зависимости |
|---|---|---|
| `.png .jpg .jpeg .jpe .tif .tiff .bmp .gif` | GDI+ (`Image.FromStream`), вписывание без искажений | нет |
| `.psd .psb` | полный композит PSD (big-endian, RLE PackBits / raw, RGB и CMYK); если композит не читается — встроенная JPEG-миниатюра | нет |
| `.svg .svgz` | элемент WebBrowser (движок IE11) | IE11 (есть в Windows 10/11) |
| `.ai .eps .ps` | Ghostscript, первая страница, 120 dpi | Ghostscript (опционально) |
| `.txt .json .md .markdown .sh .bash .log .yml .yaml .xml .css .js .cs .py .ini .cfg .conf .ts .sql .ps1 .bat .cmd` | штатный Windows TXT Previewer | нет |

## Требования

- Windows 10 (19041+) или Windows 11, x64;
- .NET Framework 4.x — `csc.exe` и `regasm.exe` уже входят в систему, Visual Studio не нужен;
- PowerShell 5.1 (встроенный);
- права администратора для установки (запись в `HKLM` и создание задачи планировщика);
- опционально: [Ghostscript](https://ghostscript.com/releases/gsdnld.html) для `.ai/.eps/.ps`.

## Установка

```powershell
git clone https://github.com/<owner>/motw-preview-fix.git
cd motw-preview-fix

# всё сразу; перечисли свои диски/папки, которые надо держать без MOTW
powershell -ExecutionPolicy Bypass -File .\install.ps1 -Roots 'D:\', 'E:\Media', "$env:USERPROFILE\Downloads"

# только обработчик предпросмотра
powershell -ExecutionPolicy Bypass -File .\install.ps1 -SkipMotw

# только автоснятие метки
powershell -ExecutionPolicy Bypass -File .\install.ps1 -SkipPreviewHandler -Roots 'D:\'
```

Что делает `install.ps1`:

1. компилирует `src\PreviewMotwHandlers.cs` в `%LOCALAPPDATA%\PreviewMotw\PreviewMotwHandlers.dll`;
2. `regasm /codebase` → COM-класс `{B5C1D3E7-4A62-4F0F-9C1E-7D2A5B8F3C64}`
   (+ `Implemented Categories\{8895B1C6-…}`, AppID `prevhost` `{6d2b5079-…}`,
   `DisableLowILProcessIsolation=1`);
3. прописывает ассоциации обработчика в
   `HKLM\Software\Classes\SystemFileAssociations\.<ext>\ShellEx\{8895B1C6-…}`;
4. ослабляет Attachment Manager (см. ниже);
5. создаёт задачу планировщика `PreviewMotw-Unblock` (вход в систему + watcher);
6. сбрасывает кэш ассоциаций `SHChangeNotify(SHCNE_ASSOCCHANGED)` и печатает
   фактическое разрешение обработчиков через `AssocQueryString`.

## Проверка

Разрешение обработчиков (то, что реально увидит Проводник):

```powershell
# панель предпросмотра: выделить файл и нажать Alt+P
# либо программно:
Add-Type -TypeDefinition @'
using System; using System.Runtime.InteropServices; using System.Text;
public static class A {
  [DllImport("shlwapi.dll", CharSet=CharSet.Unicode)]
  public static extern int AssocQueryString(int f, int s, string a, string e, StringBuilder b, ref int l);
  public static string H(string ext) { int l=512; var b=new StringBuilder(l);
    AssocQueryString(0,16,ext,"{8895B1C6-B41F-4C1C-A562-0D564250836F}",b,ref l); return b.ToString(); }
}
'@
[A]::H('.psd'); [A]::H('.ai'); [A]::H('.md')
```

Проверка обработчика вообще без Проводника (рендерит файл в скрытое окно и
сохраняет скриншот):

```powershell
cd src
& "$env:SystemRoot\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /target:exe /platform:x64 `
  /out:TestHarness.exe /r:System.dll /r:System.Core.dll /r:System.Drawing.dll `
  /r:System.Windows.Forms.dll /r:"$env:LOCALAPPDATA\PreviewMotw\PreviewMotwHandlers.dll" TestHarness.cs
.\TestHarness.exe "C:\path\to\file.psd" "$env:TEMP\out.png"
# ожидаемый вывод: diag: image 3600x2600 ext=.psd
```

Проверка автоснятия метки: создать файл с `Zone.Identifier` в отслеживаемой папке
и подождать 2–3 секунды:

```powershell
$f = "$env:USERPROFILE\Downloads\motw-test.txt"
'x' | Set-Content $f
"[ZoneTransfer]`r`nZoneId=3" | Set-Content $f -Stream Zone.Identifier
Start-Sleep 5
Get-Item $f -Stream Zone.Identifier   # должно быть пусто
```

Лог watcher-а: `%LOCALAPPDATA%\PreviewMotw\unblock.log`.

## Настройка

- Список отслеживаемых путей и интервал контрольного прохода задаются при установке
  (`-Roots`, `-SweepMinutes`). Пути не передаются через командную строку задачи —
  установщик пишет их в `%LOCALAPPDATA%\PreviewMotw\roots.json`, а скрипт читает
  оттуда (кавычки вокруг `"D:\"` в аргументах `powershell.exe -File` — известная
  ловушка: `\"` там считается экранированной кавычкой).

- Добавить/убрать путь позже — два способа:

```powershell
# 1) переустановить задачу с новым списком (в elevated PowerShell)
.\install.ps1 -Roots 'D:\', 'E:\Media', "$env:USERPROFILE\Downloads"

# 2) поправить roots.json вручную и перезапустить задачу
@('D:\', 'E:\Media') | ConvertTo-Json -Compress |
    Set-Content "$env:LOCALAPPDATA\PreviewMotw\roots.json" -Encoding UTF8
Stop-ScheduledTask  PreviewMotw-Unblock
Start-ScheduledTask PreviewMotw-Unblock
```

- Скрипты не подписаны, поэтому запускать их нужно с `-ExecutionPolicy Bypass`
  (или `Unblock-File` на скачанные `.ps1`), иначе `RemoteSigned` их не пустит.

- Чтобы ZIP-распаковка вообще не ставила метку, используйте 7-Zip (он не переносит
  `Zone.Identifier`) — штатный ZIP-проводник метку ставит всегда.

## Удаление

```powershell
powershell -ExecutionPolicy Bypass -File .\uninstall.ps1            # обработчики + задача
powershell -ExecutionPolicy Bypass -File .\uninstall.ps1 -Full -Purge  # + настройки MOTW + файлы
```

## Как это работает

### Обработчик предпросмотра

Managed COM-класс, зарегистрированный через `mscoree.dll` + `regasm /codebase`;
загружается Проводником в `prevhost.exe` (поэтому нужен AppID prevhost и
`DisableLowILProcessIsolation`, иначе хост не сможет создать дочернее окно).
Обработчик создаёт `Panel` в WinForms, перепарентит его к окну хоста
(`SetParent` + `WS_CHILD`) и кладёт внутрь `PictureBox` (GDI+), `WebBrowser`
(SVG) или `Label` с сообщением.

PSD читается вручную: big-endian заголовок, пропуск color-mode/resources/
layer&mask, затем uncompressed (`compression=0`) или PackBits RLE (`compression=1`)
плоские каналы 8 бит; сборка в `Bitmap` 24 bpp, CMYK конвертируется приближённо.
ZIP-сжатые PSD (редкий случай) уходят в fallback — встроенную миниатюру 1036.

EPS/AI/PS растеризуются Ghostscript-ом во временный PNG во временной папке.

### MOTW watcher

- при старте — полный проход по путям через `cmd /c dir /s /r` (самый дешёвый способ
  увидеть ADS у ~100k файлов: ~5 с), затем снятие метки в найденных каталогах;
- дальше — `FileSystemWatcher` на каждый путь: файл разблокируется через 1–3 с;
- каждые `SweepMinutes` — повторный проход, потому что `FileSystemWatcher` не
  сообщает про запись только в альтернативный поток;
- файлы с атрибутом «только чтение» обрабатываются со снятием/восстановлением атрибута
  (`Unblock-File` на RO-файлах молча не работает).

### Настройки Windows, которые меняет `install.ps1`

| Ключ | Значение | Смысл |
|---|---|---|
| `HKCU\…\Policies\Attachments\SaveZoneInformation` | 1 | не сохранять сведения о зоне у вложений |
| `HKCU\…\Policies\Attachments\HideZoneInfoOnProperties` | 1 | убрать из свойств галку «Разблокировать» |
| `HKLM\…\Policies\Attachments\ScanWithAntiVirus` | 1 | не проверять вложения антивирусом |
| `HKCU\…\AppHost\EnableWebContentEvaluation` | 0 | SmartScreen для скачанных файлов выключен |

Снять их можно `uninstall.ps1 -Full`.

## ⚠️ Безопасность

Mark of the Web — реальная линия защиты: он включает Protected View в Office,
SmartScreen, ограничения в Adobe Reader. Отключение метки и SmartScreen снижает
защиту от вредоносных документов. Используйте на машинах, где вы контролируете
источник файлов, и не отключайте это на общих/рабочих компьютерах без согласования
с ИБ.

## Ограничения

- `.zip .rar .7z` обработчика предпросмотра не имеют;
- SVG рендерится движком IE11: без IE (`mshtml.dll` отключён групповой политикой)
  останется пустая панель;
- `.psd` с ZIP-сжатием композита показывается миниатюрой 160×120;
- AI/EPS рендерятся 0,5–2 с, Ghostscript запускается на каждый показ;
- регистрация только 64-битная (32-битный `prevhost` обработчик не увидит);
- исходники `.cs` должны оставаться в UTF-8 с BOM — `csc.exe` определяет кодировку по BOM.

## Лицензия

MIT, см. [LICENSE](LICENSE).

---

## English summary

Two independent Windows 10/11 tools:

1. **Preview handler pack** — a managed shell preview handler for `.png .jpg .jpeg
   .tif .tiff .bmp .gif .psd .psb .svg .ai .eps .ps` (Windows ships none of these),
   plus explicit `.txt/.json/.md/.sh/...` associations to the built-in TXT previewer.
   PSD/PSB composites are decoded in C#, SVG goes through the IE WebBrowser control,
   EPS/AI/PS are rasterized with Ghostscript.
2. **MOTW auto-unblock** — the `PreviewMotw-Unblock` scheduled task removes the
   `Zone.Identifier` alternate data stream from a set of roots on logon, via a
   `FileSystemWatcher`, with a periodic full sweep as a safety net.

Install elevated: `.\install.ps1 -Roots 'D:\'`, remove with `.\uninstall.ps1 -Full -Purge`.
Disabling Mark of the Web lowers document protection (Office Protected View,
SmartScreen) — use it deliberately.
