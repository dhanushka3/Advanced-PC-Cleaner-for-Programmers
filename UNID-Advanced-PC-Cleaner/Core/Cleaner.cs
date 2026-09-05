using System.IO;
using System.Runtime.InteropServices;

namespace UNIDAdvancedPCCleaner;

public static class Cleaner
{
    public class ItemResult
    {
        public CleanTarget Target { get; set; } = new();
        public string Status { get; set; } = "";
        public string Details { get; set; } = "";
    }

    public class Summary
    {
        public long FreedBytes { get; set; }
        public int FailedCount { get; set; }
        public List<ItemResult> Results { get; } = new();
    }

    [DllImport("shell32.dll")]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? pszRootPath, int dwFlags);

    private static readonly string[] ToolNames = { "node", "npm", "npx", "yarn", "pnpm", "pip", "python", "go", "cargo", "dotnet", "java" };
    private static List<string>? _toolDirs;

    private static List<string> ToolDirs()
    {
        if (_toolDirs != null) return _toolDirs;
        _toolDirs = new List<string>();
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var tool in ToolNames)
        {
            foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    if (File.Exists(Path.Combine(dir, tool + ".exe")))
                    {
                        _toolDirs.Add(Path.GetFullPath(dir).TrimEnd('\\').ToLowerInvariant());
                        break;
                    }
                }
                catch { }
            }
        }
        return _toolDirs;
    }

    public static bool IsProtected(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return true;
        string full;
        try { full = Path.GetFullPath(path).TrimEnd('\\').ToLowerInvariant(); }
        catch { return true; }

        var protectedRoots = new List<string>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType == DriveType.Fixed && drive.IsReady)
                protectedRoots.Add(drive.RootDirectory.FullName.TrimEnd('\\').ToLowerInvariant());
        }
        protectedRoots.Add(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('\\').ToLowerInvariant());
        protectedRoots.Add(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles).TrimEnd('\\').ToLowerInvariant());
        protectedRoots.Add(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86).TrimEnd('\\').ToLowerInvariant());
        protectedRoots.Add(Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\').ToLowerInvariant());
        protectedRoots.Add(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData).TrimEnd('\\').ToLowerInvariant());
        protectedRoots.Add(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData).TrimEnd('\\').ToLowerInvariant());
        protectedRoots.Add(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData).TrimEnd('\\').ToLowerInvariant());

        foreach (var root in protectedRoots)
        {
            if (full.Equals(root, StringComparison.Ordinal)) return true;
        }

        foreach (var toolDir in ToolDirs())
        {
            if (full.Equals(toolDir, StringComparison.Ordinal) || full.StartsWith(toolDir + "\\", StringComparison.Ordinal)) return true;
        }

        return false;
    }

    public static Summary CleanAll(List<CleanTarget> targets, Action<string, int>? progress)
    {
        var summary = new Summary();
        int done = 0;
        foreach (var t in targets)
        {
            done++;
            progress?.Invoke($"Cleaning {t.Name}...", (int)(done * 100.0 / targets.Count));
            var result = CleanOne(t);
            summary.Results.Add(result);
            if (result.Status == "Deleted") summary.FreedBytes += Math.Max(0, t.SizeBytes);
            if (result.Status is "Failed" or "Partial" or "Skipped") summary.FailedCount++;
        }
        return summary;
    }

    private static ItemResult CleanOne(CleanTarget t)
    {
        if (t.IsRecycleBin)
        {
            var letters = ScanOptions.SelectedDrives.Count > 0
                ? ScanOptions.SelectedDrives.ToList()
                : new List<string>();
            if (letters.Count == 0)
                return new ItemResult { Target = t, Status = "Failed", Details = "No drives selected" };
            int failed = 0;
            foreach (var letter in letters)
            {
                int rc = SHEmptyRecycleBin(IntPtr.Zero, letter + ":\\", 0x1 | 0x2 | 0x4);
                if (rc != 0) failed++;
            }
            if (failed == 0)
                return new ItemResult { Target = t, Status = "Deleted", Details = "Recycle Bin emptied" };
            return new ItemResult
            {
                Target = t,
                Status = failed == letters.Count ? "Failed" : "Partial",
                Details = $"{failed} drive(s) failed"
            };
        }

        if (IsProtected(t.Path))
            return new ItemResult { Target = t, Status = "Skipped", Details = "Protected path (tool/software directory)" };

        try
        {
            if (t.IsFile)
            {
                File.Delete(t.Path);
                return new ItemResult { Target = t, Status = "Deleted", Details = "OK" };
            }

            try
            {
                Directory.Delete(t.Path, true);
            }
            catch
            {
                BestEffortDelete(t.Path);
            }

            return Directory.Exists(t.Path)
                ? new ItemResult { Target = t, Status = "Partial", Details = "Some files locked/in use, leftovers remain" }
                : new ItemResult { Target = t, Status = "Deleted", Details = "OK" };
        }
        catch (Exception ex)
        {
            return new ItemResult { Target = t, Status = "Failed", Details = ex.Message };
        }
    }

    private static void BestEffortDelete(string dir)
    {
        try
        {
            foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint
            }))
            {
                try { f.Delete(); } catch { }
            }
        }
        catch { }

        try
        {
            foreach (var d in Directory.EnumerateDirectories(dir, "*", SearchOption.AllDirectories)
                         .OrderByDescending(x => x.Length))
            {
                try { Directory.Delete(d, true); } catch { }
            }
        }
        catch { }

        try { Directory.Delete(dir, true); } catch { }
    }
}