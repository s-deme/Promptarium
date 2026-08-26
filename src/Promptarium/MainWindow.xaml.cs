using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using Promptarium.Models;
using Promptarium.Services;
using Clipboard = System.Windows.Clipboard;
using MessageBox = System.Windows.MessageBox;
using Mouse = System.Windows.Input.Mouse;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;

namespace Promptarium;

public partial class MainWindow : Window
{
    private const int PageSize = 100;
    private readonly LibraryDatabase _database = new();
    private readonly ImageScanner _scanner;
    private readonly ObservableCollection<ImageCard> _cards = [];
    private readonly ObservableCollection<PromptTag> _tags = [];
    private ImageDetail? _selectedDetail;
    private int _currentPage;
    private int _totalResults;
    private bool _isLoading;
    private bool _isScanning;
    private CancellationTokenSource? _scanCancellation;
    private readonly DispatcherTimer _searchDebounceTimer;

    public ObservableCollection<ImageCard> Cards => _cards;
    public IReadOnlyList<string> TagCategories => PromptClassifier.Categories;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        _database.Initialize();
        _scanner = new ImageScanner(_database, new PngMetadataReader(), new ComfyWorkflowParser(), new PromptClassifier());
        _searchDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _searchDebounceTimer.Tick += SearchDebounceTimer_Tick;
        TagsGrid.ItemsSource = _tags;
        RatingComboBox.SelectedIndex = 0;
        CategoryComboBox.ItemsSource = new[] { "すべて" }.Concat(PromptClassifier.Categories.Skip(1)).ToList();
        CategoryComboBox.SelectedIndex = 0;
        CopyCategoryComboBox.ItemsSource = PromptClassifier.Categories;
        CopyCategoryComboBox.SelectedIndex = 0;
        ParseStatusComboBox.SelectedIndex = 0;
        MinimumRatingComboBox.SelectedIndex = 0;
        TagComboBox.AddHandler(System.Windows.Controls.TextBox.TextChangedEvent, new TextChangedEventHandler(FilterChanged));
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        await RefreshFilterListsAsync();
        await RefreshScanRootsAsync();
        await RefreshLibraryAsync();
    }

    private async void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "スキャンする画像フォルダを選択してください（サブフォルダも対象です）。",
            UseDescriptionForTitle = true
        };
        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        _database.UpsertScanRoot(dialog.SelectedPath, includeSubfolders: true);
        StatusTextBlock.Text = $"登録しました: {dialog.SelectedPath}";
        await ScanAndRefreshAsync();
    }

    private async void ScanAll_Click(object sender, RoutedEventArgs e) => await ScanAndRefreshAsync();

    private async void ForceRescan_Click(object sender, RoutedEventArgs e) => await ScanAndRefreshAsync(forceRescan: true);

    private void CancelScan_Click(object sender, RoutedEventArgs e) => _scanCancellation?.Cancel();

    private async Task ScanAndRefreshAsync(bool forceRescan = false, long? rootId = null)
    {
        if (_isScanning) return;
        if (rootId is null && !_database.GetScanRoots().Any())
        {
            MessageBox.Show("先にスキャン対象のフォルダを追加してください。", "Promptarium", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _isScanning = true;
        _scanCancellation = new CancellationTokenSource();
        SetBusy(true, forceRescan ? "完全再解析を開始しています…" : "差分スキャンを開始しています…");
        try
        {
            var progress = new Progress<ScanProgress>(value => StatusTextBlock.Text = $"スキャン中 {value.Processed}/{value.Discovered}件  更新: {value.Registered}  変更なし: {value.Skipped}  エラー: {value.Failed}");
            var result = rootId is { } id
                ? await _scanner.ScanRootAsync(id, progress, _scanCancellation.Token, forceRescan)
                : await _scanner.ScanAllAsync(progress, _scanCancellation.Token, forceRescan);
            StatusTextBlock.Text = $"スキャン完了: 更新 {result.Registered}件、変更なし {result.Skipped}件、エラー {result.Failed}件（{result.Elapsed.TotalSeconds:F1}秒）";
            _currentPage = 0;
            await RefreshFilterListsAsync();
            await RefreshScanRootsAsync();
            await RefreshLibraryAsync();
        }
        catch (OperationCanceledException)
        {
            StatusTextBlock.Text = "スキャンを中止しました。登録済みの結果は保持されています。";
        }
        catch (Exception exception)
        {
            AppLogger.Error("スキャン処理", exception);
            MessageBox.Show(exception.Message, "スキャンエラー", MessageBoxButton.OK, MessageBoxImage.Error);
            StatusTextBlock.Text = "スキャンに失敗しました。";
        }
        finally
        {
            _scanCancellation?.Dispose();
            _scanCancellation = null;
            _isScanning = false;
            SetBusy(false);
        }
    }

    private void FilterChanged(object sender, RoutedEventArgs e)
    {
        if (_isLoading) return;
        _currentPage = 0;
        _searchDebounceTimer.Stop();
        _searchDebounceTimer.Start();
    }

    private void TagComboBox_KeyUp(object sender, System.Windows.Input.KeyEventArgs e) => FilterChanged(sender, e);

    private async void SearchDebounceTimer_Tick(object? sender, EventArgs e)
    {
        _searchDebounceTimer.Stop();
        await RefreshLibraryAsync(saveSearchHistory: true);
    }

    private async void ClearFilters_Click(object sender, RoutedEventArgs e)
    {
        _isLoading = true;
        SearchTextBox.Clear();
        TagComboBox.Text = string.Empty;
        ModelComboBox.SelectedIndex = 0;
        LoraComboBox.SelectedIndex = 0;
        CategoryComboBox.SelectedIndex = 0;
        FavoritesOnlyCheckBox.IsChecked = false;
        ParseStatusComboBox.SelectedIndex = 0;
        MinimumRatingComboBox.SelectedIndex = 0;
        MinimumWidthTextBox.Clear();
        MinimumHeightTextBox.Clear();
        _isLoading = false;
        _currentPage = 0;
        await RefreshLibraryAsync();
    }

    private async Task RefreshFilterListsAsync()
    {
        var selectedModel = SelectedFilter(ModelComboBox);
        var selectedLora = SelectedFilter(LoraComboBox);
        var tagText = TagComboBox.Text;
        var models = await Task.Run(_database.GetModels);
        var loras = await Task.Run(_database.GetLoras);
        var tags = await Task.Run(() => _database.GetTagSuggestions(tagText, 100));
        var recentSearches = await Task.Run(() => _database.GetRecentSearches());
        _isLoading = true;
        ModelComboBox.ItemsSource = new[] { "すべて" }.Concat(models).ToList();
        LoraComboBox.ItemsSource = new[] { "すべて" }.Concat(loras).ToList();
        ModelComboBox.SelectedItem = models.Contains(selectedModel) ? selectedModel : "すべて";
        LoraComboBox.SelectedItem = loras.Contains(selectedLora) ? selectedLora : "すべて";
        TagComboBox.ItemsSource = tags;
        TagComboBox.Text = tagText;
        RecentSearchComboBox.ItemsSource = new[] { "最近の検索" }.Concat(recentSearches).ToList();
        RecentSearchComboBox.SelectedIndex = 0;
        _isLoading = false;
    }

    private async Task RefreshLibraryAsync(bool saveSearchHistory = false)
    {
        if (_isLoading) return;
        SetBusy(true, "ライブラリを読み込んでいます…");
        try
        {
            var filter = GetSearch();
            _totalResults = await Task.Run(() => _database.CountSearchResults(filter));
            var summaries = await Task.Run(() => _database.Search(filter, PageSize, _currentPage * PageSize));
            var cards = await Task.Run(() => summaries.Select(summary => new ImageCard(summary, ThumbnailLoader.Load(summary.PrimaryPath))).ToList());
            _cards.Clear();
            foreach (var card in cards) _cards.Add(card);
            var pages = Math.Max(1, (int)Math.Ceiling(_totalResults / (double)PageSize));
            if (_currentPage >= pages) _currentPage = Math.Max(0, pages - 1);
            PageTextBlock.Text = $"{_totalResults:N0}件  /  {pages}ページ中 {_currentPage + 1}ページ目";
            if (StatusTextBlock.Text.Contains("読み込んで")) StatusTextBlock.Text = $"{_totalResults:N0}件を表示できます。";
            if (saveSearchHistory && !string.IsNullOrWhiteSpace(filter.Text))
            {
                await Task.Run(() => _database.SaveRecentSearch(filter.Text));
            }
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void LibraryListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LibraryListBox.SelectedItem is not ImageCard card) return;
        var detail = await Task.Run(() => _database.GetDetail(card.Summary.Id));
        if (detail is null) return;
        _selectedDetail = detail;
        PopulateDetail(detail);
    }

    private void PopulateDetail(ImageDetail detail)
    {
        DetailImage.Source = ThumbnailLoader.Load(detail.PrimaryPath, 700);
        DetailPathTextBlock.Text = $"{detail.PrimaryPath}\n{detail.Width} × {detail.Height}  /  保存場所 {detail.LocationCount}件";
        ParseStatusTextBlock.Text = $"解析状態: {StatusLabel(detail.ParseStatus)}";
        ParseMessageTextBlock.Text = detail.ParseMessage ?? string.Empty;
        FavoriteCheckBox.IsChecked = detail.IsFavorite;
        RatingComboBox.SelectedIndex = detail.Rating ?? 0;
        PositiveTextBox.Text = detail.ManualPositivePrompt ?? detail.PositivePrompt ?? string.Empty;
        NegativeTextBox.Text = detail.ManualNegativePrompt ?? detail.NegativePrompt ?? string.Empty;
        ModelTextBox.Text = detail.ManualModelName ?? detail.ModelName ?? string.Empty;
        var sources = ReadValueSources(detail.ValueSourcesJson);
        var positiveSource = SourceOf(detail.ManualPositivePrompt, "positive_prompt", sources);
        var negativeSource = SourceOf(detail.ManualNegativePrompt, "negative_prompt", sources);
        var modelSource = SourceOf(detail.ManualModelName, "model_name", sources);
        var loraSource = SourceOf(null, "loras", sources);
        var vaeSource = SourceOf(detail.ManualVaeName, "vae_name", sources);
        var seedSource = SourceOf(detail.ManualSeed, "seed", sources);
        var stepsSource = SourceOf(detail.ManualSteps, "steps", sources);
        var cfgSource = SourceOf(detail.ManualCfg, "cfg", sources);
        var samplerSource = SourceOf(detail.ManualSampler, "sampler", sources);
        var schedulerSource = SourceOf(detail.ManualScheduler, "scheduler", sources);
        var displayedWidth = detail.ManualWidth ?? detail.WorkflowWidth ?? detail.Width.ToString();
        var displayedHeight = detail.ManualHeight ?? detail.WorkflowHeight ?? detail.Height.ToString();
        var widthSource = !string.IsNullOrWhiteSpace(detail.ManualWidth)
            ? "ユーザー入力"
            : !string.IsNullOrWhiteSpace(detail.WorkflowWidth) ? SourceOf(null, "workflow_width", sources) : SourceOf(null, "width", sources);
        var heightSource = !string.IsNullOrWhiteSpace(detail.ManualHeight)
            ? "ユーザー入力"
            : !string.IsNullOrWhiteSpace(detail.WorkflowHeight) ? SourceOf(null, "workflow_height", sources) : SourceOf(null, "height", sources);
        PositiveSourceTextBlock.Text = $"情報源: {positiveSource}";
        NegativeSourceTextBlock.Text = $"情報源: {negativeSource}";
        GenerationInfoTextBlock.Text = $"解析値（{detail.ParseSource ?? "解析できませんでした"}）\nモデル: {detail.ManualModelName ?? detail.ModelName ?? "—"} [{modelSource}]\nLoRA: {FormatLoras(detail.LorasJson)} [{loraSource}]\nVAE: {detail.ManualVaeName ?? detail.VaeName ?? "—"} [{vaeSource}]  /  Seed: {detail.ManualSeed ?? detail.Seed ?? "—"} [{seedSource}]\nSteps: {detail.ManualSteps ?? detail.Steps ?? "—"} [{stepsSource}]  /  CFG: {detail.ManualCfg ?? detail.Cfg ?? "—"} [{cfgSource}]\nSampler: {detail.ManualSampler ?? detail.Sampler ?? "—"} [{samplerSource}]  /  Scheduler: {detail.ManualScheduler ?? detail.Scheduler ?? "—"} [{schedulerSource}]\n生成サイズ: {displayedWidth} × {displayedHeight} px [{widthSource} / {heightSource}]\nPNG画像サイズ: {detail.Width} × {detail.Height} px [PNG IHDR]";
        VaeTextBox.Text = detail.ManualVaeName ?? string.Empty;
        SeedTextBox.Text = detail.ManualSeed ?? string.Empty;
        StepsTextBox.Text = detail.ManualSteps ?? string.Empty;
        CfgTextBox.Text = detail.ManualCfg ?? string.Empty;
        SamplerTextBox.Text = detail.ManualSampler ?? string.Empty;
        SchedulerTextBox.Text = detail.ManualScheduler ?? string.Empty;
        WidthTextBox.Text = detail.ManualWidth ?? string.Empty;
        HeightTextBox.Text = detail.ManualHeight ?? string.Empty;
        GenerationSourceTextBlock.Text = HasManualGenerationValues(detail)
            ? "手動補足値は抽出値と別に保存されます。空欄の項目は抽出値を変更しません。"
            : "手動補足値は未入力です。入力しても抽出値・元PNGは変更されません。";
        NoteTextBox.Text = detail.Note ?? string.Empty;
        RawMetadataTextBox.Text = detail.RawMetadata ?? string.Empty;
        PromptJsonTextBox.Text = detail.PromptJson ?? string.Empty;
        WorkflowJsonTextBox.Text = detail.WorkflowJson ?? string.Empty;
        LocationsListBox.ItemsSource = detail.Locations;
        _tags.Clear();
        foreach (var tag in detail.Tags) _tags.Add(tag);
    }

    private void SaveEdits_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedDetail is null) return;
        var edits = new UserEdits
        {
            PositivePrompt = ManualValue(PositiveTextBox.Text, _selectedDetail.PositivePrompt),
            NegativePrompt = ManualValue(NegativeTextBox.Text, _selectedDetail.NegativePrompt),
            ModelName = ManualValue(ModelTextBox.Text, _selectedDetail.ModelName),
            IsFavorite = FavoriteCheckBox.IsChecked == true,
            Rating = RatingComboBox.SelectedIndex == 0 ? null : RatingComboBox.SelectedIndex,
            Note = NoteTextBox.Text.Trim()
        };
        var overrides = new UserGenerationOverrides
        {
            VaeName = VaeTextBox.Text.Trim(), Seed = SeedTextBox.Text.Trim(), Steps = StepsTextBox.Text.Trim(), Cfg = CfgTextBox.Text.Trim(),
            Sampler = SamplerTextBox.Text.Trim(), Scheduler = SchedulerTextBox.Text.Trim(), Width = WidthTextBox.Text.Trim(), Height = HeightTextBox.Text.Trim()
        };
        _database.SaveUserEdits(_selectedDetail.Id, edits, overrides, _tags.ToList());
        StatusTextBlock.Text = "変更を保存しました。";
        _ = RefreshLibraryAsync();
    }

    private void CopyPositive_Click(object sender, RoutedEventArgs e) => CopyText(PositiveTextBox.Text, "ポジティブプロンプトをコピーしました。");
    private void CopyNegative_Click(object sender, RoutedEventArgs e) => CopyText(NegativeTextBox.Text, "ネガティブプロンプトをコピーしました。");
    private void CopyCategory_Click(object sender, RoutedEventArgs e)
    {
        var category = CopyCategoryComboBox.SelectedItem as string;
        if (string.IsNullOrWhiteSpace(category)) return;
        var prompt = string.Join(", ", _tags
            .Where(tag => tag.PromptKind == "positive" && tag.Category == category)
            .OrderBy(tag => tag.Ordinal)
            .Select(tag => tag.RawText));
        CopyText(prompt, $"「{category}」のタグをコピーしました。");
    }

    private void ExportWorkflow_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedDetail is null || string.IsNullOrWhiteSpace(_selectedDetail.WorkflowJson))
        {
            MessageBox.Show("書き出せるworkflow JSONがありません。", "Promptarium", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dialog = new SaveFileDialog { Filter = "JSON ファイル|*.json", FileName = "workflow.json" };
        if (dialog.ShowDialog() != true) return;
        File.WriteAllText(dialog.FileName, _selectedDetail.WorkflowJson);
        StatusTextBlock.Text = "workflow JSONを書き出しました。";
    }

    private void OpenLocation_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedDetail is null || string.IsNullOrWhiteSpace(_selectedDetail.PrimaryPath)) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_selectedDetail.PrimaryPath}\"") { UseShellExecute = true });
    }

    private void Backup_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppPaths.BackupDirectory);
        var dialog = new SaveFileDialog { Filter = "Promptarium バックアップ|*.db", FileName = $"promptarium-{DateTime.Now:yyyyMMdd-HHmm}.db", InitialDirectory = AppPaths.BackupDirectory };
        if (dialog.ShowDialog() != true) return;
        _database.BackupTo(dialog.FileName);
        StatusTextBlock.Text = "バックアップを作成しました。";
    }

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Promptarium バックアップ|*.db" };
        if (dialog.ShowDialog() != true) return;
        if (MessageBox.Show("現在のカタログを選択したバックアップで置き換えます。続行しますか？", "復元", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _database.RestoreFrom(dialog.FileName);
        _currentPage = 0;
        await RefreshFilterListsAsync();
        await RefreshLibraryAsync();
        StatusTextBlock.Text = "バックアップを復元しました。";
    }

    private async void PreviousPage_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPage == 0) return;
        _currentPage--;
        await RefreshLibraryAsync();
    }

    private async void NextPage_Click(object sender, RoutedEventArgs e)
    {
        if ((_currentPage + 1) * PageSize >= _totalResults) return;
        _currentPage++;
        await RefreshLibraryAsync();
    }

    private void ToggleScanRoots_Click(object sender, RoutedEventArgs e)
    {
        ScanRootsPanel.Visibility = ScanRootsPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void ScanSelectedRoot_Click(object sender, RoutedEventArgs e)
    {
        if (ScanRootsListBox.SelectedItem is ScanRoot root) await ScanAndRefreshAsync(rootId: root.Id);
    }

    private async void ToggleSelectedRoot_Click(object sender, RoutedEventArgs e)
    {
        if (ScanRootsListBox.SelectedItem is not ScanRoot root) return;
        _database.SetScanRootEnabled(root.Id, !root.IsEnabled);
        await RefreshScanRootsAsync();
    }

    private async void DeleteSelectedRoot_Click(object sender, RoutedEventArgs e)
    {
        if (ScanRootsListBox.SelectedItem is not ScanRoot root) return;
        if (MessageBox.Show($"スキャン対象から削除しますか？\n{root.Path}\n画像資産と過去の場所情報は削除しません。", "フォルダ管理", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _database.DeleteScanRoot(root.Id);
        await RefreshScanRootsAsync();
        StatusTextBlock.Text = "スキャン対象を削除しました。";
    }

    private void ShowDiagnostics_Click(object sender, RoutedEventArgs e) => new DiagnosticsWindow { Owner = this }.Show();

    private async void RecentSearchChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoading || RecentSearchComboBox.SelectedItem is not string query || query == "最近の検索") return;
        SearchTextBox.Text = query;
        _currentPage = 0;
        await RefreshLibraryAsync();
    }

    private async Task RefreshScanRootsAsync()
    {
        var selectedId = (ScanRootsListBox.SelectedItem as ScanRoot)?.Id;
        var roots = await Task.Run(() => _database.GetScanRoots(includeDisabled: true));
        ScanRootsListBox.ItemsSource = roots;
        ScanRootsListBox.SelectedItem = roots.FirstOrDefault(root => root.Id == selectedId);
    }

    private LibrarySearch GetSearch() => new()
    {
        Text = SearchTextBox.Text,
        Model = SelectedFilter(ModelComboBox),
        Lora = SelectedFilter(LoraComboBox),
        Tag = TagComboBox.Text,
        Category = SelectedFilter(CategoryComboBox),
        FavoritesOnly = FavoritesOnlyCheckBox.IsChecked == true,
        ParseStatus = SelectedParseStatus(),
        MinimumRating = MinimumRatingComboBox.SelectedIndex,
        MinimumWidth = ReadNonNegativeInt(MinimumWidthTextBox.Text),
        MinimumHeight = ReadNonNegativeInt(MinimumHeightTextBox.Text)
    };

    private static string SelectedFilter(System.Windows.Controls.ComboBox comboBox) => comboBox.SelectedItem as string is { } value && value != "すべて" ? value : string.Empty;
    private ParseStatus? SelectedParseStatus() => ParseStatusComboBox.SelectedIndex <= 0 ? null : (ParseStatus)(ParseStatusComboBox.SelectedIndex - 1);
    private static int ReadNonNegativeInt(string? value) => int.TryParse(value, out var result) ? Math.Max(0, result) : 0;
    private static string? ManualValue(string? value, string? extracted) => string.Equals(value?.Trim(), extracted?.Trim(), StringComparison.Ordinal) ? null : value?.Trim();
    private static bool HasManualGenerationValues(ImageDetail detail) => new[] { detail.ManualVaeName, detail.ManualSeed, detail.ManualSteps, detail.ManualCfg, detail.ManualSampler, detail.ManualScheduler, detail.ManualWidth, detail.ManualHeight }.Any(value => !string.IsNullOrWhiteSpace(value));
    private void CopyText(string text, string status) { if (!string.IsNullOrWhiteSpace(text)) { Clipboard.SetText(text); StatusTextBlock.Text = status; } }
    private void SetBusy(bool busy, string? status = null) { if (status is not null) StatusTextBlock.Text = status; Mouse.OverrideCursor = busy ? System.Windows.Input.Cursors.Wait : null; }
    private static string StatusLabel(ParseStatus status) => status switch { ParseStatus.Parsed => "解析済み", ParseStatus.Partial => "一部解析", ParseStatus.UnknownNodes => "未知ノードを含む", ParseStatus.NoMetadata => "生成情報なし", ParseStatus.Corrupt => "メタデータ破損", _ => "解析不能" };

    private static Dictionary<string, string> ReadValueSources(string? json)
    {
        try { return JsonSerializer.Deserialize<Dictionary<string, string>>(json ?? "{}") ?? []; }
        catch { return []; }
    }

    private static string SourceOf(string? manualValue, string field, IReadOnlyDictionary<string, string> sources) =>
        !string.IsNullOrWhiteSpace(manualValue) ? "ユーザー入力" : sources.GetValueOrDefault(field, "情報なし");

    private static string FormatLoras(string? json)
    {
        try { var loras = System.Text.Json.JsonSerializer.Deserialize<List<LoraUsage>>(json ?? "[]") ?? []; return loras.Count == 0 ? "—" : string.Join(", ", loras.Select(lora => lora.Name)); }
        catch { return "取得できません"; }
    }

    public sealed class ImageCard
    {
        public ImageCard(ImageSummary summary, System.Windows.Media.Imaging.BitmapImage? thumbnail) { Summary = summary; Thumbnail = thumbnail; }
        public ImageSummary Summary { get; }
        public System.Windows.Media.Imaging.BitmapImage? Thumbnail { get; }
        public string Title => Path.GetFileName(Summary.PrimaryPath);
        public string Model => Summary.ModelName ?? "モデル情報なし";
        public string Meta => $"{Summary.Width}×{Summary.Height}  ·  {StatusLabel(Summary.ParseStatus)}  ·  保存先 {Summary.LocationCount}";
        public string RatingText
        {
            get
            {
                var stars = Summary.Rating is { } rating ? new string('★', rating) : string.Empty;
                return Summary.IsFavorite ? $"★  {stars}" : stars;
            }
        }
    }
}
