# File Recovery Parser

A WPF desktop app for cataloguing and browsing files recovered from a corrupt drive.

## Requirements

- Windows 10/11
- [.NET 8 SDK](https://dotnet.microsoft.com/download) (for building)

## Running

```bash
cd FileRecoveryParser
dotnet run
```

Or build a self-contained executable:

```bash
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

## Features

- **Folder picker** — browse or type a path, then click Scan Folder
- **Live streaming** — files appear in the list as they are found; cancel any time
- **Category filters** — toggle Image / Video / Audio / Document etc. in the sidebar
- **Extension filters** — populated automatically after a scan; check/uncheck individual extensions
- **Search bar** — filters the list by file name or extension in real time
- **Sortable columns** — click any column header to sort; click again to reverse
- **Image preview** — select any image row to see a thumbnail and file metadata in the right panel
- **Virtualized list** — handles 700k+ rows without lag

## Project structure

```
FileRecoveryParser/
├── App.xaml / App.xaml.cs          Application entry point + global styles
├── Views/
│   ├── MainWindow.xaml             Main UI layout
│   └── MainWindow.xaml.cs         Folder browser, post-scan wiring
├── ViewModels/
│   └── MainViewModel.cs           All UI state, scanning, filtering, sorting
├── Services/
│   ├── FileScanner.cs             Parallel directory walker (IAsyncEnumerable)
│   └── FileTypeDetector.cs        Extension → MIME / category lookup
├── Models/
│   ├── FileRecord.cs              Per-file data model
│   └── FileCategory.cs            Category enum
├── Data/
│   └── ExtensionMap.cs            Extension → (MIME, category) dictionary
└── Converters/
    └── Converters.cs              WPF value converters (size, colour, date, sort arrow)
```

## Adding PostgreSQL later

When you're ready to add DB export, `FileScanner.ScanAsync` returns an
`IAsyncEnumerable<FileRecord>` — just add a second consumer that reads from the
same stream (or re-runs a scan) and bulk-inserts into Postgres using the
`PostgresExporter` from the earlier version of this project.
