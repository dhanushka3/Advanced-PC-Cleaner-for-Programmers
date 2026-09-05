using System.Collections.Concurrent;
using System.IO;

namespace UNIDAdvancedPCCleaner;

public static class Scanner
{
    private static readonly string[] TargetNames =
    {
        "node_modules", "__pycache__", ".pytest_cache", ".mypy_cache", ".ruff_cache",
        "dist", "build", "out", "target", "obj", ".next", ".nuxt", ".output", ".turbo", ".cache"
    };

    private static readonly string[] PruneNames =
    {
        "AppData", ".git", "$Recycle.Bin", "System Volume Information", "Recovery", "PerfLogs", "Windows.old",
        ".vscode", ".cursor", ".antigravity-ide", ".idea", "xampp", "wamp", "wamp64", "laragon", "phpmyadmin",
        "mysql", "mariadb", "postgres", "mongodb", "redis", "nginx", "apache", "tomcat", "jenkins", "docker",
        "flutter", "android", "jdk", "jre", "nodejs", "tools", "extensions"
    };

    private static readonly string[] ExcludedDriveRoots =
    {
        "Windows", "Program Files", "Program Files (x86)", "ProgramData", "$Recycle.Bin",
        "System Volume Information", "Recovery", "PerfLogs", "Users", "Windows.old"
    };

    private static readonly (string Name, string Path)[] DevCacheDefs =
    {
        ("npm cache",       Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "npm-cache")),
        ("pnpm store",      Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "pnpm", "store")),
        ("pip cache",       Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "pip", "cache")),
        ("yarn cache",      Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Yarn")),
        ("go build cache",  Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "go-build")),
        ("nuget packages",  Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages")),
        ("gradle caches",   Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gradle", "caches")),
        ("maven repository",Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".m2", "repository")),
        ("cargo registry",  Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cargo", "registry")),
        ("bun cache",       Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".bun", "install", "cache")),
        ("composer cache",  Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Composer", "cache")),
        ("uv cache",        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "uv", "cache"))
    };

    private static readonly string UserProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static readonly string LocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public static List<CleanTarget> ScanDevDependencies(Action<int, string>? progress)
    {
        var found = new List<CleanTarget>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string name, string category, string path)
        {
            if (!visited.Add(path)) return;
            found.Add(new CleanTarget { Name = name, Category = category, Path = path, Impact = Impact.For(name) });
        }

        if (ScanOptions.IsPathOnSelectedDrive(UserProfile))
            Walk(UserProfile, 0, 4, Add);
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
            if (!DriveAllowed(drive.Name)) continue;
            foreach (var d in SafeEnumerateDirs(drive.RootDirectory.FullName))
            {
                if (ExcludedDriveRoots.Contains(d.Name)) continue;
                Walk(d.FullName, 1, 3, Add);
            }
        }

        Measure(found, progress);
        return found.Where(t => t.SizeBytes > 0).OrderByDescending(t => t.SizeBytes).ToList();
    }

    public static List<CleanTarget> ScanDevCaches(Action<int, string>? progress)
    {
        var found = new List<CleanTarget>();
        foreach (var def in DevCacheDefs)
        {
            if (Directory.Exists(def.Path) && ScanOptions.IsPathOnSelectedDrive(def.Path))
                found.Add(new CleanTarget { Name = def.Name, Category = "Dev Caches", Path = def.Path, Impact = Impact.For(def.Name) });
        }

        var jb = Path.Combine(LocalAppData, "JetBrains");
        if (Directory.Exists(jb) && ScanOptions.IsPathOnSelectedDrive(jb))
        {
            foreach (var dir in SafeEnumerateDirs(jb))
            {
                if (dir.Name.Contains("Toolbox", StringComparison.OrdinalIgnoreCase)) continue;
                var caches = Path.Combine(dir.FullName, "caches");
                if (Directory.Exists(caches))
                    found.Add(new CleanTarget { Name = $"JetBrains caches ({dir.Name})", Category = "Dev Caches", Path = caches, Impact = Impact.For("JetBrains") });
            }
        }

        Measure(found, progress);
        return found.Where(t => t.SizeBytes > 0).OrderByDescending(t => t.SizeBytes).ToList();
    }

    public static List<CleanTarget> ScanTemp(Action<int, string>? progress)
    {
        var found = new List<CleanTarget>();
        void Add(string name, string path)
        {
            if (Directory.Exists(path) && ScanOptions.IsPathOnSelectedDrive(path))
                found.Add(new CleanTarget { Name = name, Category = "Temp Files", Path = path, Impact = Impact.For(name) });
        }

        Add("User temp files", Path.GetTempPath());
        Add("Windows temp files", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp"));
        Measure(found, progress);
        return found.Where(t => t.SizeBytes > 0).OrderByDescending(t => t.SizeBytes).ToList();
    }

    public static List<CleanTarget> ScanRecycleBin(Action<int, string>? progress)
    {
        long total = 0;
        int count = 0;
        Exception? comError = null;
        var thread = new Thread(() =>
        {
            try
            {
                dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
                dynamic bin = shell.Namespace(10);
                foreach (var item in bin.Items())
                {
                    try
                    {
                        var size = item.ExtendedProperty("Size");
                        if (size != null) total += Convert.ToInt64(size);
                    }
                    catch { }
                    count++;
                }
            }
            catch (Exception ex) { comError = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        var found = new List<CleanTarget>();
        if (count > 0)
            found.Add(new CleanTarget { Name = "Recycle Bin", Category = "Recycle Bin", Path = "RECYCLEBIN", SizeBytes = total, IsRecycleBin = true, Impact = Impact.For("Recycle Bin") });
        progress?.Invoke(100, $"Found {count} item(s) in Recycle Bin");
        return found;
    }

    public static List<CleanTarget> ScanDownloads(Action<int, string>? progress)
    {
        var found = new List<CleanTarget>();
        var downloads = Path.Combine(UserProfile, "Downloads");
        if (!Directory.Exists(downloads) || !ScanOptions.IsPathOnSelectedDrive(downloads)) return found;

        var now = DateTime.Now;
        var all = new ConcurrentBag<CleanTarget>();
        try
        {
            foreach (var f in new DirectoryInfo(downloads).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
            {
                try
                {
                    bool old = (now - f.LastWriteTime).TotalDays > 90;
                    bool big = f.Length > 100L * 1024 * 1024;
                    if (old || big)
                        all.Add(new CleanTarget { Name = f.Name, Category = "Downloads", Path = f.FullName, SizeBytes = f.Length, IsFile = true, Impact = "user file - review before deleting" });
                }
                catch { }
            }
        }
        catch { }

        found = all.OrderByDescending(t => t.SizeBytes).Take(500).ToList();
        progress?.Invoke(100, $"Found {found.Count} old/large download file(s)");
        return found;
    }

    public static List<CleanTarget> ScanSystemFiles(Action<int, string>? progress)
    {
        var found = new List<CleanTarget>();
        void AddDir(string name, string path)
        {
            if (Directory.Exists(path) && ScanOptions.IsPathOnSelectedDrive(path))
                found.Add(new CleanTarget { Name = name, Category = "System Files", Path = path, Impact = Impact.For(name) });
        }

        AddDir("Windows Update cache", @"C:\Windows\SoftwareDistribution\Download");
        AddDir("Error reports (WER queue)", @"C:\ProgramData\Microsoft\Windows\WER\ReportQueue");
        AddDir("Error reports (WER archive)", @"C:\ProgramData\Microsoft\Windows\WER\ReportArchive");
        AddDir("Prefetch files", @"C:\Windows\Prefetch");

        var explorer = Path.Combine(LocalAppData, @"Microsoft\Windows\Explorer");
        if (Directory.Exists(explorer) && ScanOptions.IsPathOnSelectedDrive(explorer))
        {
            foreach (var f in Directory.EnumerateFiles(explorer, "thumbcache_*.db"))
                found.Add(new CleanTarget { Name = Path.GetFileName(f), Category = "System Files", Path = f, SizeBytes = new FileInfo(f).Length, IsFile = true, Impact = "safe - Windows regenerates automatically" });
        }

        Measure(found.Where(t => !t.IsFile).ToList(), progress);
        return found.Where(t => t.SizeBytes > 0).OrderByDescending(t => t.SizeBytes).ToList();
    }

    public static List<CleanTarget> ScanLargeFiles(Action<int, string>? progress)
    {
        const long minSize = 500L * 1024 * 1024;
        var found = new List<CleanTarget>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
            if (!DriveAllowed(drive.Name)) continue;
            foreach (var d in SafeEnumerateDirs(drive.RootDirectory.FullName))
            {
                if (ExcludedDriveRoots.Contains(d.Name)) continue;
                WalkFiles(d.FullName, 1, 3, minSize, found);
            }
        }
        progress?.Invoke(100, $"Found {found.Count} large file(s)");
        return found.OrderByDescending(t => t.SizeBytes).Take(200).ToList();
    }

    private static void WalkFiles(string dir, int depth, int maxDepth, long minSize, List<CleanTarget> found)
    {
        if (depth > maxDepth) return;
        foreach (var d in SafeEnumerateDirs(dir))
        {
            if (PruneNames.Contains(d.Name)) continue;
            foreach (var f in SafeEnumerateFiles(d.FullName))
            {
                try
                {
                    if (f.Length >= minSize)
                        found.Add(new CleanTarget { Name = f.Name, Category = "Large Files", Path = f.FullName, SizeBytes = f.Length, IsFile = true, Impact = "user file - verify before deleting" });
                }
                catch { }
            }
            WalkFiles(d.FullName, depth + 1, maxDepth, minSize, found);
        }
    }

    private static void Walk(string dir, int depth, int maxDepth, Action<string, string, string> add)
    {
        if (depth > maxDepth) return;
        foreach (var d in SafeEnumerateDirs(dir))
        {
            if (PruneNames.Contains(d.Name)) continue;
            if (TargetNames.Contains(d.Name))
            {
                add(d.Name, "Dev Dependencies & Builds", d.FullName);
                continue;
            }
            Walk(d.FullName, depth + 1, maxDepth, add);
        }
    }

    private static bool DriveAllowed(string driveName)
    {
        var letter = driveName.TrimEnd('\\');
        if (letter.Length < 2 || letter[1] != ':') return false;
        return ScanOptions.SelectedDrives.Contains(letter.Substring(0, 1).ToUpperInvariant());
    }

    private static IEnumerable<DirectoryInfo> SafeEnumerateDirs(string path)
    {
        try
        {
            return new DirectoryInfo(path).EnumerateDirectories("*", new EnumerationOptions
            {
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint
            });
        }
        catch
        {
            return Array.Empty<DirectoryInfo>();
        }
    }

    private static IEnumerable<FileInfo> SafeEnumerateFiles(string path)
    {
        try
        {
            return new DirectoryInfo(path).EnumerateFiles("*", new EnumerationOptions
            {
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint
            });
        }
        catch
        {
            return Array.Empty<FileInfo>();
        }
    }

    private static void Measure(List<CleanTarget> found, Action<int, string>? progress)
    {
        if (found.Count == 0) return;
        int done = 0;
        Parallel.ForEach(found, new ParallelOptions { MaxDegreeOfParallelism = 4 }, t =>
        {
            t.SizeBytes = FolderSize(t.Path);
            int d = Interlocked.Increment(ref done);
            progress?.Invoke((int)(d * 100.0 / found.Count), $"Measuring [{t.Category}] {d}/{found.Count}");
        });
    }

    public static long FolderSize(string path)
    {
        try
        {
            long total = 0;
            foreach (var f in new DirectoryInfo(path).EnumerateFiles("*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint
            }))
            {
                total += f.Length;
            }
            return total;
        }
        catch
        {
            return 0;
        }
    }
}