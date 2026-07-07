using System.Collections.ObjectModel;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;
using FileRenamer.Models;
using FileRenamer.Services;

namespace FileRenamer;

public sealed partial class MainWindow : Window
{
    private readonly ObservableCollection<RenameItem> _items = new();
    private readonly List<RenameItem> _allLoadedItems = new();
    private readonly AiService _ai = new();
    private CancellationTokenSource? _cts;
    private string? _libraryRootPath;

    public MainWindow()
    {
        try
        {
            InitializeComponent();
            ItemsList.ItemsSource = _items;
            _items.CollectionChanged += OnItemsCollectionChanged;
            RefreshUiState();

            // Load saved settings
            var settings = SettingsService.LoadSettings();
            ApplySettingsToService(settings);
            LoadSettingsToUi(settings);

            AiService.RateLimitUpdated += OnRateLimitUpdated;

            _ = LoadModelsAsync(GetCurrentProviderModel(settings));
            
            // Safely set selection here after InitializeComponent is fully completed
            FileTypeCombo.SelectedIndex = 0;
            MainNavView.SelectedItem = MainNavView.MenuItems[0];

            SetupTitleBar();
            Activated += MainWindow_Activated;
            
            // Initialize Mica Alt backdrop
            TrySetMicaBackdrop();
        }
        catch (Exception ex)
        {
            System.IO.File.WriteAllText("crash.log", ex.ToString() + "\n" + ex.StackTrace);
            throw;
        }
    }

    private void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
    }

    private void AppTitleBar_PaneToggleRequested(TitleBar sender, object args)
    {
        MainNavView.IsPaneOpen = !MainNavView.IsPaneOpen;
    }

    private void SetupTitleBar()
    {
        try
        {
            if (AppWindowTitleBar.IsCustomizationSupported())
            {
                ExtendsContentIntoTitleBar = true;
                SetTitleBar(AppTitleBar);

                var appWindow = GetAppWindow();
                if (appWindow != null)
                {
                    // Style the caption buttons to match dark theme
                    var tb = appWindow.TitleBar;
                    if (tb != null)
                    {
                        tb.ButtonBackgroundColor         = Microsoft.UI.Colors.Transparent;
                        tb.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
                        tb.ButtonForegroundColor         = Microsoft.UI.Colors.White;
                        tb.ButtonInactiveForegroundColor = Windows.UI.Color.FromArgb(255, 150, 150, 150);
                        tb.ButtonHoverBackgroundColor    = Windows.UI.Color.FromArgb(60,  255, 255, 255);
                        tb.ButtonHoverForegroundColor    = Microsoft.UI.Colors.White;
                        tb.ButtonPressedBackgroundColor  = Windows.UI.Color.FromArgb(30,  255, 255, 255);
                        tb.ButtonPressedForegroundColor  = Microsoft.UI.Colors.White;
                        tb.PreferredHeightOption         = TitleBarHeightOption.Tall;
                    }

                    // Set window icon
                    var iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "appicon.ico");
                    if (System.IO.File.Exists(iconPath))
                        appWindow.SetIcon(iconPath);

                    appWindow.Title = "FileRenamer";
                }
            }
            else
            {
                AppTitleBar.Visibility = Visibility.Collapsed;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed custom title bar setup: {ex.Message}");
        }
    }

    private AppWindow? GetAppWindow()
    {
        try
        {
            var windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(windowHandle);
            return Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);
        }
        catch
        {
            return null;
        }
    }

    private async Task LoadModelsAsync(string? selectedModel = null)
    {
        try
        {
            ModelCombo.SelectionChanged -= ModelCombo_SelectionChanged;
            ModelCombo.Items.Clear();
            var models = await _ai.GetModelsAsync();
            foreach (var m in models)
                ModelCombo.Items.Add(m);

            if (ModelCombo.Items.Count > 0)
            {
                if (!string.IsNullOrEmpty(selectedModel) && ModelCombo.Items.Contains(selectedModel))
                {
                    ModelCombo.SelectedItem = selectedModel;
                }
                else
                {
                    ModelCombo.SelectedIndex = 0;
                }
            }
            else
            {
                if (_ai.Provider == "Ollama")
                    ModelCombo.PlaceholderText = "No models found — is Ollama running?";
                else
                    ModelCombo.PlaceholderText = "No models available";
            }
        }
        catch
        {
            if (_ai.Provider == "Ollama")
                ModelCombo.PlaceholderText = "Ollama not reachable";
            else
                ModelCombo.PlaceholderText = "Error loading models";
        }
        finally
        {
            ModelCombo.SelectionChanged += ModelCombo_SelectionChanged;
        }
    }

    private void OnItemsCollectionChanged(object? s, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        => RefreshUiState();

    private void RefreshUiState()
    {
        EmptyState.Visibility = _items.Count == 0
            ? Microsoft.UI.Xaml.Visibility.Visible
            : Microsoft.UI.Xaml.Visibility.Collapsed;
        FileCountText.Text = _items.Count == 0
            ? "No files loaded"
            : $"{_items.Count} file{(_items.Count == 1 ? "" : "s")} loaded";
    }

    private void SetStatus(string text)
    {
        StatusBar.Title   = string.Empty;
        StatusBar.Message = text;
        StatusBar.IsOpen  = !string.IsNullOrEmpty(text);
    }

    private void SetGenerating(bool active)
    {
        GenerateBtn.IsEnabled  = !active;
        CancelBtn.Visibility   = active ? Visibility.Visible : Visibility.Collapsed;
        PickFolderBtn.IsEnabled = !active;
        PickFileBtn.IsEnabled   = !active;
    }

    private void ShowError(string title, string message)
    {
        ErrorBar.Title   = title;
        ErrorBar.Message = message;
        ErrorBar.IsOpen  = true;
    }

    private async void PickFolderBtn_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        picker.FileTypeFilter.Add("*");
        var folder = await picker.PickSingleFolderAsync();
        if (folder == null) return;

        string folderPath = folder.Path;
        SetStatus("Scanning folder…");
        PickFolderBtn.IsEnabled = false;

        // Scan all files in the folder (we filter by selection, so load all formats)
        var newItems = await Task.Run(() =>
            Directory.GetFiles(folderPath)
                .Select(f => {
                    string ext = Path.GetExtension(f);
                    string orig = Path.GetFileNameWithoutExtension(f);
                    var parsed = AiService.ParseCategoryAndName("KEEP", orig, ext);
                    return new RenameItem
                    {
                        FullPath      = f,
                        OriginalName  = orig,
                        Extension     = ext,
                        Category      = parsed.Category,
                        TargetSubfolder = parsed.TargetSubfolder
                    };
                })
                .ToList());

        // Update the master list
        _allLoadedItems.Clear();
        _allLoadedItems.AddRange(newItems);

        // Update UI
        UpdateFileTypeCombo();
        ApplyFilter();

        if (string.IsNullOrEmpty(_libraryRootPath))
        {
            SetLibraryPath(folderPath);
        }

        PickFolderBtn.IsEnabled = true;
        SetStatus($"{_allLoadedItems.Count} files loaded.");
    }

    private async void PickFileBtn_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        picker.FileTypeFilter.Add("*");
        var file = await picker.PickSingleFileAsync();
        if (file == null) return;

        string ext = Path.GetExtension(file.Path);
        string orig = Path.GetFileNameWithoutExtension(file.Path);
        var parsed = AiService.ParseCategoryAndName("KEEP", orig, ext);

        var item = new RenameItem
        {
            FullPath = file.Path,
            OriginalName = orig,
            Extension = ext,
            Category = parsed.Category,
            TargetSubfolder = parsed.TargetSubfolder
        };

        _allLoadedItems.Add(item);
        UpdateFileTypeCombo();
        ApplyFilter();
        SetStatus($"{_allLoadedItems.Count} file{(_allLoadedItems.Count == 1 ? "" : "s")} loaded.");
    }

    private void UpdateFileTypeCombo()
    {
        FileTypeCombo.SelectionChanged -= FileTypeCombo_SelectionChanged;
        FileTypeCombo.Items.Clear();

        FileTypeCombo.Items.Add("All Files (*.*)");

        var exts = _allLoadedItems
            .Select(i => i.Extension.ToLowerInvariant())
            .Distinct()
            .OrderBy(e => e)
            .ToList();

        foreach (var ext in exts)
         {
             if (string.IsNullOrEmpty(ext)) continue;
             string friendlyName = ext switch
             {
                 ".pdf" => "PDF Files (*.pdf)",
                 ".docx" => "Word Documents (*.docx)",
                 ".txt" => "Text Files (*.txt)",
                 ".md" => "Markdown Files (*.md)",
                 ".csv" => "CSV Files (*.csv)",
                 ".log" => "Log Files (*.log)",
                 ".png" => "PNG Images (*.png)",
                 ".jpg" or ".jpeg" => "JPEG Images (*.jpg, *.jpeg)",
                 ".bmp" => "BMP Images (*.bmp)",
                 ".gif" => "GIF Images (*.gif)",
                 ".tif" or ".tiff" => "TIFF Images (*.tif, *.tiff)",
                 _ => $"{ext.TrimStart('.').ToUpperInvariant()} Files (*{ext})"
             };

             if (!FileTypeCombo.Items.OfType<string>().Contains(friendlyName))
                 FileTypeCombo.Items.Add(friendlyName);
         }

         FileTypeCombo.SelectedIndex = 0;
         FileTypeCombo.SelectionChanged += FileTypeCombo_SelectionChanged;
    }

    private void FileTypeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FileTypeCombo == null) return;
        ApplyFilter();
    }

    private void MainNavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.IsSettingsSelected)
        {
            MainWorkspace.Visibility = Visibility.Collapsed;
            SettingsWorkspace.Visibility = Visibility.Visible;
        }
        else
        {
            SettingsWorkspace.Visibility = Visibility.Collapsed;
            MainWorkspace.Visibility = Visibility.Visible;
            ApplyFilter();
        }
    }

    private void UpdateCategoryCounts()
    {
        if (MainNavView == null) return;

        int totalCount = _allLoadedItems.Count;
        int books = 0, papers = 0, doc = 0, courses = 0, notes = 0, projects = 0, software = 0, media = 0, personal = 0, finance = 0, gov = 0, certs = 0, archive = 0, unknown = 0;

        foreach (var item in _allLoadedItems)
        {
            switch (item.Category)
            {
                case "Books": books++; break;
                case "Research Papers": papers++; break;
                case "Documentation": doc++; break;
                case "Courses": courses++; break;
                case "Notes": notes++; break;
                case "Projects": projects++; break;
                case "Software": software++; break;
                case "Media": media++; break;
                case "Personal": personal++; break;
                case "Finance": finance++; break;
                case "Government": gov++; break;
                case "Certificates": certs++; break;
                case "Archive": archive++; break;
                default: unknown++; break;
            }
        }

        foreach (var item in MainNavView.MenuItems.OfType<NavigationViewItem>())
        {
            string tag = item.Tag as string ?? "";
            switch (tag)
            {
                case "All": item.Content = $"All Files ({totalCount})"; break;
                case "Books": item.Content = $"Books ({books})"; break;
                case "Research Papers": item.Content = $"Research Papers ({papers})"; break;
                case "Documentation": item.Content = $"Documentation ({doc})"; break;
                case "Courses": item.Content = $"Courses ({courses})"; break;
                case "Notes": item.Content = $"Notes ({notes})"; break;
                case "Projects": item.Content = $"Projects ({projects})"; break;
                case "Software": item.Content = $"Software ({software})"; break;
                case "Media": item.Content = $"Media ({media})"; break;
                case "Personal": item.Content = $"Personal ({personal})"; break;
                case "Finance": item.Content = $"Finance ({finance})"; break;
                case "Government": item.Content = $"Government ({gov})"; break;
                case "Certificates": item.Content = $"Certificates ({certs})"; break;
                case "Archive": item.Content = $"Archive ({archive})"; break;
                case "Unknown": item.Content = $"Unknown ({unknown})"; break;
            }
        }
    }

    private void ApplyFilter()
    {
         if (FileTypeCombo == null || MainNavView == null) return;

         _items.CollectionChanged -= OnItemsCollectionChanged;
         _items.Clear();

         // Get selected category from NavigationView
         string selectedCategory = "All";
         if (MainNavView.SelectedItem is NavigationViewItem selectedNav)
         {
             selectedCategory = selectedNav.Tag as string ?? "All";
         }

         // Get selected extension wildcard
         List<string>? extensionWildcards = null;
         if (FileTypeCombo.SelectedIndex > 0)
         {
             string selectedText = FileTypeCombo.SelectedItem as string ?? "";
             var match = System.Text.RegularExpressions.Regex.Match(selectedText, @"\(([^)]+)\)");
             if (match.Success)
             {
                 extensionWildcards = match.Groups[1].Value
                     .Split(',', StringSplitOptions.TrimEntries)
                     .Select(w => w.Replace("*", "").ToLowerInvariant())
                     .ToList();
             }
         }

         foreach (var item in _allLoadedItems)
         {
             bool matchCategory = selectedCategory == "All" || item.Category == selectedCategory;
             bool matchExtension = extensionWildcards == null || extensionWildcards.Contains(item.Extension.ToLowerInvariant());

             if (matchCategory && matchExtension)
             {
                 _items.Add(item);
             }
         }

         _items.CollectionChanged += OnItemsCollectionChanged;
         RefreshUiState();
         UpdateCategoryCounts();
    }

    private void AddItem(string path)
    {
        string ext = Path.GetExtension(path);
        string orig = Path.GetFileNameWithoutExtension(path);
        var parsed = AiService.ParseCategoryAndName("KEEP", orig, ext);
        _items.Add(new RenameItem
        {
            FullPath = path,
            OriginalName = orig,
            Extension = ext,
            Category = parsed.Category,
            TargetSubfolder = parsed.TargetSubfolder
        });
    }

    private async void GenerateBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_items.Count == 0) return;

        // Smart target selection:
        // 1. First prioritize items that failed (Status == "Error")
        var targetItems = _items.Where(i => i.Status == "Error").ToList();
        
        // 2. If none failed, target any "Pending" or empty suggested items
        if (targetItems.Count == 0)
        {
            targetItems = _items.Where(i => i.Status == "Pending" || string.IsNullOrEmpty(i.SuggestedName)).ToList();
        }
        
        // 3. Fallback: process all non-skipped items to regenerate
        if (targetItems.Count == 0)
        {
            targetItems = _items.Where(i => !i.Status.StartsWith("Skipped")).ToList();
        }

        if (targetItems.Count == 0) return;

        string selectedModel = ModelCombo.SelectedItem as string ?? ModelCombo.Text;
        if (string.IsNullOrEmpty(selectedModel))
        {
            selectedModel = GetDefaultModelForProvider(_ai.Provider);
        }
        _ai.Model = selectedModel;

        // Save selected model
        var settings = SettingsService.LoadSettings();
        if (settings.Provider == "Ollama") settings.OllamaModel = selectedModel;
        else if (settings.Provider == "OpenAI") settings.OpenAiModel = selectedModel;
        else if (settings.Provider == "Google") settings.GoogleModel = selectedModel;
        SettingsService.SaveSettings(settings);

        ErrorBar.IsOpen = false;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        SetGenerating(true);

        int done = 0;
        try
        {
            foreach (var item in targetItems)
            {
                token.ThrowIfCancellationRequested();

                item.Status = "Reading...";
                try
                {
                    var extraction = await Task.Run(
                        () => DocumentTextExtractor.Extract(item.FullPath), token);

                    if (extraction.Skipped)
                    {
                        item.Status = $"Skipped ({extraction.SkipReason})";
                        item.SuggestedName = item.OriginalName;
                        continue;
                    }

                    item.ExtractedText = extraction.Text;
                    item.PagesRead     = extraction.PagesRead;
                    item.WasOcr        = extraction.WasOcr;

                    string onlineMetadata = "";
                    if (item.Category == "Books")
                    {
                        item.Status = $"Searching online metadata… ({done + 1}/{targetItems.Count})";
                        SetStatus(item.Status);
                        onlineMetadata = await OnlineBookSearchService.SearchBookOnlineAsync(extraction.Text, item.OriginalName);
                    }

                    item.Status = $"Asking model… ({done + 1}/{targetItems.Count})";
                    SetStatus(item.Status);

                    var result = await _ai.SuggestNameAsync(
                        originalName  : item.OriginalName,
                        extension     : item.Extension,
                        extractedText : extraction.Text,
                        pageCount     : extraction.PagesRead,
                        fileSizeBytes : new System.IO.FileInfo(item.FullPath).Length,
                        wasOcr        : extraction.WasOcr,
                        parentFolder  : Path.GetFileName(Path.GetDirectoryName(item.FullPath) ?? ""),
                        onlineMetadata: onlineMetadata,
                        ct            : token);

                    item.SuggestedName = result.SuggestedName;
                    item.Category      = result.Category;
                    item.TargetSubfolder = result.TargetSubfolder;

                    item.Status = extraction.WasOcr ? "Ready (OCR)" : "Ready";
                    done++;
                    UpdateCategoryCounts();
                }
                catch (OperationCanceledException)
                {
                    item.Status = "Cancelled";
                    throw;   // bubble up to outer catch
                }
                catch (Exception ex)
                {
                    item.Status = "Error";
                    item.SuggestedName = item.OriginalName;
                    ShowError($"Failed — {item.OriginalName}", ex.Message);
                }
            }
            SetStatus($"Done — {done} suggestion{(done == 1 ? "" : "s")} generated.");
        }
        catch (OperationCanceledException)
        {
            SetStatus("Cancelled.");
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            SetGenerating(false);
        }
    }

    private void CancelBtn_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        CancelBtn.IsEnabled = false;
        SetStatus("Cancelling…");
    }

    private void RenameSelectedBtn_Click(object sender, RoutedEventArgs e) =>
        DoRename(_items.Where(i => i.IsSelected && !i.Status.StartsWith("Skipped")));

    private void RenameAllBtn_Click(object sender, RoutedEventArgs e) =>
        DoRename(_items.Where(i => !i.Status.StartsWith("Skipped")));

    private async void MoveToLibraryBtn_Click(object sender, RoutedEventArgs e)
    {
        var targetItems = _items.Where(i => i.IsSelected).ToList();
        if (targetItems.Count == 0)
        {
            ShowError("No Files Selected", "Please select at least one file to move to the library.");
            return;
        }

        if (string.IsNullOrEmpty(_libraryRootPath))
        {
            var picker = new FolderPicker();
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            picker.FileTypeFilter.Add("*");
            var folder = await picker.PickSingleFolderAsync();
            if (folder == null) return;
            SetLibraryPath(folder.Path);
        }

        int moved = 0, failed = 0;

        foreach (var item in targetItems)
        {
            try
            {
                string targetDir = Path.Combine(_libraryRootPath!, item.TargetSubfolder);
                if (!Directory.Exists(targetDir))
                {
                    Directory.CreateDirectory(targetDir);
                }

                string finalName = string.IsNullOrEmpty(item.SuggestedName) ? item.OriginalName : item.SuggestedName;
                string destPath = Path.Combine(targetDir, finalName + item.Extension);
                destPath = GetUniquePath(destPath);

                File.Move(item.FullPath, destPath);
                item.FullPath = destPath;
                item.Status = "Moved";
                moved++;
            }
            catch (Exception ex)
            {
                item.Status = "Move Failed";
                failed++;
                ShowError($"Failed to move {item.OriginalName}", ex.Message);
            }
        }

        SetStatus($"Moved {moved} file(s) to Library{(failed > 0 ? $", {failed} failed" : "")}.");
        
        foreach (var item in targetItems.Where(i => i.Status == "Moved"))
        {
            _allLoadedItems.Remove(item);
        }
        ApplyFilter();
    }

    private async void MoveItemBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.DataContext is not RenameItem item) return;

        if (string.IsNullOrEmpty(_libraryRootPath))
        {
            var picker = new FolderPicker();
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            picker.FileTypeFilter.Add("*");
            var folder = await picker.PickSingleFolderAsync();
            if (folder == null) return;
            SetLibraryPath(folder.Path);
        }

        try
        {
            string targetDir = Path.Combine(_libraryRootPath!, item.TargetSubfolder);
            if (!Directory.Exists(targetDir))
            {
                Directory.CreateDirectory(targetDir);
            }

            string finalName = string.IsNullOrEmpty(item.SuggestedName) ? item.OriginalName : item.SuggestedName;
            string destPath = Path.Combine(targetDir, finalName + item.Extension);
            destPath = GetUniquePath(destPath);

            File.Move(item.FullPath, destPath);
            item.FullPath = destPath;
            item.Status = "Moved";

            _allLoadedItems.Remove(item);
            ApplyFilter();

            SetStatus($"Moved {item.OriginalName} to Library.");
        }
        catch (Exception ex)
        {
            item.Status = "Move Failed";
            ShowError($"Failed to move {item.OriginalName}", ex.Message);
        }
    }

    private void ViewFileBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is RenameItem item)
        {
            OpenFile(item);
        }
    }

    private void ItemsList_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
    {
        if (ItemsList.SelectedItem is RenameItem item)
        {
            OpenFile(item);
        }
    }

    private void OpenFile(RenameItem item)
    {
        try
        {
            if (System.IO.File.Exists(item.FullPath))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(item.FullPath) { UseShellExecute = true });
            }
            else
            {
                ShowError("File Not Found", $"The file no longer exists at: {item.FullPath}");
            }
        }
        catch (Exception ex)
        {
            ShowError("Failed to Open File", ex.Message);
        }
    }

    private void DoRename(IEnumerable<RenameItem> items)
    {
        int done = 0, failed = 0;
        foreach (var item in items.ToList())
        {
            try
            {
                string dir = Path.GetDirectoryName(item.FullPath)!;
                string newPath = Path.Combine(dir, item.SuggestedName + item.Extension);
                newPath = GetUniquePath(newPath);
                File.Move(item.FullPath, newPath);
                item.FullPath = newPath;
                item.Status = "Renamed";
                done++;
            }
            catch
            {
                item.Status = "Failed";
                failed++;
            }
        }
        SetStatus($"Renamed {done} file{(done == 1 ? "" : "s")}{(failed > 0 ? $", {failed} failed" : "")}.");
    }

    private static string GetUniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        string dir = Path.GetDirectoryName(path)!;
        string name = Path.GetFileNameWithoutExtension(path);
        string ext = Path.GetExtension(path);
        int i = 1;
        string candidate;
do { candidate = Path.Combine(dir, $"{name}-{i++}{ext}"); }
        while (File.Exists(candidate));
        return candidate;
    }

    private void SetLibraryPath(string path)
    {
        _libraryRootPath = path;
        LibraryPathText.Text = string.IsNullOrEmpty(path) ? "No Destination Selected" : path;
    }

    private async void ChangeLibraryPathBtn_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        picker.FileTypeFilter.Add("*");
        var folder = await picker.PickSingleFolderAsync();
        if (folder != null)
        {
            SetLibraryPath(folder.Path);
        }
    }

    private bool TrySetMicaBackdrop()
    {
        try
        {
            this.SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop()
            {
                Kind = Microsoft.UI.Composition.SystemBackdrops.MicaKind.BaseAlt
            };
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void ApplySettingsToService(AppSettings settings)
    {
        _ai.Provider = settings.Provider;
        _ai.Endpoint = settings.OllamaEndpoint;
        _ai.OllamaApiKey = settings.OllamaApiKey;
        _ai.OpenAiApiKey = settings.OpenAiApiKey;
        _ai.GoogleApiKey = settings.GoogleApiKey;

        if (QuotaPanel != null)
        {
            QuotaPanel.Visibility = Visibility.Collapsed;
        }
    }

    private string GetCurrentProviderModel(AppSettings settings)
    {
        return settings.Provider switch
        {
            "OpenAI" => settings.OpenAiModel,
            "Google" => settings.GoogleModel,
            _ => settings.OllamaModel
        };
    }

    private string GetDefaultModelForProvider(string provider)
    {
        return provider switch
        {
            "OpenAI" => "gpt-4o-mini",
            "Google" => "gemini-1.5-flash",
            _ => "gemma3:4b"
        };
    }

    private void LoadSettingsToUi(AppSettings settings)
    {
        if (ProviderCombo == null) return;

        foreach (ComboBoxItem item in ProviderCombo.Items)
        {
            if (item.Tag as string == settings.Provider)
            {
                ProviderCombo.SelectedItem = item;
                break;
            }
        }
        if (ProviderCombo.SelectedItem == null && ProviderCombo.Items.Count > 0)
        {
            ProviderCombo.SelectedIndex = 0;
        }

        OllamaEndpointText.Text = settings.OllamaEndpoint;
        OllamaApiKeyText.Password = settings.OllamaApiKey;
        OpenAiApiKeyText.Password = settings.OpenAiApiKey;
        GoogleApiKeyText.Password = settings.GoogleApiKey;

        UpdateSettingsPanelsVisibility(settings.Provider);
    }

    private void UpdateSettingsPanelsVisibility(string provider)
    {
        if (OllamaSettingsPanel == null || OpenAiSettingsPanel == null || GoogleSettingsPanel == null) return;

        OllamaSettingsPanel.Visibility = provider == "Ollama" ? Visibility.Visible : Visibility.Collapsed;
        OpenAiSettingsPanel.Visibility = provider == "OpenAI" ? Visibility.Visible : Visibility.Collapsed;
        GoogleSettingsPanel.Visibility = provider == "Google" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ProviderCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProviderCombo == null || ProviderCombo.SelectedItem == null) return;
        string provider = (ProviderCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "Ollama";
        UpdateSettingsPanelsVisibility(provider);
    }

    private void ModelCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ModelCombo == null || ModelCombo.SelectedItem == null) return;
        string selectedModel = ModelCombo.SelectedItem as string ?? "";
        if (string.IsNullOrEmpty(selectedModel)) return;

        var settings = SettingsService.LoadSettings();
        if (settings.Provider == "Ollama") settings.OllamaModel = selectedModel;
        else if (settings.Provider == "OpenAI") settings.OpenAiModel = selectedModel;
        else if (settings.Provider == "Google") settings.GoogleModel = selectedModel;
        SettingsService.SaveSettings(settings);
    }

    private async void SaveSettingsBtn_Click(object sender, RoutedEventArgs e)
    {
        string provider = (ProviderCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "Ollama";
        var settings = new AppSettings
        {
            Provider = provider,
            OllamaEndpoint = OllamaEndpointText.Text,
            OllamaApiKey = OllamaApiKeyText.Password,
            OpenAiApiKey = OpenAiApiKeyText.Password,
            GoogleApiKey = GoogleApiKeyText.Password
        };

        var oldSettings = SettingsService.LoadSettings();
        settings.OllamaModel = oldSettings.OllamaModel;
        settings.OpenAiModel = oldSettings.OpenAiModel;
        settings.GoogleModel = oldSettings.GoogleModel;

        string currentModel = ModelCombo.SelectedItem as string ?? ModelCombo.Text;
        if (!string.IsNullOrEmpty(currentModel))
        {
            if (provider == "Ollama") settings.OllamaModel = currentModel;
            else if (provider == "OpenAI") settings.OpenAiModel = currentModel;
            else if (provider == "Google") settings.GoogleModel = currentModel;
        }

        SettingsService.SaveSettings(settings);
        ApplySettingsToService(settings);

        await LoadModelsAsync(GetCurrentProviderModel(settings));

        SetStatus("Settings saved successfully.");
    }

    private async void PullModelBtn_Click(object sender, RoutedEventArgs e)
    {
        string modelName = PullModelNameText.Text.Trim();
        if (string.IsNullOrEmpty(modelName))
        {
            PullStatusText.Text = "Please enter a model name.";
            PullStatusText.Visibility = Visibility.Visible;
            return;
        }

        PullModelBtn.IsEnabled = false;
        PullModelNameText.IsEnabled = false;
        PullProgressRing.IsActive = true;
        PullStatusText.Text = "Starting pull...";
        PullStatusText.Visibility = Visibility.Visible;

        using var cts = new CancellationTokenSource();
        try
        {
            // Update service immediately with current UI values in case they aren't saved yet
            _ai.Endpoint = OllamaEndpointText.Text;
            _ai.OllamaApiKey = OllamaApiKeyText.Password;

            await _ai.PullModelAsync(modelName, progress =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    PullStatusText.Text = progress;
                });
            }, cts.Token);

            PullStatusText.Text = $"Successfully pulled model '{modelName}'!";
            
            var settings = SettingsService.LoadSettings();
            await LoadModelsAsync(GetCurrentProviderModel(settings));
        }
        catch (Exception ex)
        {
            PullStatusText.Text = $"Error: {ex.Message}";
        }
        finally
        {
            PullModelBtn.IsEnabled = true;
            PullModelNameText.IsEnabled = true;
            PullProgressRing.IsActive = false;
        }
    }

    private void OnRateLimitUpdated(object? sender, RateLimitInfo info)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (info.LimitTokens > 0)
            {
                QuotaProgressBar.Maximum = info.LimitTokens;
                QuotaProgressBar.Value = info.RemainingTokens;
                
                double pct = (double)info.RemainingTokens / info.LimitTokens * 100;
                QuotaText.Text = $"{pct:F0}% ({FormatNumber(info.RemainingTokens)} / {FormatNumber(info.LimitTokens)} tokens)";
                QuotaPanel.Visibility = Visibility.Visible;
            }
            else if (info.LimitRequests > 0)
            {
                QuotaProgressBar.Maximum = info.LimitRequests;
                QuotaProgressBar.Value = info.RemainingRequests;
                
                double pct = (double)info.RemainingRequests / info.LimitRequests * 100;
                QuotaText.Text = $"{pct:F0}% ({info.RemainingRequests} / {info.LimitRequests} reqs)";
                QuotaPanel.Visibility = Visibility.Visible;
            }
        });
    }

    private string FormatNumber(long num)
    {
        if (num >= 1000000)
            return $"{(double)num / 1000000:F1}M";
        if (num >= 1000)
            return $"{(double)num / 1000:F1}K";
        return num.ToString();
    }
}

