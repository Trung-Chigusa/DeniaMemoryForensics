using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using DeniaMemoryForensics.Models;
using DeniaMemoryForensics.Services;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace DeniaMemoryForensics;

public partial class MainWindow : Window
{
    private readonly VolatilityRunner _volatility = new();
    private readonly ImageCarver _imageCarver = new();
    private readonly VirusTotalService _virusTotal = new();
    private readonly DeniaSettings _settings;
    private CancellationTokenSource? _runningTask;
    private string _lastImageFolder = "";

    public MainWindow()
    {
        InitializeComponent();
        _settings = AppPaths.LoadSettings();
        ApplySettingsToUi();
        UpdateCaseChrome();
    }

    private void ApplySettingsToUi()
    {
        EnginePathBox.Text = _settings.VolatilityEnginePath;
        OutputRootBox.Text = string.IsNullOrWhiteSpace(_settings.OutputRoot) ? AppPaths.DefaultOutputRoot : _settings.OutputRoot;
        DumpPathBoxSet(_settings.LastDumpPath);
        VirusApiKeyBox.Password = _settings.VirusTotalApiKey;
        VirusTargetBox.Text = OutputRootBox.Text;
        DashboardOutputText.Text = OutputRootBox.Text;
    }

    private void Navigate_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.RadioButton radio || radio.Tag is not string target)
        {
            return;
        }

        OverviewView.Visibility = target == "Overview" ? Visibility.Visible : Visibility.Collapsed;
        ConsoleView.Visibility = target == "Console" ? Visibility.Visible : Visibility.Collapsed;
        TreeView.Visibility = target == "Tree" ? Visibility.Visible : Visibility.Collapsed;
        ImagesView.Visibility = target == "Images" ? Visibility.Visible : Visibility.Collapsed;
        VirusView.Visibility = target == "Virus" ? Visibility.Visible : Visibility.Collapsed;
        SettingsView.Visibility = target == "Settings" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Window_DragOver(object sender, System.Windows.DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop) ? System.Windows.DragDropEffects.Copy : System.Windows.DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
        {
            return;
        }

        var files = (string[])e.Data.GetData(System.Windows.DataFormats.FileDrop);
        if (files.Length > 0)
        {
            DumpPathBoxSet(files[0]);
            SaveSettingsFromUi();
        }
    }

    private void SelectDump_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select memory dump",
            Filter = "Memory dumps|*.raw;*.vmem;*.mem;*.dmp;*.dump;*.lime;*.hpak|All files|*.*"
        };

        if (dialog.ShowDialog() == true)
        {
            DumpPathBoxSet(dialog.FileName);
            SaveSettingsFromUi();
        }
    }

    private async void RunStatus_Click(object sender, RoutedEventArgs e)
    {
        await RunOperationAsync("Checking engine status", async token =>
        {
            ConsoleOutputBox.Clear();
            await _volatility.RunStatusAsync(EnginePathBox.Text.Trim(), AppendConsole, token);
        });
    }

    private async void RunCommand_Click(object sender, RoutedEventArgs e)
    {
        await RunBattleCommandFromBoxAsync(GetCommandText());
    }

    private async void QuickInfo_Click(object sender, RoutedEventArgs e) => await RunBattleCommandFromBoxAsync("info --limit 20");

    private async void QuickProcesses_Click(object sender, RoutedEventArgs e) => await RunBattleCommandFromBoxAsync("ps --limit 80");

    private async void QuickTree_Click(object sender, RoutedEventArgs e)
    {
        NavigateTo("Tree");
        await RenderTreeAsync();
    }

    private async Task RunBattleCommandFromBoxAsync(string command)
    {
        CommandBox.Text = command;
        NavigateTo("Console");
        await RunOperationAsync($"Running {command}", async token =>
        {
            ConsoleOutputBox.Clear();
            AppendConsole($"> {command}");
            await _volatility.RunBattleCommandAsync(EnginePathBox.Text.Trim(), _settings.LastDumpPath, command, AppendConsole, token);
        });
    }

    private void ClearConsole_Click(object sender, RoutedEventArgs e) => ConsoleOutputBox.Clear();

    private async void RenderTree_Click(object sender, RoutedEventArgs e) => await RenderTreeAsync();

    private async Task RenderTreeAsync()
    {
        var source = SelectedText(TreeSourceBox);
        var filter = TreeFilterBox.Text.Trim();
        var limit = SafeInt(TreeLimitBox.Text, 500);
        var depth = SafeInt(TreeDepthBox.Text, 0);
        var command = $"tree --source {source} --limit {limit} --depth {depth}";
        if (!string.IsNullOrWhiteSpace(filter))
        {
            command += $" --filter \"{filter}\"";
        }

        await RunOperationAsync("Rendering file tree", async token =>
        {
            TreeOutputBox.Clear();
            AppendTree($"> {command}");
            await _volatility.RunBattleCommandAsync(EnginePathBox.Text.Trim(), _settings.LastDumpPath, command, AppendTree, token);
        });
    }

    private void SaveTree_Click(object sender, RoutedEventArgs e)
    {
        var outputPath = Path.Combine(GetOutputRoot(), $"file_tree_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllText(outputPath, TreeOutputBox.Text);
        SetStatus($"Tree saved: {outputPath}");
    }

    private async void NativeCarve_Click(object sender, RoutedEventArgs e)
    {
        NavigateTo("Images");
        await RunOperationAsync("Native image carving", async token =>
        {
            ImageOutputBox.Clear();
            var folder = PrepareImageFolder();
            var carveFolder = Path.Combine(folder, "carved");
            var options = new CarveOptions(
                ImageExtensionsBox.Text,
                ParseBytes(ImageMinBox.Text, 256),
                ParseBytes(ImageMaxBox.Text, 32 * 1024 * 1024),
                SafeInt(ImageLimitBox.Text, 0),
                ImageValidateBox.IsChecked == true);

            AppendImage($"Native C# carve output: {carveFolder}");
            var files = await _imageCarver.CarveAsync(_settings.LastDumpPath, carveFolder, options, AppendImage, token);
            _lastImageFolder = folder;
            VirusTargetBox.Text = carveFolder;
            AppendImage($"Completed. Carved {files.Count} files.");
            OpenFolder(folder);
        });
    }

    private async void VolDumpImages_Click(object sender, RoutedEventArgs e)
    {
        var outputName = ResolveImageOutputName();
        var command = $"dump-images --mode both --output \"{outputName}\" --extensions \"{ImageExtensionsBox.Text}\" --min {ImageMinBox.Text} --max {ImageMaxBox.Text} --limit {ImageLimitBox.Text}";
        if (ImageValidateBox.IsChecked == true)
        {
            command += " --validate";
        }

        NavigateTo("Console");
        await RunBattleCommandFromBoxAsync(command);
    }

    private void OpenImagesFolder_Click(object sender, RoutedEventArgs e)
    {
        var target = string.IsNullOrWhiteSpace(_lastImageFolder) ? GetOutputRoot() : _lastImageFolder;
        OpenFolder(target);
    }

    private void BrowseEngine_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select Volatility/Battle engine",
            Filter = "Executable or Python script|*.exe;*.py|All files|*.*"
        };

        if (dialog.ShowDialog() == true)
        {
            EnginePathBox.Text = dialog.FileName;
            SaveSettingsFromUi();
        }
    }

    private void BrowseOutputRoot_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "Select output folder",
            UseDescriptionForTitle = true
        };

        if (dialog.ShowDialog() == Forms.DialogResult.OK)
        {
            OutputRootBox.Text = dialog.SelectedPath;
            SaveSettingsFromUi();
        }
    }

    private void AutoDetectEngine_Click(object sender, RoutedEventArgs e)
    {
        var path = AppPaths.DetectVolatilityEngine();
        EnginePathBox.Text = path;
        SaveSettingsFromUi();
        SetStatus(string.IsNullOrWhiteSpace(path) ? "Engine not found automatically." : $"Engine detected: {path}");
    }

    private void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        SaveSettingsFromUi();
        SetStatus("Settings saved.");
    }

    private void OpenOutput_Click(object sender, RoutedEventArgs e) => OpenFolder(GetOutputRoot());

    private void GetVirusKey_Click(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "https://www.virustotal.com/gui/my-apikey",
            UseShellExecute = true
        });
    }

    private void SaveVirusKey_Click(object sender, RoutedEventArgs e)
    {
        SaveSettingsFromUi();
        SetStatus("VirusTotal API key saved locally.");
    }

    private void BrowseVirusFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select file for VirusTotal hash lookup",
            Filter = "All files|*.*"
        };

        if (dialog.ShowDialog() == true)
        {
            VirusTargetBox.Text = dialog.FileName;
        }
    }

    private void BrowseVirusFolder_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "Select folder for VirusTotal hash lookup",
            UseDescriptionForTitle = true
        };

        if (dialog.ShowDialog() == Forms.DialogResult.OK)
        {
            VirusTargetBox.Text = dialog.SelectedPath;
        }
    }

    private async void CheckVirus_Click(object sender, RoutedEventArgs e)
    {
        await RunOperationAsync("Checking VirusTotal hashes", async token =>
        {
            VirusResultsList.Items.Clear();
            var files = VirusTotalService.CollectFiles(
                VirusTargetBox.Text.Trim(),
                VirusExecutableOnlyBox.IsChecked == true,
                SafeInt(VirusLimitBox.Text, 200));

            if (files.Count == 0)
            {
                SetStatus("No files found for VirusTotal lookup.");
                return;
            }

            for (var i = 0; i < files.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                SetStatus($"VirusTotal {i + 1}/{files.Count}: {Path.GetFileName(files[i])}");
                var result = await _virusTotal.CheckFileAsync(files[i], VirusApiKeyBox.Password.Trim(), token);
                Dispatcher.Invoke(() => VirusResultsList.Items.Add(result));

                if (i < files.Count - 1)
                {
                    await Task.Delay(1600, token);
                }
            }
        });
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _runningTask?.Cancel();
        SetStatus("Cancellation requested.");
    }

    private async Task RunOperationAsync(string status, Func<CancellationToken, Task> operation)
    {
        if (_runningTask is not null)
        {
            SetStatus("Another task is already running.");
            return;
        }

        _runningTask = new CancellationTokenSource();
        CancelButton.IsEnabled = true;
        SetStatus(status);

        try
        {
            SaveSettingsFromUi();
            await operation(_runningTask.Token);
            SetStatus("Ready");
        }
        catch (OperationCanceledException)
        {
            SetStatus("Cancelled");
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message);
            System.Windows.MessageBox.Show(this, ex.Message, "Denia Memory Forensics", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _runningTask.Dispose();
            _runningTask = null;
            CancelButton.IsEnabled = false;
        }
    }

    private void SaveSettingsFromUi()
    {
        _settings.VolatilityEnginePath = EnginePathBox.Text.Trim();
        _settings.OutputRoot = GetOutputRoot();
        _settings.VirusTotalApiKey = VirusApiKeyBox.Password.Trim();
        AppPaths.SaveSettings(_settings);
        UpdateCaseChrome();
    }

    private void DumpPathBoxSet(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            _settings.LastDumpPath = "";
            return;
        }

        _settings.LastDumpPath = path;
        UpdateCaseChrome();
    }

    private void UpdateCaseChrome()
    {
        var dump = string.IsNullOrWhiteSpace(_settings.LastDumpPath) ? "No dump selected" : _settings.LastDumpPath;
        SidebarDumpText.Text = string.IsNullOrWhiteSpace(_settings.LastDumpPath) ? "No dump selected" : Path.GetFileName(_settings.LastDumpPath);
        DashboardDumpText.Text = dump;
        DashboardEngineText.Text = string.IsNullOrWhiteSpace(EnginePathBox.Text) ? "Not configured" : EnginePathBox.Text;
        DashboardOutputText.Text = GetOutputRoot();
    }

    private void NavigateTo(string target)
    {
        foreach (var radio in FindVisualChildren<System.Windows.Controls.RadioButton>(this))
        {
            if (radio.Tag as string == target)
            {
                radio.IsChecked = true;
                return;
            }
        }
    }

    private string GetCommandText() => string.IsNullOrWhiteSpace(CommandBox.Text) ? "info --limit 20" : CommandBox.Text.Trim();

    private static string SelectedText(System.Windows.Controls.ComboBox combo)
    {
        return (combo.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content?.ToString() ?? combo.Text;
    }

    private string GetOutputRoot()
    {
        var root = OutputRootBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(root))
        {
            root = AppPaths.DefaultOutputRoot;
            OutputRootBox.Text = root;
        }

        Directory.CreateDirectory(root);
        return root;
    }

    private string PrepareImageFolder()
    {
        var folder = Path.Combine(GetOutputRoot(), ResolveImageOutputName());
        Directory.CreateDirectory(folder);
        _lastImageFolder = folder;
        return folder;
    }

    private string ResolveImageOutputName()
    {
        var name = ImageOutputNameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name) || name.Equals("images_auto", StringComparison.OrdinalIgnoreCase))
        {
            name = $"images_{DateTime.Now:yyyyMMdd_HHmmss}";
            ImageOutputNameBox.Text = name;
        }

        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(invalid, '_');
        }

        return name;
    }

    private void AppendConsole(string line) => AppendText(ConsoleOutputBox, line);

    private void AppendTree(string line) => AppendText(TreeOutputBox, line);

    private void AppendImage(string line) => AppendText(ImageOutputBox, line);

    private void AppendText(System.Windows.Controls.TextBox box, string line)
    {
        Dispatcher.Invoke(() =>
        {
            box.AppendText(line + Environment.NewLine);
            box.ScrollToEnd();
        });
    }

    private void SetStatus(string text)
    {
        Dispatcher.Invoke(() => StatusText.Text = text);
    }

    private static int SafeInt(string text, int fallback)
    {
        return int.TryParse(text.Trim(), out var value) ? value : fallback;
    }

    private static long ParseBytes(string text, long fallback)
    {
        text = text.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }

        var multiplier = 1L;
        var suffix = char.ToUpperInvariant(text[^1]);
        if (suffix is 'K' or 'M' or 'G')
        {
            multiplier = suffix switch
            {
                'K' => 1024L,
                'M' => 1024L * 1024L,
                'G' => 1024L * 1024L * 1024L,
                _ => 1L
            };
            text = text[..^1];
        }

        return long.TryParse(text, out var value) ? value * multiplier : fallback;
    }

    private static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"\"{path}\"",
            UseShellExecute = true
        });
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is null)
        {
            yield break;
        }

        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T typed)
            {
                yield return typed;
            }

            foreach (var nested in FindVisualChildren<T>(child))
            {
                yield return nested;
            }
        }
    }
}
