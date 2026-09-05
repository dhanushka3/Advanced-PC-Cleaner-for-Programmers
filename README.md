# UNID Advanced PC Cleaner

> Built for developers. Safe for everyone.

**UNID Advanced PC Cleaner** finds and cleans the disk space eaten by development dependencies, build outputs, package-manager caches and common junk files — with full user verification before anything is deleted.

It comes in two forms:

| Form | What it is | When to use |
|---|---|---|
| **UI app** (`UNID-Advanced-PC-Cleaner/`) | WPF desktop app (.NET 8, Windows) | Recommended - visual, per-item checkboxes, progress bars |
| **PowerShell script** (`PC-Cleaner.ps1`) | Zero-dependency console tool | Scripting, automation, no runtime needed |

---

## Features

- **Dev Dependencies & Builds** - `node_modules`, `dist`, `build`, `out`, `target`, `obj`, `.next`, `.nuxt`, `__pycache__` and more, found across all drives
- **Dev Caches** - npm, pnpm, yarn, pip, uv, go, cargo, bun, nuget, gradle, maven, composer, JetBrains
- **Temp Files** - user and Windows temp (in-use files are skipped automatically)
- **Recycle Bin** - with size preview and permanent-delete warning
- **Downloads** - old (90+ days) and large (100 MB+) files, review before deleting
- **System Files** - Windows Update cache, thumbnail caches, error reports, prefetch
- **Large Files** - files over 500 MB on your fixed drives
- **Drive limiting** - scan only the drives you choose (C:, D:, ...)
- **Impact warnings per item** - every item shows what happens if you delete it
- **Safety engine** - installed tools (Node.js, databases, XAMPP, IDEs...), Program Files, drive roots and your user profile are NEVER touched

## Safety

Everything the tool deletes is a **cache, dependency or build output** - never your source code, documents or personal data. Each item is listed with its exact size and an impact note, and deletion happens only after you confirm it. Locked/in-use files are reported and left alone.

| Target | Effect of deleting |
|---|---|
| `node_modules` | Run `npm install` / `yarn` to restore (needs lockfile + internet) |
| `.next`, `dist`, `build` | Rebuild the project; deployed sites need a redeploy |
| npm/pip/cargo caches | Re-downloaded on the next install |
| Temp files | In-use files are skipped automatically |
| Recycle Bin | Permanent - cannot be restored |

## Usage

### UI app

```powershell
& "H:\OpenCode\PC Cleaner\UNID-Advanced-PC-Cleaner\bin\Release\net8.0-windows\UNIDAdvancedPCCleaner.exe"
```

Or grab the latest release build from the [Releases](https://github.com/dhanushka3/Advanced-PC-Cleaner-for-Programmers/releases) page.

1. Pick a section from the sidebar
2. Click **Scan** (optionally limit to certain drives)
3. Tick the items you want
4. Click **Clean Selected** and confirm

### PowerShell script

```powershell
# Interactive mode
powershell -ExecutionPolicy Bypass -File "PC-Cleaner.ps1"

# Preview only - nothing gets deleted
powershell -ExecutionPolicy Bypass -File "PC-Cleaner.ps1" -ScanOnly

# Scan extra folders
powershell -ExecutionPolicy Bypass -File "PC-Cleaner.ps1" -ExtraRoots "D:\Work"

# Scripted cleanup (automation)
powershell -ExecutionPolicy Bypass -File "PC-Cleaner.ps1" -SelectIds 2,6,10 -Force
```

Every deletion is written to `cleaner-log.csv` next to the script.

## Building the UI app

Requires the .NET 8 SDK:

```powershell
dotnet build "UNID-Advanced-PC-Cleaner\UNID-Advanced-PC-Cleaner.csproj" -c Release

# Portable single-file exe
dotnet publish "UNID-Advanced-PC-Cleaner\UNID-Advanced-PC-Cleaner.csproj" -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

## Project structure

```
├── PC-Cleaner.ps1                    # PowerShell version
├── UNID-Advanced-PC-Cleaner/
│   ├── UNID-Advanced-PC-Cleaner.csproj
│   ├── App.xaml / App.xaml.cs        # WPF app entry
│   ├── MainWindow.xaml / .cs         # UI
│   └── Core/
│       ├── Scanner.cs                # scan engine (all sections)
│       ├── Cleaner.cs                # deletion + safety engine
│       └── Models.cs                 # targets, impact notes, drive options
```

## License

Free to use. Distributed under the MIT License.

---

Made with ❤️ by [UNID.Digital](https://unid.digital)