using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
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

    public ObservableCollection<ImageCard> Cards => _cards;
    public IReadOnlyList<string> TagCategories => PromptClassifier.Categories;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        _database.Initialize();
        _scanner = new ImageScanner(_database, new PngMetadataReader(), new ComfyWorkflowParser(), new PromptClassifier());
        TagsGrid.ItemsSource = _tags;
        RatingComboBox.SelectedIndex = 0;
        CategoryComboBox.ItemsSource = new[] { "すべて" }.Concat(PromptClassifier.Categories.Skip(1)).ToList();
        CategoryComboBox.SelectedIndex = 0;
        CopyCategoryComboBox.ItemsSource = PromptClassifier.Categories;
        CopyCategoryComboBox.SelectedIndex = 0;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        await RefreshFilterListsAsync();
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

    private async Task ScanAndRefreshAsync()
    {
        if (!_database.GetScanRoots().Any())
        {
            MessageBox.Show("先にスキャン対象のフォルダを追加してください。", "Promptarium", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        SetBusy(true, "スキャンを開始しています…");
        try
        {
            var progress = new Progress<ScanProgress>(value => StatusTextBlock.Text = $"スキャン中 {value.Processed}/{value.Discovered}件  登録: {value.Registered}  エラー: {value.Failed}");
            var result = await _scanner.ScanAllAsync(progress);
            StatusTextBlock.Text = $"スキャン完了: {result.Registered}/{result.Discovered}件、エラー {result.Failed}件（{result.Elapsed.TotalSeconds:F1}秒）";
            _currentPage = 0;
            await RefreshFilterListsAsync();
            await RefreshLibraryAsync();
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "スキャンエラー", MessageBoxButton.OK, MessageBoxImage.Error);
            StatusTextBlock.Text = "スキャンに失敗しました。";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void FilterChanged(object sender, RoutedEventArgs e)
    {
        if (_isLoading) return;
        _currentPage = 0;
        await RefreshLibraryAsync();
    }

    private async void ClearFilters_Click(object sender, RoutedEventArgs e)
    {
        _isLoading = true;
        SearchTextBox.Clear();
        TagTextBox.Clear();
        ModelComboBox.SelectedIndex = 0;
        LoraComboBox.SelectedIndex = 0;
        CategoryComboBox.SelectedIndex = 0;
        FavoritesOnlyCheckBox.IsChecked = false;
        _isLoading = false;
        _currentPage = 0;
        await RefreshLibraryAsync();
    }

    private async Task RefreshFilterListsAsync()
    {
        var selectedModel = SelectedFilter(ModelComboBox);
        var selectedLora = SelectedFilter(LoraComboBox);
        var models = await Task.Run(_database.GetModels);
        var loras = await Task.Run(_database.GetLoras);
        _isLoading = true;
        ModelComboBox.ItemsSource = new[] { "すべて" }.Concat(models).ToList();
        LoraComboBox.ItemsSource = new[] { "すべて" }.Concat(loras).ToList();
        ModelComboBox.SelectedItem = models.Contains(selectedModel) ? selectedModel : "すべて";
        LoraComboBox.SelectedItem = loras.Contains(selectedLora) ? selectedLora : "すべて";
        _isLoading = false;
    }

    private async Task RefreshLibraryAsync()
    {
        if (_isLoading) return;
        SetBusy(true, "ライブラリを読み込んでいます…");
        try
        {
            var filter = GetFilter();
            _totalResults = await Task.Run(() => _database.CountSearchResults(filter.Text, filter.Model, filter.Lora, filter.Tag, filter.Category, filter.FavoritesOnly));
            var summaries = await Task.Run(() => _database.Search(filter.Text, filter.Model, filter.Lora, filter.Tag, filter.Category, filter.FavoritesOnly, PageSize, _currentPage * PageSize));
            var cards = await Task.Run(() => summaries.Select(summary => new ImageCard(summary, ThumbnailLoader.Load(summary.PrimaryPath))).ToList());
            _cards.Clear();
            foreach (var card in cards) _cards.Add(card);
            var pages = Math.Max(1, (int)Math.Ceiling(_totalResults / (double)PageSize));
            if (_currentPage >= pages) _currentPage = Math.Max(0, pages - 1);
            PageTextBlock.Text = $"{_totalResults:N0}件  /  {pages}ページ中 {_currentPage + 1}ページ目";
            if (StatusTextBlock.Text.Contains("読み込んで")) StatusTextBlock.Text = $"{_totalResults:N0}件を表示できます。";
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
        PositiveSourceTextBlock.Text = detail.ManualPositivePrompt is null ? "情報源: メタデータ抽出（編集すると手動入力として保存）" : "情報源: ユーザー入力";
        NegativeSourceTextBlock.Text = detail.ManualNegativePrompt is null ? "情報源: メタデータ抽出（編集すると手動入力として保存）" : "情報源: ユーザー入力";
        GenerationInfoTextBlock.Text = $"解析値（{detail.ParseSource ?? "解析できませんでした"}）\nLoRA: {FormatLoras(detail.LorasJson)}\nVAE: {detail.VaeName ?? "—"}  /  Seed: {detail.Seed ?? "—"}  /  Steps: {detail.Steps ?? "—"}  /  CFG: {detail.Cfg ?? "—"}\nSampler: {detail.Sampler ?? "—"}  /  Scheduler: {detail.Scheduler ?? "—"}\n画像サイズ: {detail.Width} × {detail.Height} px（PNG IHDR）";
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

    private (string Text, string Model, string Lora, string Tag, string Category, bool FavoritesOnly) GetFilter() =>
        (SearchTextBox.Text, SelectedFilter(ModelComboBox), SelectedFilter(LoraComboBox), TagTextBox.Text, SelectedFilter(CategoryComboBox), FavoritesOnlyCheckBox.IsChecked == true);

    private static string SelectedFilter(System.Windows.Controls.ComboBox comboBox) => comboBox.SelectedItem as string is { } value && value != "すべて" ? value : string.Empty;
    private static string? ManualValue(string? value, string? extracted) => string.Equals(value?.Trim(), extracted?.Trim(), StringComparison.Ordinal) ? null : value?.Trim();
    private static bool HasManualGenerationValues(ImageDetail detail) => new[] { detail.ManualVaeName, detail.ManualSeed, detail.ManualSteps, detail.ManualCfg, detail.ManualSampler, detail.ManualScheduler, detail.ManualWidth, detail.ManualHeight }.Any(value => !string.IsNullOrWhiteSpace(value));
    private void CopyText(string text, string status) { if (!string.IsNullOrWhiteSpace(text)) { Clipboard.SetText(text); StatusTextBlock.Text = status; } }
    private void SetBusy(bool busy, string? status = null) { if (status is not null) StatusTextBlock.Text = status; Mouse.OverrideCursor = busy ? System.Windows.Input.Cursors.Wait : null; }
    private static string StatusLabel(ParseStatus status) => status switch { ParseStatus.Parsed => "解析済み", ParseStatus.Partial => "一部解析", ParseStatus.UnknownNodes => "未知ノードを含む", ParseStatus.NoMetadata => "生成情報なし", ParseStatus.Corrupt => "メタデータ破損", _ => "解析不能" };

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
