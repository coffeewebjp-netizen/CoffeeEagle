using CoffeeEagle.Reader.Models;
using CoffeeEagle.Reader.Services;
using Microsoft.Maui.Controls.Shapes;

namespace CoffeeEagle.Reader.Pages;

public sealed class BookshelfPage : ContentPage
{
    private const string AllFoldersId = "__all__";
    private const string UnfiledFoldersId = "__unfiled__";

    private readonly EagleLibraryStore _store;
    private readonly EagleLibraryIndexer _indexer;
    private readonly EagleImageSourceService _imageSources;
    private readonly List<EagleLibrary> _libraries = [];
    private readonly List<EagleAsset> _visibleAssets = [];
    private readonly CollectionView _assetsView = new();
    private readonly SearchBar _searchBar = new()
    {
        Placeholder = "名前・タグを検索  #tag も可",
        TextColor = Colors.White,
        PlaceholderColor = Color.FromArgb("#667386"),
        CancelButtonColor = Color.FromArgb("#21C7A8"),
        BackgroundColor = Color.FromArgb("#12171D")
    };
    private readonly Label _titleLabel = new()
    {
        Text = "CoffeeEagle",
        FontSize = 24,
        FontAttributes = FontAttributes.Bold,
        TextColor = Colors.White,
        VerticalTextAlignment = TextAlignment.Center
    };
    private readonly Label _summaryLabel = new()
    {
        FontSize = 12,
        TextColor = Color.FromArgb("#98A4B5")
    };
    private readonly Label _emptyLabel = new()
    {
        TextColor = Color.FromArgb("#98A4B5"),
        HorizontalTextAlignment = TextAlignment.Center,
        VerticalTextAlignment = TextAlignment.Center
    };
    private readonly ActivityIndicator _busyIndicator = new()
    {
        Color = Color.FromArgb("#21C7A8"),
        WidthRequest = 22,
        HeightRequest = 22
    };
    private readonly Button _libraryButton;
    private readonly Button _folderButton;
    private readonly Button _tagButton;
    private readonly Button _densityButton;
    private readonly Button _refreshButton;
    private readonly VerticalStackLayout _emptyActions;
    private readonly Button _googleDriveSelectButton;
    private readonly Button _deviceFolderSelectButton;
    private EagleReaderState _state = new();
    private EagleLibrary? _activeLibrary;
    private bool _loaded;
    private bool _isBusy;

    public BookshelfPage(
        EagleLibraryStore store,
        EagleLibraryIndexer indexer,
        EagleImageSourceService imageSources)
    {
        _store = store;
        _indexer = indexer;
        _imageSources = imageSources;

        Title = "CoffeeEagle";
        BackgroundColor = Color.FromArgb("#0B0E12");
        NavigationPage.SetHasNavigationBar(this, false);

        _libraryButton = CreateHeaderButton("Library");
        _folderButton = CreateHeaderButton("Folder");
        _tagButton = CreateHeaderButton("Tag");
        _densityButton = CreateHeaderButton("3x");
        _refreshButton = CreateHeaderButton("更新");
        _googleDriveSelectButton = CreatePrimaryButton("Google Driveから選択");
        _deviceFolderSelectButton = CreateSecondaryButton("端末/同期フォルダを選択");
        _emptyActions = CreateEmptyActions();
        _libraryButton.Clicked += async (_, _) => await ShowLibraryMenuAsync();
        _folderButton.Clicked += async (_, _) => await ShowFolderMenuAsync();
        _tagButton.Clicked += async (_, _) => await ShowTagMenuAsync();
        _densityButton.Clicked += async (_, _) => await CycleDensityAsync();
        _refreshButton.Clicked += async (_, _) => await RefreshActiveLibraryAsync();
        _googleDriveSelectButton.Clicked += async (_, _) => await AddLibraryAsync(preferGoogleDrive: true);
        _deviceFolderSelectButton.Clicked += async (_, _) => await AddLibraryAsync();
        _searchBar.TextChanged += (_, e) => UpdateSearch(e.NewTextValue ?? string.Empty);
        _searchBar.SearchButtonPressed += async (_, _) => await SaveStateAsync();
        _searchBar.Unfocused += async (_, _) => await SaveStateAsync();

        _assetsView.SelectionMode = SelectionMode.Single;
        _assetsView.ItemTemplate = new DataTemplate(CreateAssetCard);
        _assetsView.ItemSizingStrategy = ItemSizingStrategy.MeasureFirstItem;
        _assetsView.SelectionChanged += OnAssetSelected;

        Content = CreateLayout();
        ApplyDensity(refresh: false);
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_loaded)
        {
            RefreshVisibleAssets();
            return;
        }

        _loaded = true;
        await LoadStateAsync();
    }

    private View CreateLayout()
    {
        var topBar = new Grid
        {
            Padding = new Thickness(14, 12, 14, 6),
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Auto)
            },
            ColumnSpacing = 8,
            Children = { _titleLabel, _busyIndicator, _refreshButton }
        };
        Grid.SetColumn(_busyIndicator, 1);
        Grid.SetColumn(_refreshButton, 2);

        var controls = new Grid
        {
            Padding = new Thickness(14, 4, 14, 8),
            RowDefinitions =
            {
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto)
            },
            RowSpacing = 8
        };

        var actionGrid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto)
            },
            ColumnSpacing = 8,
            Children = { _libraryButton, _folderButton, _tagButton, _densityButton }
        };
        Grid.SetColumn(_folderButton, 1);
        Grid.SetColumn(_tagButton, 2);
        Grid.SetColumn(_densityButton, 3);

        controls.Children.Add(actionGrid);
        controls.Children.Add(_searchBar);
        controls.Children.Add(_summaryLabel);
        Grid.SetRow(_searchBar, 1);
        Grid.SetRow(_summaryLabel, 2);

        var listLayer = new Grid
        {
            Children = { _assetsView, _emptyActions }
        };

        var root = new Grid
        {
            RowDefinitions =
            {
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Star)
            },
            Children = { topBar, controls, listLayer }
        };
        Grid.SetRow(controls, 1);
        Grid.SetRow(listLayer, 2);
        return root;
    }

    private async Task LoadStateAsync()
    {
        SetBusy(true, "読み込み中...");
        try
        {
            _state = await _store.LoadAsync();
            _libraries.Clear();
            _libraries.AddRange(_state.Libraries);
            _activeLibrary = _libraries.FirstOrDefault(library =>
                    string.Equals(library.Id, _state.ActiveLibraryId, StringComparison.Ordinal))
                ?? _libraries.FirstOrDefault();
            _state.ActiveLibraryId = _activeLibrary?.Id;
            _searchBar.Text = _state.SearchText;
            RefreshVisibleAssets();
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task AddLibraryAsync(bool preferGoogleDrive = false)
    {
        var uri = await MainActivity.PickDocumentTreeAsync();
        if (uri is null)
        {
            return;
        }

        var treeUri = uri.ToString() ?? string.Empty;
        if (preferGoogleDrive && !_indexer.IsGoogleDriveTree(treeUri))
        {
            var continueAdd = await DisplayAlertAsync(
                "Google Driveではありません",
                "選択されたフォルダはGoogle Drive Providerではありません。",
                "このまま追加",
                "選び直す");
            if (!continueAdd)
            {
                return;
            }
        }

        await IndexLibraryAsync(treeUri, previous: null);
    }

    private async Task RefreshActiveLibraryAsync()
    {
        if (_activeLibrary is null)
        {
            await AddLibraryAsync();
            return;
        }

        await IndexLibraryAsync(_activeLibrary.TreeUri, _activeLibrary);
    }

    private async Task IndexLibraryAsync(string treeUri, EagleLibrary? previous)
    {
        if (_isBusy)
        {
            return;
        }

        var progress = new Progress<string>(message => _summaryLabel.Text = message);
        SetBusy(true, "索引作成中...");
        try
        {
            var library = await _indexer.IndexAsync(treeUri, previous, progress);
            var existingIndex = _libraries.FindIndex(item =>
                string.Equals(item.Id, library.Id, StringComparison.Ordinal)
                || string.Equals(item.TreeUri, library.TreeUri, StringComparison.Ordinal));
            if (existingIndex >= 0)
            {
                _libraries[existingIndex] = library;
            }
            else
            {
                _libraries.Insert(0, library);
            }

            _activeLibrary = library;
            _state.ActiveLibraryId = library.Id;
            _state.SelectedFolderId = AllFoldersId;
            _state.SelectedTags.Clear();
            _state.SelectedTag = null;
            await SaveStateAsync();
            RefreshVisibleAssets();
            if (library.Assets.Count == 0)
            {
                await DisplayAlertAsync("画像が見つかりません", library.IndexMessage, "OK");
            }
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("索引化できません", ex.Message, "OK");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task ShowLibraryMenuAsync()
    {
        var labels = _libraries
            .Select((library, index) => $"{index + 1}. [{library.SourceLabel}] {library.Name}")
            .Concat(["Google Driveフォルダ追加", "端末/同期フォルダ追加"])
            .ToArray();
        var selected = await DisplayActionSheetAsync("ライブラリ", "キャンセル", null, labels);
        if (string.IsNullOrWhiteSpace(selected) || selected == "キャンセル")
        {
            return;
        }

        if (selected == "Google Driveフォルダ追加")
        {
            await AddLibraryAsync(preferGoogleDrive: true);
            return;
        }

        if (selected == "端末/同期フォルダ追加")
        {
            await AddLibraryAsync();
            return;
        }

        var selectedIndex = Array.IndexOf(labels, selected);
        if (selectedIndex < 0 || selectedIndex >= _libraries.Count)
        {
            return;
        }

        _activeLibrary = _libraries[selectedIndex];
        _state.ActiveLibraryId = _activeLibrary.Id;
        _state.SelectedFolderId = AllFoldersId;
        _state.SelectedTags.Clear();
        _state.SelectedTag = null;
        await SaveStateAsync();
        RefreshVisibleAssets();
    }

    private async Task ShowFolderMenuAsync()
    {
        if (_activeLibrary is null)
        {
            await AddLibraryAsync();
            return;
        }

        var items = BuildFolderMenuItems(_activeLibrary);
        var labels = new List<string> { "すべて", "未分類" };
        labels.AddRange(items.Select(item => item.Label));
        var selected = await DisplayActionSheetAsync("フォルダ", "キャンセル", null, labels.ToArray());
        if (string.IsNullOrWhiteSpace(selected) || selected == "キャンセル")
        {
            return;
        }

        _state.SelectedFolderId = selected switch
        {
            "すべて" => AllFoldersId,
            "未分類" => UnfiledFoldersId,
            _ => items.FirstOrDefault(item => string.Equals(item.Label, selected, StringComparison.Ordinal))?.Folder.Id ?? AllFoldersId
        };
        await SaveStateAsync();
        RefreshVisibleAssets();
    }

    private async Task ShowTagMenuAsync()
    {
        if (_activeLibrary is null)
        {
            await AddLibraryAsync();
            return;
        }

        var tagCounts = _activeLibrary.Assets
            .SelectMany(asset => asset.Tags)
            .GroupBy(tag => tag, StringComparer.CurrentCultureIgnoreCase)
            .Select(group => new TagCount(group.Key, group.Count()))
            .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        var page = new TagSelectionPage(tagCounts, _state.SelectedTags);
        await Navigation.PushModalAsync(new NavigationPage(page));
        var selectedTags = await page.Completion;
        if (selectedTags is null)
        {
            return;
        }

        _state.SelectedTags = selectedTags;
        _state.SelectedTag = _state.SelectedTags.FirstOrDefault();
        await SaveStateAsync();
        RefreshVisibleAssets();
    }
    private async Task CycleDensityAsync()
    {
        _state.GridSpan = _state.GridSpan >= 5 ? 2 : _state.GridSpan + 1;
        ApplyDensity(refresh: true);
        await SaveStateAsync();
    }

    private void UpdateSearch(string text)
    {
        _state.SearchText = text;
        RefreshVisibleAssets();
    }

    private void RefreshVisibleAssets()
    {
        _visibleAssets.Clear();
        if (_activeLibrary is not null)
        {
            _visibleAssets.AddRange(_activeLibrary.Assets.Where(MatchesFilters));
        }

        _assetsView.ItemsSource = null;
        _assetsView.ItemsSource = _visibleAssets;
        UpdateHeaderText();
    }

    private bool MatchesSelectedFolder(EagleAsset asset, string folderId)
    {
        if (folderId == UnfiledFoldersId)
        {
            return asset.FolderIds.Count == 0;
        }

        if (_activeLibrary is null)
        {
            return true;
        }

        return asset.FolderIds.Any(assetFolderId => IsFolderOrDescendant(_activeLibrary, assetFolderId, folderId));
    }

    private static bool IsFolderOrDescendant(EagleLibrary library, string folderId, string ancestorFolderId)
    {
        if (string.Equals(folderId, ancestorFolderId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var foldersById = library.Folders.ToDictionary(folder => folder.Id, StringComparer.OrdinalIgnoreCase);
        var currentId = folderId;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (foldersById.TryGetValue(currentId, out var folder)
            && !string.IsNullOrWhiteSpace(folder.ParentId)
            && visited.Add(currentId))
        {
            if (string.Equals(folder.ParentId, ancestorFolderId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            currentId = folder.ParentId;
        }

        return false;
    }
    private bool MatchesFilters(EagleAsset asset)
    {
        var folderId = _state.SelectedFolderId;
        if (!string.IsNullOrWhiteSpace(folderId)
            && folderId != AllFoldersId
            && !MatchesSelectedFolder(asset, folderId))
        {
            return false;
        }

        if (_state.SelectedTags.Count > 0
            && !_state.SelectedTags.All(selectedTag => asset.Tags.Any(tag => string.Equals(tag, selectedTag, StringComparison.CurrentCultureIgnoreCase))))
        {
            return false;
        }

        var terms = (_state.SearchText ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var term in terms)
        {
            if (term.StartsWith('#'))
            {
                var tagTerm = term[1..];
                if (!asset.Tags.Any(tag => tag.Contains(tagTerm, StringComparison.CurrentCultureIgnoreCase)))
                {
                    return false;
                }
            }
            else if (!asset.SearchBlob.Contains(term, StringComparison.CurrentCultureIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private View CreateAssetCard()
    {
        var image = new Image
        {
            Aspect = Aspect.AspectFill,
            BackgroundColor = Color.FromArgb("#171D25"),
            HeightRequest = 112
        };

        var title = new Label
        {
            FontSize = 13,
            FontAttributes = FontAttributes.Bold,
            TextColor = Colors.White,
            LineBreakMode = LineBreakMode.TailTruncation,
            MaxLines = 1
        };

        var meta = new Label
        {
            FontSize = 11,
            TextColor = Color.FromArgb("#98A4B5"),
            LineBreakMode = LineBreakMode.TailTruncation,
            MaxLines = 1
        };

        var tag = new Label
        {
            FontSize = 10,
            TextColor = Color.FromArgb("#D6A73B"),
            LineBreakMode = LineBreakMode.TailTruncation,
            MaxLines = 1
        };

        var stack = new VerticalStackLayout
        {
            Spacing = 5,
            Children = { image, title, meta, tag }
        };

        var card = new Border
        {
            Padding = new Thickness(7),
            Margin = new Thickness(5),
            Stroke = Color.FromArgb("#273241"),
            StrokeThickness = 1,
            StrokeShape = new RoundRectangle { CornerRadius = 8 },
            BackgroundColor = Color.FromArgb("#12171D"),
            Content = stack,
            HeightRequest = GetCardHeight()
        };

        card.BindingContextChanged += (_, _) =>
        {
            if (card.BindingContext is not EagleAsset asset)
            {
                return;
            }

            image.HeightRequest = GetImageHeight();
            card.HeightRequest = GetCardHeight();
            image.Source = _imageSources.CreateThumbnailSource(asset);
            title.Text = asset.Name;
            meta.Text = CreateAssetMetaText(asset);
            tag.Text = asset.Tags.Count == 0 ? string.Empty : string.Join("  ", asset.Tags.Take(3));
        };

        return card;
    }

    private async void OnAssetSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (e.CurrentSelection.FirstOrDefault() is not EagleAsset asset)
        {
            return;
        }

        _assetsView.SelectedItem = null;
        var index = _visibleAssets.IndexOf(asset);
        if (index < 0)
        {
            return;
        }

        if (asset.MediaKind == EagleAssetMediaKind.Image)
        {
            await Navigation.PushAsync(new ViewerPage(_visibleAssets.ToList(), index, _imageSources));
            return;
        }

        try
        {
            await _imageSources.OpenExternalAsync(asset);
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("開けません", ex.Message, "OK");
        }
    }

    private void ApplyDensity(bool refresh)
    {
        _state.GridSpan = Math.Clamp(_state.GridSpan <= 0 ? 3 : _state.GridSpan, 2, 5);
        _densityButton.Text = $"{_state.GridSpan}x";
        _assetsView.ItemsLayout = new GridItemsLayout(_state.GridSpan, ItemsLayoutOrientation.Vertical)
        {
            HorizontalItemSpacing = 2,
            VerticalItemSpacing = 2
        };

        if (refresh)
        {
            RefreshVisibleAssets();
        }
    }

    private void UpdateHeaderText()
    {
        _titleLabel.Text = _activeLibrary?.Name ?? "CoffeeEagle";
        _libraryButton.Text = _activeLibrary is null ? "追加" : _activeLibrary.SourceLabel;
        var folderText = ResolveSelectedFolderName();
        _folderButton.Text = folderText.Length > 9 ? folderText[..9] + "..." : folderText;
        _tagButton.Text = _state.SelectedTags.Count == 0 ? "Tag" : $"Tag {_state.SelectedTags.Count}";
        _emptyActions.IsVisible = _visibleAssets.Count == 0;
        _emptyLabel.Text = _activeLibrary is null
            ? "EAGLE .library フォルダを選択"
            : "表示できる項目がありません";
        var showSelectActions = _activeLibrary is null || _activeLibrary.Assets.Count == 0;
        _googleDriveSelectButton.IsVisible = showSelectActions;
        _deviceFolderSelectButton.IsVisible = showSelectActions;
        _summaryLabel.Text = _activeLibrary is null
            ? "Google Drive または端末上の EAGLE .library を選択"
            : CreateSummaryText(_activeLibrary);
    }

    private string CreateSummaryText(EagleLibrary library)
    {
        var summary = $"{library.SourceLabel}  {_visibleAssets.Count:N0} / {library.Assets.Count:N0} items  Indexed {library.IndexedAt:yyyy-MM-dd HH:mm}";
        return library.Assets.Count == 0 && !string.IsNullOrWhiteSpace(library.IndexMessage)
            ? summary + "  " + library.IndexMessage
            : summary;
    }
    private string ResolveSelectedFolderName()
    {
        if (_state.SelectedFolderId == UnfiledFoldersId)
        {
            return "未分類";
        }

        if (_activeLibrary is null || string.IsNullOrWhiteSpace(_state.SelectedFolderId) || _state.SelectedFolderId == AllFoldersId)
        {
            return "Folder";
        }

        return _activeLibrary.Folders.FirstOrDefault(folder =>
                string.Equals(folder.Id, _state.SelectedFolderId, StringComparison.OrdinalIgnoreCase))?.Name
            ?? "Folder";
    }

    private async Task SaveStateAsync()
    {
        _state.Libraries = _libraries;
        _state.ActiveLibraryId = _activeLibrary?.Id;
        await _store.SaveAsync(_state);
    }

    private void SetBusy(bool isBusy, string? message = null)
    {
        _isBusy = isBusy;
        _busyIndicator.IsRunning = isBusy;
        _busyIndicator.IsVisible = isBusy;
        _refreshButton.IsEnabled = !isBusy;
        _libraryButton.IsEnabled = !isBusy;
        _folderButton.IsEnabled = !isBusy;
        _tagButton.IsEnabled = !isBusy;
        _densityButton.IsEnabled = !isBusy;
        _googleDriveSelectButton.IsEnabled = !isBusy;
        _deviceFolderSelectButton.IsEnabled = !isBusy;
        if (!string.IsNullOrWhiteSpace(message))
        {
            _summaryLabel.Text = message;
        }
    }

    private double GetCardHeight() => _state.GridSpan switch
    {
        <= 2 => 218,
        3 => 176,
        4 => 148,
        _ => 126
    };

    private double GetImageHeight() => _state.GridSpan switch
    {
        <= 2 => 150,
        3 => 112,
        4 => 88,
        _ => 68
    };

    private static string CreateAssetMetaText(EagleAsset asset)
    {
        var extension = string.IsNullOrWhiteSpace(asset.Extension) ? "file" : asset.Extension.TrimStart('.').ToUpperInvariant();
        var size = FormatBytes(asset.SizeBytes);
        var kind = asset.MediaKind switch
        {
            EagleAssetMediaKind.Audio => "AUDIO",
            EagleAssetMediaKind.Video => "VIDEO",
            EagleAssetMediaKind.Image => "IMAGE",
            _ => "FILE"
        };
        return string.IsNullOrWhiteSpace(size) ? $"{kind}  {extension}" : $"{kind}  {extension}  {size}";
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0)
        {
            return string.Empty;
        }

        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var size = (double)bytes;
        var unitIndex = 0;
        while (size >= 1024 && unitIndex < units.Length - 1)
        {
            size /= 1024;
            unitIndex++;
        }

        return unitIndex == 0 ? $"{bytes} B" : $"{size:0.0} {units[unitIndex]}";
    }

    private List<FolderMenuItem> BuildFolderMenuItems(EagleLibrary library)
    {
        var foldersById = library.Folders.ToDictionary(folder => folder.Id, StringComparer.OrdinalIgnoreCase);
        var childrenByParent = library.Folders
            .GroupBy(folder => NormalizeParentId(folder.ParentId, foldersById), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderBy(folder => folder.SortOrder)
                    .ThenBy(folder => folder.Name, StringComparer.CurrentCultureIgnoreCase)
                    .ToList(),
                StringComparer.OrdinalIgnoreCase);
        var items = new List<FolderMenuItem>();
        AppendFolderMenuItems(library, childrenByParent, parentId: string.Empty, depth: 0, items);
        return items;
    }

    private void AppendFolderMenuItems(
        EagleLibrary library,
        IReadOnlyDictionary<string, List<EagleFolder>> childrenByParent,
        string parentId,
        int depth,
        List<FolderMenuItem> items)
    {
        if (!childrenByParent.TryGetValue(parentId, out var folders))
        {
            return;
        }

        foreach (var folder in folders)
        {
            var hasChildren = childrenByParent.ContainsKey(folder.Id);
            var count = CountAssetsInFolderTree(library, folder.Id);
            var indent = new string('　', Math.Min(depth, 8));
            var marker = hasChildren ? "▾ " : "  ";
            var label = count > 0
                ? $"{indent}{marker}{folder.Name}    {count:N0}"
                : $"{indent}{marker}{folder.Name}";
            items.Add(new FolderMenuItem(label, folder));
            AppendFolderMenuItems(library, childrenByParent, folder.Id, depth + 1, items);
        }
    }

    private static string NormalizeParentId(string? parentId, IReadOnlyDictionary<string, EagleFolder> foldersById)
    {
        return !string.IsNullOrWhiteSpace(parentId) && foldersById.ContainsKey(parentId)
            ? parentId
            : string.Empty;
    }

    private static int CountAssetsInFolderTree(EagleLibrary library, string folderId)
    {
        return library.Assets.Count(asset => asset.FolderIds.Any(assetFolderId => IsFolderOrDescendant(library, assetFolderId, folderId)));
    }

    private sealed record FolderMenuItem(string Label, EagleFolder Folder);

    private sealed record TagCount(string Name, int Count);

    private sealed class TagSelectionPage : ContentPage
    {
        private readonly TaskCompletionSource<List<string>?> _completion = new();
        private readonly HashSet<string> _selected;

        public Task<List<string>?> Completion => _completion.Task;

        public TagSelectionPage(IReadOnlyList<TagCount> tags, IEnumerable<string> selectedTags)
        {
            _selected = new HashSet<string>(selectedTags, StringComparer.CurrentCultureIgnoreCase);
            Title = "タグ";
            BackgroundColor = Color.FromArgb("#0B0E12");
            NavigationPage.SetHasNavigationBar(this, true);

            ToolbarItems.Add(new ToolbarItem("クリア", null, () =>
            {
                _selected.Clear();
                Complete(_selected.ToList());
            }));
            ToolbarItems.Add(new ToolbarItem("完了", null, () => Complete(_selected.OrderBy(tag => tag, StringComparer.CurrentCultureIgnoreCase).ToList())));

            var search = new SearchBar
            {
                Placeholder = "タグを検索",
                TextColor = Colors.White,
                PlaceholderColor = Color.FromArgb("#667386"),
                CancelButtonColor = Color.FromArgb("#21C7A8"),
                BackgroundColor = Color.FromArgb("#12171D")
            };

            var list = new VerticalStackLayout { Spacing = 0 };
            void Render(string filter)
            {
                list.Children.Clear();
                var visibleTags = tags.Where(tag => string.IsNullOrWhiteSpace(filter) || tag.Name.Contains(filter, StringComparison.CurrentCultureIgnoreCase));
                foreach (var tag in visibleTags)
                {
                    list.Children.Add(CreateTagRow(tag));
                }
            }

            search.TextChanged += (_, e) => Render(e.NewTextValue ?? string.Empty);
            Render(string.Empty);

            var scroll = new ScrollView { Content = list };
            var root = new Grid
            {
                RowDefinitions =
                {
                    new RowDefinition(GridLength.Auto),
                    new RowDefinition(GridLength.Star)
                },
                Children = { search, scroll }
            };
            Grid.SetRow(scroll, 1);
            Content = root;
        }

        protected override bool OnBackButtonPressed()
        {
            Complete(null);
            return true;
        }

        private View CreateTagRow(TagCount tag)
        {
            var checkBox = new CheckBox
            {
                IsChecked = _selected.Contains(tag.Name),
                Color = Color.FromArgb("#21C7A8"),
                VerticalOptions = LayoutOptions.Center
            };
            var name = new Label
            {
                Text = tag.Name,
                TextColor = Colors.White,
                FontSize = 16,
                FontAttributes = FontAttributes.Bold,
                VerticalTextAlignment = TextAlignment.Center,
                LineBreakMode = LineBreakMode.TailTruncation
            };
            var count = new Label
            {
                Text = tag.Count.ToString("N0"),
                TextColor = Color.FromArgb("#98A4B5"),
                FontSize = 13,
                VerticalTextAlignment = TextAlignment.Center,
                HorizontalTextAlignment = TextAlignment.End
            };
            checkBox.CheckedChanged += (_, e) =>
            {
                if (e.Value)
                {
                    _selected.Add(tag.Name);
                }
                else
                {
                    _selected.Remove(tag.Name);
                }
            };

            var row = new Grid
            {
                Padding = new Thickness(12, 7),
                ColumnDefinitions =
                {
                    new ColumnDefinition(GridLength.Auto),
                    new ColumnDefinition(GridLength.Star),
                    new ColumnDefinition(GridLength.Auto)
                },
                ColumnSpacing = 10,
                Children = { checkBox, name, count }
            };
            Grid.SetColumn(name, 1);
            Grid.SetColumn(count, 2);
            var tap = new TapGestureRecognizer();
            tap.Tapped += (_, _) => checkBox.IsChecked = !checkBox.IsChecked;
            row.GestureRecognizers.Add(tap);
            return row;
        }

        private async void Complete(List<string>? selectedTags)
        {
            if (!_completion.TrySetResult(selectedTags))
            {
                return;
            }

            await Navigation.PopModalAsync();
        }
    }
    private VerticalStackLayout CreateEmptyActions()
    {
        return new VerticalStackLayout
        {
            Padding = new Thickness(28, 0),
            Spacing = 12,
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
            WidthRequest = 320,
            Children = { _emptyLabel, _googleDriveSelectButton, _deviceFolderSelectButton }
        };
    }

    private static Button CreatePrimaryButton(string text)
    {
        return new Button
        {
            Text = text,
            FontSize = 15,
            FontAttributes = FontAttributes.Bold,
            TextColor = Color.FromArgb("#06130F"),
            BackgroundColor = Color.FromArgb("#21C7A8"),
            CornerRadius = 8,
            HeightRequest = 48,
            Padding = new Thickness(14, 0)
        };
    }

    private static Button CreateSecondaryButton(string text)
    {
        return new Button
        {
            Text = text,
            FontSize = 14,
            FontAttributes = FontAttributes.Bold,
            TextColor = Color.FromArgb("#EDF5F3"),
            BackgroundColor = Color.FromArgb("#172029"),
            BorderColor = Color.FromArgb("#2B3948"),
            BorderWidth = 1,
            CornerRadius = 8,
            HeightRequest = 46,
            Padding = new Thickness(14, 0)
        };
    }
    private static Button CreateHeaderButton(string text)
    {
        return new Button
        {
            Text = text,
            FontSize = 12,
            FontAttributes = FontAttributes.Bold,
            TextColor = Color.FromArgb("#EDF5F3"),
            BackgroundColor = Color.FromArgb("#172029"),
            BorderColor = Color.FromArgb("#2B3948"),
            BorderWidth = 1,
            CornerRadius = 8,
            Padding = new Thickness(10, 0),
            HeightRequest = 38,
            MinimumWidthRequest = 54
        };
    }
}
