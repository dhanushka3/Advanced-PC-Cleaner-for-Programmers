using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace UNIDAdvancedPCCleaner;

public partial class MainWindow : Window
{
    private readonly Dictionary<string, List<CleanTarget>> _results = new();
    private readonly ObservableCollection<SectionRow> _dashRows = new();
    private readonly Dictionary<string, SectionRow> _dashMap = new();
    private string _current = "Dashboard";
    private bool _busy;

    private static readonly (string Tag, string Title, string Desc)[] Sections =
    {
        ("Dev", "Dev Dependencies & Builds",
         "node_modules, build outputs (dist, build, out, target, obj), Python caches and .next/.nuxt build dirs found on your drives. All regenerable via package managers and builds."),
        ("Cache", "Dev Caches",
         "Package manager caches (npm, pip, pnpm, yarn, go, cargo, bun, nuget, gradle, maven, uv, composer, JetBrains). Safe - re-downloaded on the next install."),
        ("Temp", "Temp Files",
         "User and Windows temporary files. Files currently in use are skipped automatically."),
        ("Recycle", "Recycle Bin",
         "Permanently empties the Recycle Bin on all drives. Deleted files cannot be restored."),
        ("Downloads", "Downloads",
         "Files in your Downloads folder that are old (90+ days) or large (100 MB+). Review each file before deleting."),
        ("System", "System Files",
         "Windows Update downloads, thumbnail caches, error reports and prefetch files. Some folders require administrator rights."),
        ("Large", "Large Files",
         "Files larger than 500 MB found on your fixed drives. These may be important - verify before deleting.")
    };

    public MainWindow()
    {
        InitializeComponent();
        SectionNav.SelectedIndex = 0;

        foreach (var s in Sections)
        {
            var row = new SectionRow { Name = s.Title };
            _dashMap[s.Tag] = row;
            _dashRows.Add(row);
        }
        DashList.ItemsSource = _dashRows;

        foreach (var d in DriveInfo.GetDrives())
        {
            if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
            var letter = d.Name.Substring(0, 1).ToUpperInvariant();
            ScanOptions.SelectedDrives.Add(letter);
            var chk = new CheckBox
            {
                Content = letter + ":",
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                IsChecked = true
            };
            chk.Checked += DriveChk_Changed;
            chk.Unchecked += DriveChk_Changed;
            DrivePanel.Children.Add(chk);
        }
        AllDrivesChk.IsChecked = DrivePanel.Children.Count > 0;
    }

    private void SectionNav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SectionNav.SelectedItem is not ListBoxItem item || item.Tag is not string tag) return;
        ShowSection(tag);
    }

    private void ShowSection(string tag)
    {
        _current = tag;
        bool isDash = tag == "Dashboard";
        DashboardPanel.Visibility = isDash ? Visibility.Visible : Visibility.Collapsed;
        ResultsGrid.Visibility = isDash ? Visibility.Collapsed : Visibility.Visible;
        CleanBtn.Visibility = isDash ? Visibility.Collapsed : Visibility.Visible;
        SelectAllChk.Visibility = isDash ? Visibility.Collapsed : Visibility.Visible;
        SelectedInfo.Visibility = isDash ? Visibility.Collapsed : Visibility.Visible;

        if (isDash)
        {
            SectionTitle.Text = "Dashboard";
            SectionDesc.Text = "Overview of all cleanup sections.";
            ScanBtn.Content = "Scan All";
            UpdateDashboard();
        }
        else
        {
            var (_, title, desc) = Sections.First(s => s.Tag == tag);
            SectionTitle.Text = title;
            SectionDesc.Text = desc;
            ScanBtn.Content = "Scan";
            if (_results.TryGetValue(tag, out var list))
            {
                ResultsGrid.ItemsSource = list;
                UpdateFooter();
            }
            else
            {
                ResultsGrid.ItemsSource = null;
                SelectedInfo.Text = "Selected: 0 items - 0 B";
                SelectAllChk.IsChecked = false;
            }
        }
    }

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _current == "Dashboard") return;
        await RunScan(_current);
    }

    private async void ScanAll_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        foreach (var s in Sections)
        {
            await RunScan(s.Tag);
        }
        SectionNav.SelectedIndex = 0;
    }

    private async Task RunScan(string tag)
    {
        SetBusy(true);
        ScanProgress.Value = 0;
        StatusText.Text = $"Scanning [{titleOf(tag)}]...";
        IProgress<(int, string)> progress = new Progress<(int, string)>(v =>
        {
            ScanProgress.Value = v.Item1;
            StatusText.Text = v.Item2;
        });
        var items = await Task.Run(() => ScanSection(tag, (p, l) => progress.Report((p, l))));
        _results[tag] = items;
        var row = _dashMap[tag];
        if (items.Count == 0)
        {
            row.Detail = "nothing to clean";
        }
        else
        {
            long total = items.Sum(t => t.SizeBytes);
            row.Detail = $"{items.Count} item(s) - {CleanTarget.FormatSize(total)}";
        }
        StatusText.Text = $"Scan complete: {items.Count} item(s) found";
        if (_current == tag) ResultsGrid.ItemsSource = items;
        if (_current == "Dashboard") UpdateDashboard();
        UpdateFooter();
        SetBusy(false);
    }

    private static string titleOf(string tag) => Sections.First(s => s.Tag == tag).Title;

    private static List<CleanTarget> ScanSection(string tag, Action<int, string> progress) => tag switch
    {
        "Dev" => Scanner.ScanDevDependencies(progress),
        "Cache" => Scanner.ScanDevCaches(progress),
        "Temp" => Scanner.ScanTemp(progress),
        "Recycle" => Scanner.ScanRecycleBin(progress),
        "Downloads" => Scanner.ScanDownloads(progress),
        "System" => Scanner.ScanSystemFiles(progress),
        "Large" => Scanner.ScanLargeFiles(progress),
        _ => new List<CleanTarget>()
    };

    private async void Clean_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _current == "Dashboard") return;
        if (!_results.TryGetValue(_current, out var list)) return;
        var selected = list.Where(t => t.IsSelected).ToList();
        if (selected.Count == 0)
        {
            MessageBox.Show(this, "Nothing selected. Tick the items you want to clean first.",
                "UNID Advanced PC Cleaner", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        long total = selected.Sum(t => t.SizeBytes);
        var confirm = MessageBox.Show(this,
            $"Delete {selected.Count} item(s) totalling {CleanTarget.FormatSize(total)}?\n\n" +
            "Installed tools and software are NEVER touched.",
            "Confirm cleaning", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        SetBusy(true);
        ScanProgress.Value = 0;
        IProgress<(string, int)> progress = new Progress<(string, int)>(v =>
        {
            ScanProgress.Value = v.Item2;
            StatusText.Text = v.Item1;
        });

        var copy = selected.ToList();
        var summary = await Task.Run(() => Cleaner.CleanAll(copy, (l, p) => progress.Report((l, p))));

        StatusText.Text = "Cleaning complete";
        MessageBox.Show(this,
            $"Freed: {CleanTarget.FormatSize(summary.FreedBytes)}\n" +
            $"Failed / locked: {summary.FailedCount} item(s)\n\n" +
            "Details are shown in the list below.",
            "Cleaning finished", MessageBoxButton.OK,
            summary.FailedCount > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);

        await RunScan(_current);
        SetBusy(false);
    }

    private void SelectAllChk_Changed(object sender, RoutedEventArgs e)
    {
        if (_current == "Dashboard" || !_results.TryGetValue(_current, out var list)) return;
        bool value = SelectAllChk.IsChecked == true;
        foreach (var t in list) t.IsSelected = value;
        ResultsGrid.ItemsSource = null;
        ResultsGrid.ItemsSource = list;
        UpdateFooter();
    }

    private void UpdateFooter()
    {
        if (_current == "Dashboard" || !_results.TryGetValue(_current, out var list))
        {
            SelectedInfo.Text = "Selected: 0 items - 0 B";
            return;
        }
        var sel = list.Where(t => t.IsSelected).ToList();
        long sum = sel.Sum(t => t.SizeBytes);
        SelectedInfo.Text = $"Selected: {sel.Count} item(s) - {CleanTarget.FormatSize(sum)}";
        SelectAllChk.IsChecked = list.Count > 0 && sel.Count == list.Count;
    }

    private void UpdateDashboard()
    {
        long grand = 0;
        int count = 0;
        foreach (var s in Sections)
        {
            if (_results.TryGetValue(s.Tag, out var list))
            {
                grand += list.Sum(t => t.SizeBytes);
                count += list.Count;
            }
        }
        DashTotal.Text = count == 0
            ? "No sections scanned yet."
            : $"Total reclaimable across all sections: {CleanTarget.FormatSize(grand)} ({count} item(s))";
    }

    private void DriveChk_Changed(object sender, RoutedEventArgs e)
    {
        var chk = (CheckBox)sender;
        var letter = (string)chk.Content;
        letter = letter.TrimEnd(':').ToUpperInvariant();
        if (chk.IsChecked == true) ScanOptions.SelectedDrives.Add(letter);
        else ScanOptions.SelectedDrives.Remove(letter);
        SyncAllDrivesChk();
        _ = RefreshForDrives();
    }

    private void AllDrivesChk_Changed(object sender, RoutedEventArgs e)
    {
        bool all = AllDrivesChk.IsChecked == true;
        foreach (var c in DrivePanel.Children.OfType<CheckBox>())
        {
            c.Checked -= DriveChk_Changed;
            c.Unchecked -= DriveChk_Changed;
            c.IsChecked = all;
            c.Checked += DriveChk_Changed;
            c.Unchecked += DriveChk_Changed;
        }
        if (all)
        {
            ScanOptions.SelectedDrives.Clear();
            foreach (var c in DrivePanel.Children.OfType<CheckBox>())
                ScanOptions.SelectedDrives.Add(c.Content.ToString()!.TrimEnd(':').ToUpperInvariant());
        }
        else
        {
            ScanOptions.SelectedDrives.Clear();
        }
        _ = RefreshForDrives();
    }

    private void SyncAllDrivesChk()
    {
        int total = DrivePanel.Children.Count;
        AllDrivesChk.Checked -= AllDrivesChk_Changed;
        AllDrivesChk.Unchecked -= AllDrivesChk_Changed;
        AllDrivesChk.IsChecked = total > 0 && ScanOptions.SelectedDrives.Count == total;
        AllDrivesChk.Checked += AllDrivesChk_Changed;
        AllDrivesChk.Unchecked += AllDrivesChk_Changed;
    }

    private async Task RefreshForDrives()
    {
        if (_busy || _current == "Dashboard") return;
        if (!_results.ContainsKey(_current)) return;
        await RunScan(_current);
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        ScanBtn.IsEnabled = !busy;
        ScanAllBtn.IsEnabled = !busy;
        CleanBtn.IsEnabled = !busy;
        SectionNav.IsEnabled = !busy;
        SelectAllChk.IsEnabled = !busy;
    }
}