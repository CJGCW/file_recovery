# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build & Run

```bash
# Run in development (Windows required — WPF target)
dotnet run

# Self-contained single-file release build
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

There are no tests and no linting configuration. The project has zero NuGet dependencies beyond the .NET 8 SDK.

## Architecture

This is a **Windows-only WPF desktop app** (.NET 8, `net8.0-windows`) following MVVM. The purpose is cataloguing files recovered from a corrupt drive.

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
