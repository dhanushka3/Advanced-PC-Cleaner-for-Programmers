namespace UNIDAdvancedPCCleaner;

using System.IO;

public static class ScanOptions
{
    public static HashSet<string> SelectedDrives { get; } = new(StringComparer.OrdinalIgnoreCase);

    public static bool IsPathOnSelectedDrive(string path)
    {
        try
        {
            if (string.IsNullOrEmpty(path)) return true;
            if (path == "RECYCLEBIN") return true;
            var root = Path.GetPathRoot(path);
            if (string.IsNullOrEmpty(root)) return true;
            var letter = root.TrimEnd('\\');
            if (letter.Length < 2 || letter[1] != ':') return true;
            return SelectedDrives.Contains(letter.Substring(0, 1).ToUpperInvariant());
        }
        catch
        {
            return true;
        }
    }
}

public class CleanTarget
{
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
    public long SizeBytes { get; set; }
    public string Impact { get; set; } = "";
    public bool IsSelected { get; set; }
    public bool IsFile { get; set; }
    public bool IsRecycleBin { get; set; }

    public string SizeText => FormatSize(SizeBytes);

    public static string FormatSize(long bytes)
    {
        if (bytes < 1024L) return $"{bytes} B";
        if (bytes < 1024L * 1024) return $"{(bytes / 1024.0):N0} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{(bytes / (1024.0 * 1024)):N1} MB";
        return $"{(bytes / (1024.0 * 1024 * 1024)):N2} GB";
    }
}

public static class Impact
{
    public static string For(string name)
    {
        var n = name.ToLowerInvariant();
        if (n.Contains("recycle")) return "PERMANENT - deleted files cannot be restored";
        if (n.Contains("node_modules")) return "dependencies removed - run npm install / yarn to restore (needs lockfile + internet)";
        if (n.StartsWith("npm cache") || n.StartsWith("pnpm store") || n.StartsWith("yarn cache") || n.StartsWith("bun cache")) return "safe - packages re-download on next install";
        if (n.StartsWith("pip cache") || n.StartsWith("uv cache") || n.StartsWith("composer")) return "safe - re-downloaded on next install";
        if (n.StartsWith("cargo")) return "crates re-fetched on next build - offline builds fail until then";
        if (n.StartsWith("nuget")) return "restored on next dotnet restore/build - offline builds fail until then";
        if (n.StartsWith("gradle")) return "re-downloaded on next Gradle build";
        if (n.StartsWith("maven")) return "re-downloaded on next Maven build";
        if (n.StartsWith("go build")) return "safe - rebuilds automatically";
        if (n.Contains("jetbrains")) return "safe - rebuilt when the IDE starts";
        if (n.StartsWith(".next") || n.StartsWith(".nuxt") || n.StartsWith(".output") || n.StartsWith(".turbo")) return "build output - recreate with next build / nuxt build; deployed sites need rebuild + redeploy";
        if (n is "dist" or "build" or "out" or "target" or "obj") return "build output - regenerated on next project build";
        if (n.Contains("__pycache__") || n.Contains("pytest_cache") || n.Contains("mypy_cache") || n.Contains("ruff_cache")) return "safe - Python regenerates automatically";
        if (n.StartsWith(".cache")) return "safe - tools rebuild it automatically";
        if (n.Contains("temp")) return "in-use files are skipped; close running apps first";
        if (n.Contains("thumb") || n.Contains("prefetch")) return "safe - Windows regenerates automatically";
        if (n.Contains("update") || n.Contains("wer") || n.Contains("error report")) return "safe - Windows re-downloads or regenerates";
        return "regenerable - verify before deleting";
    }
}

public class SectionRow
{
    public string Name { get; set; } = "";
    public string Detail { get; set; } = "not scanned yet";
}