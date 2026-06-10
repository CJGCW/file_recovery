# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build & Run

```bash
# Run in development (Windows required — WPF target)
dotnet run

# Self-contained single-file release build
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

**Always finish a code-change task with a fresh single-file publish** (not just `dotnet build`). The user runs the published exe to verify changes, so producing the exe is part of "done."

There are no tests and no linting configuration. The project requires the .NET 10 SDK.

### Bundled external binaries

Bundled alongside the .exe in `tools/` (gitignored, ~115 MB total). The csproj copies them into the publish output with `ExcludeFromSingleFile=true`. Fetch them once after a fresh clone:

```powershell
# ── ffmpeg.exe (Gyan essentials build) — required for deep scan & metadata ──
$rel = Invoke-RestMethod 'https://api.github.com/repos/GyanD/codexffmpeg/releases/latest'
$url = ($rel.assets | Where-Object name -Like '*essentials_build.zip').browser_download_url
$zip = Join-Path $env:TEMP 'ffmpeg-essentials.zip'
Invoke-WebRequest $url -OutFile $zip -UseBasicParsing
Add-Type -AssemblyName System.IO.Compression.FileSystem
$z = [System.IO.Compression.ZipFile]::OpenRead($zip)
$entry = $z.Entries | Where-Object FullName -Match 'bin/ffmpeg\.exe$' | Select-Object -First 1
New-Item -ItemType Directory -Force tools | Out-Null
[System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, "tools/ffmpeg.exe", $true)
$z.Dispose()

# ── eng.traineddata (Tesseract OCR best-quality model) — required for Tesseract pass ──
New-Item -ItemType Directory -Force 'tools/tessdata' | Out-Null
Invoke-WebRequest 'https://github.com/tesseract-ocr/tessdata_best/raw/main/eng.traineddata' `
    -OutFile 'tools/tessdata/eng.traineddata' -UseBasicParsing
```

## Architecture

This is a **Windows-only WPF desktop app** (.NET 10, `net10.0-windows`) following MVVM. The purpose is cataloguing files recovered from a corrupt drive.

**Data flow:**
1. `Data/ExtensionMap.cs` — static dictionary mapping ~100 file extensions to `(MimeType, FileCategory)` tuples. Files with unrecognised extensions are **skipped** by the scanner (not recorded as Unknown).
2. `Services/FileTypeDetector.cs` — thin wrapper over `ExtensionMap`; returns `null` for unknown extensions.
3. `Services/FileScanner.cs` — async streaming engine. Uses `IAsyncEnumerable<FileRecord>` backed by a bounded `Channel<T>` (capacity 2000) and `Parallel.ForEachAsync` (capped at `ProcessorCount`). Per-file exceptions are silently swallowed. Designed for 700k+ file trees without memory pressure.
4. `ViewModels/MainViewModel.cs` — orchestrates scanning on a background `Task`, dispatches results to the UI thread in batches (status update every 500 files) via `Application.Current.Dispatcher.Invoke`. Holds two collections: `ObservableCollection<FileRecord> _allFiles` (backing store) and `ICollectionView FileView` (filtered/sorted WPF view).
5. `Views/MainWindow.xaml` — three-column layout with a virtualized `DataGrid` (`VirtualizationMode="Recycling"`) for the file list and a right-side image preview panel. Code-behind (`MainWindow.xaml.cs`) is minimal: wires the folder picker dialog and calls `PopulateExtensionFilters()` on scan completion.

**Key design notes:**
- `IAsyncEnumerable` was retained specifically to support a future PostgreSQL export path (the README notes a prior `PostgresExporter` was removed from this version).
- Image previews are loaded async at 420px width with `BitmapCacheOption.OnLoad` and `.Freeze()` for cross-thread safety.
- Global dark theme (accent `#7C6FF7`) and all shared styles are defined in `App.xaml`, not in individual views.
- `RelayCommand`, `CategoryFilter`, and `ExtensionFilter` helper classes are defined inline inside `MainViewModel.cs`.
