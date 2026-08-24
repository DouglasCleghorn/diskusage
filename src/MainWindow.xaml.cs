using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using DiskUsage.Core;
using diskusage.ViewModels;
using Microsoft.Win32;

namespace diskusage;

public partial class MainWindow : Window
{
    private static readonly TimeSpan LiveRefreshInterval = TimeSpan.FromMilliseconds(1250);
    private readonly FileSystemScanner _scanner = new();
    private readonly BulkObservableCollection<UsageItemViewModel> _rows = [];
    private readonly ICollectionView _rowsView;
    private CancellationTokenSource? _scanCancellation;
    private CancellationTokenSource? _exportCancellation;
    private ScanResult? _scanResult;
    private UsageNode? _activeScanRoot;
    private UsageNode? _currentDirectory;
    private bool _showingDrives;
    private DateTime _deferLiveRefreshUntilUtc;
    private ScanUiState _scanState = ScanUiState.Ready;

    public MainWindow()
    {
        InitializeComponent();
        UsageGrid.ItemsSource = _rows;
        _rowsView = CollectionViewSource.GetDefaultView(_rows);
        _rowsView.Filter = FilterRow;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        PathBox.Text = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        ShowDrives();
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        _scanCancellation?.Cancel();
        _exportCancellation?.Cancel();
    }

    private async void Scan_Click(object sender, RoutedEventArgs e) => await ScanPathAsync();

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _scanCancellation?.Cancel();
        _exportCancellation?.Cancel();
    }

    private async void Save_Click(object sender, RoutedEventArgs e) => await SaveCurrentDirectoryAsync();

    private async void PathBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await ScanPathAsync();
        }
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose a folder to scan",
            Multiselect = false
        };

        if (Directory.Exists(PathBox.Text))
        {
            dialog.InitialDirectory = PathBox.Text;
        }

        if (dialog.ShowDialog(this) == true)
        {
            PathBox.Text = dialog.FolderName;
            _ = ScanPathAsync();
        }
    }

    private async Task ScanPathAsync()
    {
        var path = PathBox.Text.Trim();
        if (!Directory.Exists(path))
        {
            MessageBox.Show(this, $"Directory not found:\n{path}", "diskusage", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _scanCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _scanCancellation = cancellation;
        _activeScanRoot = null;
        _currentDirectory = null;
        _scanState = ScanUiState.Updating;
        SetScanningState(true);
        UpdateScanStateBadge();
        _rows.Clear();
        CurrentPathText.Text = Path.GetFullPath(path);
        SummaryText.Text = string.Empty;
        StatusText.Text = "Starting scan…";

        ScanProgress? latestProgress = null;
        var progress = new InlineProgress<ScanProgress>(value => Volatile.Write(ref latestProgress, value));

        void ApplyLatestProgress()
        {
            if (!ReferenceEquals(_scanCancellation, cancellation))
            {
                return;
            }

            var value = Volatile.Read(ref latestProgress);
            if (value is null)
            {
                return;
            }

            if (value.Root is not null && _activeScanRoot is null)
            {
                _activeScanRoot = value.Root;
                ShowDirectory(value.Root);
            }

            StatusText.Text = $"Updating · {value.Files:N0} files · {SizeFormatter.Format(value.Bytes)} discovered · {value.Skipped:N0} skipped";
            if (DateTime.UtcNow >= _deferLiveRefreshUntilUtc)
            {
                RefreshCurrentDirectoryRows(preserveSelection: true);
            }
        }

        try
        {
            var scanTask = _scanner.ScanAsync(
                path,
                new ScanOptions { IncludeHidden = true, FollowLinks = false, CollectFiles = true },
                progress,
                cancellation.Token);

            var firstRefresh = true;
            while (!scanTask.IsCompleted)
            {
                var delay = Task.Delay(firstRefresh ? TimeSpan.FromMilliseconds(25) : LiveRefreshInterval);
                await Task.WhenAny(scanTask, delay);
                ApplyLatestProgress();
                firstRefresh = false;
            }

            _scanResult = await scanTask;
            _currentDirectory ??= _scanResult.Root;
            _scanState = ScanUiState.Final;
            UpdateScanStateBadge();
            RefreshCurrentDirectoryRows(preserveSelection: true);
            StatusText.Text = $"Final · completed in {_scanResult.Elapsed.TotalSeconds:N1}s · {_scanResult.Skipped:N0} inaccessible entries skipped";
        }
        catch (OperationCanceledException)
        {
            if (_activeScanRoot is not null)
            {
                _scanState = ScanUiState.Canceled;
                UpdateScanStateBadge();
                RefreshCurrentDirectoryRows(preserveSelection: true);
                StatusText.Text = "Canceled · showing partial results";
            }
            else
            {
                ShowDrives();
            }
        }
        catch (Exception exception)
        {
            _scanState = ScanUiState.Canceled;
            UpdateScanStateBadge();
            StatusText.Text = "Scan failed";
            MessageBox.Show(this, exception.Message, "Scan failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            if (ReferenceEquals(_scanCancellation, cancellation))
            {
                _scanCancellation.Dispose();
                _scanCancellation = null;
                SetScanningState(false);
            }
        }
    }

    private void ShowDirectory(UsageNode directory)
    {
        _showingDrives = false;
        _currentDirectory = directory;
        CurrentPathText.Text = directory.FullPath;
        UpButton.IsEnabled = true;
        FilterBox.Clear();

        RefreshCurrentDirectoryRows(preserveSelection: false);
        UpdateSaveButtonState();
    }

    private async Task SaveCurrentDirectoryAsync()
    {
        if (_scanState != ScanUiState.Final ||
            _scanResult is null ||
            _currentDirectory is not { } directory)
        {
            return;
        }

        var folderName = Path.GetFileName(directory.FullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(folderName))
        {
            folderName = directory.FullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Replace(":", string.Empty);
        }

        var dialog = new SaveFileDialog
        {
            Title = $"Save inventory for {directory.FullPath}",
            FileName = $"diskusage-{folderName}-{DateTime.Now:yyyyMMdd-HHmmss}",
            AddExtension = false,
            OverwritePrompt = false,
            Filter = "Parquet inventory (*.parquet)|*.parquet|CSV (*.csv)|*.csv|CSV · Brotli (*.csv.br)|*.csv.br|CSV · gzip (*.csv.gz)|*.csv.gz|CSV · ZIP (*.csv.zip)|*.csv.zip|TSV (*.tsv)|*.tsv|TSV · Brotli (*.tsv.br)|*.tsv.br|TSV · gzip (*.tsv.gz)|*.tsv.gz|TSV · ZIP (*.tsv.zip)|*.tsv.zip"
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var format = GetExportFormat(dialog.FileName, dialog.FilterIndex);
        var outputPath = ExportFormatParser.EnsureFileExtension(dialog.FileName, format);
        if (File.Exists(outputPath) &&
            MessageBox.Show(
                this,
                $"{outputPath} already exists. Replace it?",
                "Confirm Save As",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return;
        }

        int? compressionLevel = null;
        var compressionLevelText = CompressionLevelBox.Text.Trim();
        if (compressionLevelText.Length > 0 && (!int.TryParse(compressionLevelText, out var parsedLevel) || parsedLevel < 0))
        {
            MessageBox.Show(this, "Compression level must be a non-negative integer.", "Invalid compression level", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (compressionLevelText.Length > 0)
        {
            compressionLevel = int.Parse(compressionLevelText);
        }

        try
        {
            format.ValidateCompressionLevel(compressionLevel);
        }
        catch (ArgumentException exception)
        {
            MessageBox.Show(this, exception.Message, "Invalid compression level", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var previousState = _scanState;
        var cancellation = new CancellationTokenSource();
        _exportCancellation = cancellation;
        _scanState = ScanUiState.Saving;
        SetScanningState(true);
        UpdateScanStateBadge();
        var levelText = compressionLevel is null ? string.Empty : $" at level {compressionLevel}";
        StatusText.Text = $"Saving {format.Extension()} inventory{levelText}…";

        var progress = new Progress<ScanProgress>(value =>
        {
            if (ReferenceEquals(_exportCancellation, cancellation))
            {
                StatusText.Text = $"Saving · {value.Files:N0} files · {SizeFormatter.Format(value.Bytes)}";
            }
        });

        try
        {
            var exporter = new FileListExporter();
            var result = await Task.Run(
                () => exporter.ExportRecordsAsync(
                    EnumerateCachedFiles(directory),
                    outputPath,
                    format,
                    progress,
                    cancellation.Token,
                    compressionLevel),
                cancellation.Token);
            _scanState = previousState;
            UpdateScanStateBadge();
            StatusText.Text = $"Saved {result.Files:N0} files ({SizeFormatter.Format(result.Bytes)}) to {result.OutputPath}";
        }
        catch (OperationCanceledException)
        {
            _scanState = previousState;
            UpdateScanStateBadge();
            StatusText.Text = "Save canceled";
        }
        catch (Exception exception)
        {
            _scanState = previousState;
            UpdateScanStateBadge();
            StatusText.Text = "Save failed";
            MessageBox.Show(this, exception.Message, "Save failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            if (ReferenceEquals(_exportCancellation, cancellation))
            {
                _exportCancellation.Dispose();
                _exportCancellation = null;
                SetScanningState(false);
            }
        }
    }

    private static IEnumerable<FileRecord> EnumerateCachedFiles(UsageNode root)
    {
        var pending = new Stack<UsageNode>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            foreach (var file in directory.Files)
            {
                yield return file;
            }

            var children = directory.Children;
            for (var index = children.Count - 1; index >= 0; index--)
            {
                pending.Push(children[index]);
            }
        }
    }

    private static ExportFormat GetExportFormat(string fileName, int filterIndex)
    {
        if (ExportFormatParser.TryParseFileName(fileName, out var parsed))
        {
            return parsed;
        }

        return filterIndex switch
        {
            2 => ExportFormat.Csv,
            3 => ExportFormat.CsvBrotli,
            4 => ExportFormat.CsvGzip,
            5 => ExportFormat.CsvZip,
            6 => ExportFormat.Tsv,
            7 => ExportFormat.TsvBrotli,
            8 => ExportFormat.TsvGzip,
            9 => ExportFormat.TsvZip,
            _ => ExportFormat.Parquet
        };
    }

    private void RefreshCurrentDirectoryRows(bool preserveSelection)
    {
        if (_currentDirectory is not { } directory)
        {
            return;
        }

        var selectedPath = preserveSelection
            ? (UsageGrid.SelectedItem as UsageItemViewModel)?.FullPath
            : null;

        var parentSize = directory.TotalSize;
        var rows = directory.Children
            .Select(child => UsageItemViewModel.FromDirectory(child, parentSize));

        // Direct-file lists can be enormous. While scanning, keep the live view focused on
        // folders and totals; materialize and sort the complete file list once at final.
        if (_scanState != ScanUiState.Updating)
        {
            rows = rows.Concat(directory.Files.Select(file => UsageItemViewModel.FromFile(file, parentSize)));
        }

        var sortedRows = rows
            .OrderByDescending(row => row.Size)
            .ToArray();

        var scrollViewer = FindVisualChild<ScrollViewer>(UsageGrid);
        var verticalOffset = scrollViewer?.VerticalOffset ?? 0;

        _rows.ReplaceAll(sortedRows);

        SummaryText.Text = $"{SizeFormatter.Format(directory.TotalSize)} · {directory.FileCount:N0} files · {directory.DirectoryCount:N0} folders";
        if (selectedPath is not null)
        {
            UsageGrid.SelectedItem = _rows.FirstOrDefault(row => row.FullPath == selectedPath);
        }

        if (preserveSelection && scrollViewer is not null)
        {
            scrollViewer.ScrollToVerticalOffset(verticalOffset);
        }
    }

    private void UsageGrid_PreviewMouseInput(object sender, MouseEventArgs e) =>
        _deferLiveRefreshUntilUtc = DateTime.UtcNow.AddMilliseconds(900);

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
            {
                return match;
            }

            if (FindVisualChild<T>(child) is { } descendant)
            {
                return descendant;
            }
        }

        return null;
    }

    private void Up_Click(object sender, RoutedEventArgs e)
    {
        if (_currentDirectory?.Parent is not null)
        {
            ShowDirectory(_currentDirectory.Parent);
        }
        else if (!_showingDrives)
        {
            ShowDrives();
        }
    }

    private void FilterBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => _rowsView.Refresh();

    private bool FilterRow(object value)
    {
        if (value is not UsageItemViewModel row)
        {
            return false;
        }

        var filter = FilterBox.Text.Trim();
        return filter.Length == 0 || row.Name.Contains(filter, StringComparison.OrdinalIgnoreCase);
    }

    private async void UsageGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) => await OpenSelectedAsync();

    private async void UsageGrid_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await OpenSelectedAsync();
        }
        else if (e.Key == Key.Back && !_showingDrives)
        {
            e.Handled = true;
            if (_currentDirectory?.Parent is not null)
            {
                ShowDirectory(_currentDirectory.Parent);
            }
            else
            {
                ShowDrives();
            }
        }
    }

    private async void Open_Click(object sender, RoutedEventArgs e) => await OpenSelectedAsync();

    private async Task OpenSelectedAsync()
    {
        if (UsageGrid.SelectedItem is not UsageItemViewModel selected)
        {
            return;
        }

        if (selected.Drive is not null)
        {
            PathBox.Text = selected.Drive.RootDirectory.FullName;
            await ScanPathAsync();
            return;
        }

        if (selected.Directory is not null)
        {
            ShowDirectory(selected.Directory);
            return;
        }

        Process.Start(new ProcessStartInfo(selected.FullPath) { UseShellExecute = true });
    }

    private void ShowDrives()
    {
        _scanState = ScanUiState.Ready;
        _activeScanRoot = null;
        _showingDrives = true;
        _currentDirectory = null;
        CurrentPathText.Text = "This PC";
        UpButton.IsEnabled = false;
        FilterBox.Clear();

        var availableDrives = new List<UsageItemViewModel>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.IsReady)
                {
                    availableDrives.Add(UsageItemViewModel.FromDrive(drive));
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Unavailable removable and network drives are omitted from the instant overview.
            }
        }

        var drives = availableDrives
            .OrderBy(row => row.FullPath)
            .ToArray();

        _rows.ReplaceAll(drives);

        var used = drives.Sum(drive => drive.Size);
        SummaryText.Text = $"{drives.Length:N0} drives · {SizeFormatter.Format(used)} used";
        StatusText.Text = "Select a drive to scan, or enter a folder path";
        UpdateScanStateBadge();
        UpdateSaveButtonState();
        if (_rows.Count > 0)
        {
            UsageGrid.SelectedIndex = 0;
        }
    }

    private void ShowInExplorer_Click(object sender, RoutedEventArgs e)
    {
        if (UsageGrid.SelectedItem is not UsageItemViewModel selected)
        {
            return;
        }

        var arguments = selected.IsDirectory
            ? $"\"{selected.FullPath}\""
            : $"/select,\"{selected.FullPath}\"";
        Process.Start("explorer.exe", arguments);
    }

    private void CopyPath_Click(object sender, RoutedEventArgs e)
    {
        if (UsageGrid.SelectedItem is UsageItemViewModel selected)
        {
            Clipboard.SetText(selected.FullPath);
        }
    }

    private void SetScanningState(bool scanning)
    {
        ScanButton.IsEnabled = !scanning;
        CancelButton.IsEnabled = scanning;
        PathBox.IsEnabled = !scanning;
        CompressionLevelBox.IsEnabled = !scanning;
        UsageGrid.IsEnabled = true;
        UpdateSaveButtonState();
    }

    private void UpdateSaveButtonState()
    {
        SaveButton.IsEnabled =
            _scanCancellation is null &&
            _exportCancellation is null &&
            _scanState == ScanUiState.Final &&
            _scanResult is not null &&
            !_showingDrives &&
            _currentDirectory is not null;
        SaveButton.ToolTip = SaveButton.IsEnabled
            ? "Save the completed in-memory scan without rescanning the filesystem"
            : "Save requires a completed scan";
    }

    private void UpdateScanStateBadge()
    {
        var (text, background, foreground) = _scanState switch
        {
            ScanUiState.Updating => ("UPDATING", "#FEF0C7", "#93370D"),
            ScanUiState.Saving => ("SAVING", "#D1E9FF", "#175CD3"),
            ScanUiState.Final => ("FINAL", "#D1FADF", "#027A48"),
            ScanUiState.Canceled => ("CANCELED", "#FEE4E2", "#B42318"),
            _ => ("READY", "#E4E7EC", "#475467")
        };

        ScanStateText.Text = text;
        ScanStateBadge.Background = (Brush)new BrushConverter().ConvertFromString(background)!;
        ScanStateText.Foreground = (Brush)new BrushConverter().ConvertFromString(foreground)!;
    }

    private enum ScanUiState
    {
        Ready,
        Updating,
        Saving,
        Final,
        Canceled
    }

    private sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
