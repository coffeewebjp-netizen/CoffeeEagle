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
    private readonly GoogleDriveLibraryService _drive;
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
    private Action? _activeSheetCancel;

    public BookshelfPage(
        EagleLibraryStore store,
        EagleLibraryIndexer indexer,
        EagleImageSourceService imageSources,
        GoogleDriveLibraryService drive)
    {
        _store = store;
        _indexer = indexer;
        _imageSources = imageSources;
        _drive = drive;

        Title = "CoffeeEagle";
        BackgroundColor = Color.FromArgb("#0B0E12");
        NavigationPage.SetHasNavigationBar(this, false);

        _libraryButton = CreateHeaderButton("Library");
        _folderButton = CreateHeaderButton("Folder");
        _tagButton = CreateHeaderButton("Tag");
        _densityButton = CreateHeaderButton("3x");
        _refreshButton = CreateHeaderButton("更新");
        _googleDriveSelectButton = CreatePrimaryButton("Google Drive APIで追加");
        _deviceFolderSelectButton = CreateSecondaryButton("端末/同期フォルダを選択");
        _emptyActions = CreateEmptyActions();
        _libraryButton.Clicked += async (_, _) => await ShowLibraryMenuAsync();
        _folderButton.Clicked += async (_, _) => await ShowFolderMenuAsync();
        _tagButton.Clicked += async (_, _) => await ShowTagMenuAsync();
        _densityButton.Clicked += async (_, _) => await CycleDensityAsync();
        _refreshButton.Clicked += async (_, _) => await RefreshActiveLibraryAsync();
        _googleDriveSelectButton.Clicked += async (_, _) => await AddGoogleDriveApiLibraryAsync();
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

    protected override bool OnBackButtonPressed()
    {
        if (_activeSheetCancel is not null)
        {
            _activeSheetCancel.Invoke();
            return true;
        }

        return base.OnBackButtonPressed();
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
            _state.Libraries = EagleLibraryIdentity.Deduplicate(_state.Libraries, _state.ActiveLibraryId);
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

    private async Task AddGoogleDriveApiLibraryAsync()
    {
        if (_isBusy)
        {
            return;
        }

        var clientId = await DisplayPromptAsync(
            "Google Drive API",
            "Google OAuth Client IDを入力してください。",
            initialValue: string.IsNullOrWhiteSpace(_state.GoogleDriveClientId) ? GoogleDriveLibraryService.DefaultClientId : _state.GoogleDriveClientId,
            keyboard: Keyboard.Text);
        if (string.IsNullOrWhiteSpace(clientId))
        {
            return;
        }

        var folderInput = await DisplayPromptAsync(
            "Google Drive API",
            "EAGLE .libraryフォルダのURLまたはフォルダIDを入力してください。",
            initialValue: _state.GoogleDriveFolderId ?? string.Empty,
            keyboard: Keyboard.Text);
        if (string.IsNullOrWhiteSpace(folderInput))
        {
            return;
        }

        var folderId = GoogleDriveLibraryService.ExtractFolderId(folderInput);
        if (string.IsNullOrWhiteSpace(folderId))
        {
            await DisplayAlertAsync("Google Drive API", "フォルダIDを読み取れませんでした。", "OK");
            return;
        }

        _state.GoogleDriveClientId = clientId.Trim();
        _state.GoogleDriveClientSecret = null;
        _state.GoogleDriveFolderId = folderId;
        await SaveStateAsync();

        var progress = new Progress<string>(message => _summaryLabel.Text = message);
        if (!await _drive.HasRefreshTokenAsync())
        {
            var connect = await DisplayAlertAsync(
                "Google Drive接続",
                "Googleログインを開き、Driveの読み取りを許可します。",
                "接続",
                "キャンセル");
            if (!connect)
            {
                return;
            }

            SetBusy(true, "Google Drive認証中...");
            try
            {
                await _drive.AuthorizeWithBrowserAsync(_state, progress);
                await SaveStateAsync();
            }
            finally
            {
                SetBusy(false);
            }
        }

        var existingIndex = EagleLibraryIdentity.FindMatchingIndex(
            _libraries,
            GoogleDriveLibraryService.BuildFolderUri(folderId),
            EagleLibrarySourceKind.GoogleDriveApi,
            folderId);
        var existing = existingIndex >= 0 ? _libraries[existingIndex] : null;
        await IndexGoogleDriveApiLibraryAsync(existing);
    }
    private async Task RefreshActiveLibraryAsync()
    {
        if (_activeLibrary is null)
        {
            await AddGoogleDriveApiLibraryAsync();
            return;
        }

        if (_activeLibrary.SourceKind == EagleLibrarySourceKind.GoogleDriveApi
            || GoogleDriveLibraryService.IsDriveFolderUri(_activeLibrary.TreeUri))
        {
            await IndexGoogleDriveApiLibraryAsync(_activeLibrary);
            return;
        }

        await IndexLibraryAsync(_activeLibrary.TreeUri, _activeLibrary);
    }

    private async Task IndexGoogleDriveApiLibraryAsync(EagleLibrary? previous)
    {
        if (_isBusy)
        {
            return;
        }

        var progress = new Progress<string>(message => _summaryLabel.Text = message);
        SetBusy(true, "Drive API索引作成中...");
        try
        {
            var library = await _drive.IndexAsync(_state, previous, progress);
            UpsertLibrary(library);

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
        catch (GoogleDriveReconnectRequiredException ex)
        {
            var reconnect = await DisplayAlertAsync("Google Drive再接続", ex.Message, "接続", "キャンセル");
            if (reconnect)
            {
                await _drive.AuthorizeWithBrowserAsync(_state, progress);
                await SaveStateAsync();
                SetBusy(false);
                await IndexGoogleDriveApiLibraryAsync(previous);
            }
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Drive APIで索引化できません", ex.Message, "OK");
        }
        finally
        {
            SetBusy(false);
        }
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
            UpsertLibrary(library);

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

    private void UpsertLibrary(EagleLibrary library)
    {
        var existingIndex = EagleLibraryIdentity.FindMatchingIndex(_libraries, library);
        if (existingIndex >= 0)
        {
            _libraries[existingIndex] = library;
        }
        else
        {
            _libraries.Insert(0, library);
        }

        var deduplicated = EagleLibraryIdentity.Deduplicate(_libraries, library.Id);
        _libraries.Clear();
        _libraries.AddRange(deduplicated);
    }

    private async Task ShowLibraryMenuAsync()
    {
        var options = _libraries
            .Select(library => new PickerOption(
                library.Id,
                library.Name,
                $"{library.SourceLabel}  {library.Assets.Count:N0} items",
                string.Equals(library.Id, _activeLibrary?.Id, StringComparison.Ordinal),
                "ライブラリ"))
            .Concat([
                new PickerOption("__add_drive_api__", "Google Drive APIフォルダ追加", "Drive APIでEAGLE .libraryを追加", false, "追加"),
                new PickerOption("__add_drive_provider__", "Google Drive Providerフォルダ追加", "AndroidのGoogle Drive Providerから追加", false, "追加"),
                new PickerOption("__add_device__", "端末/同期フォルダ追加", "端末または同期フォルダから追加", false, "追加")
            ])
            .ToList();

        var selected = await ShowOptionPickerAsync("ライブラリ", "切り替え / 追加", options, searchPlaceholder: "ライブラリを検索");
        if (selected is null)
        {
            return;
        }

        if (selected.Id == "__add_drive_api__")
        {
            await AddGoogleDriveApiLibraryAsync();
            return;
        }

        if (selected.Id == "__add_drive_provider__")
        {
            await AddLibraryAsync(preferGoogleDrive: true);
            return;
        }

        if (selected.Id == "__add_device__")
        {
            await AddLibraryAsync();
            return;
        }

        var library = _libraries.FirstOrDefault(item => string.Equals(item.Id, selected.Id, StringComparison.Ordinal));
        if (library is null)
        {
            return;
        }

        _activeLibrary = library;
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
        var options = new List<PickerOption>
        {
            new(AllFoldersId, "すべて", $"{_activeLibrary.Assets.Count:N0} items", IsSelectedFolder(AllFoldersId), "フォルダ"),
            new(UnfiledFoldersId, "未分類", $"{_activeLibrary.Assets.Count(asset => asset.FolderIds.Count == 0):N0} items", IsSelectedFolder(UnfiledFoldersId), "フォルダ")
        };
        options.AddRange(items.Select(item => new PickerOption(
            item.Folder.Id,
            item.DisplayName,
            item.Count > 0 ? $"{item.Count:N0} items" : "空のフォルダ",
            IsSelectedFolder(item.Folder.Id),
            item.Depth == 0 ? "フォルダ" : $"階層 {item.Depth + 1}",
            item.Depth)));

        var selected = await ShowOptionPickerAsync("フォルダ", "階層から選択", options, searchPlaceholder: "フォルダを検索");
        if (selected is null)
        {
            return;
        }

        _state.SelectedFolderId = selected.Id;
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
        var selectedTags = await ShowTagPopupAsync(tagCounts, _state.SelectedTags, _activeLibrary.Assets.Count);
        if (selectedTags is null)
        {
            return;
        }

        _state.SelectedTags = selectedTags;
        _state.SelectedTag = _state.SelectedTags.FirstOrDefault();
        await SaveStateAsync();
        RefreshVisibleAssets();
    }

    private Task<List<string>?> ShowTagPopupAsync(
        IReadOnlyList<TagCount> tagCounts,
        IEnumerable<string> selectedTags,
        int assetCount)
    {
        var popup = new TagPopupView(tagCounts, selectedTags, assetCount);
        return ShowBottomSheetAsync(popup, popup.Completion, popup.Cancel);
    }

    private Task<PickerOption?> ShowOptionPickerAsync(
        string title,
        string subtitle,
        IReadOnlyList<PickerOption> options,
        string searchPlaceholder)
    {
        var popup = new OptionPickerView(title, subtitle, options, searchPlaceholder);
        return ShowBottomSheetAsync(popup, popup.Completion, popup.Cancel);
    }

    private async Task<TResult?> ShowBottomSheetAsync<TResult>(
        View popup,
        Task<TResult?> completion,
        Action cancel)
    {
        if (Content is not Grid root)
        {
            return default;
        }

        var previousCancel = _activeSheetCancel;
        _activeSheetCancel = cancel;
        var dimmer = new BoxView
        {
            Color = Color.FromArgb("#48000000"),
            InputTransparent = false
        };
        var panel = new Border
        {
            BackgroundColor = Color.FromArgb("#F00B0E12"),
            Stroke = Color.FromArgb("#334150"),
            StrokeThickness = 1,
            StrokeShape = new RoundRectangle { CornerRadius = 12 },
            Content = popup,
            VerticalOptions = LayoutOptions.End,
            HeightRequest = Math.Min(Math.Max(420, Height * 0.72), 620),
            Margin = new Thickness(10, 0, 10, 10)
        };
        var overlay = new Grid
        {
            Opacity = 0,
            InputTransparent = false,
            Children = { dimmer, panel }
        };
        Grid.SetRowSpan(overlay, Math.Max(1, root.RowDefinitions.Count));
        overlay.ZIndex = 1000;

        var dimmerTap = new TapGestureRecognizer();
        dimmerTap.Tapped += (_, _) => cancel();
        dimmer.GestureRecognizers.Add(dimmerTap);

        TResult? result = default;
        root.Children.Add(overlay);
        panel.TranslationY = 32;
        try
        {
            await Task.WhenAll(
                overlay.FadeToAsync(1, 140, Easing.CubicOut),
                panel.TranslateToAsync(0, 0, 180, Easing.CubicOut));

            result = await completion;

            await Task.WhenAll(
                overlay.FadeToAsync(0, 120, Easing.CubicIn),
                panel.TranslateToAsync(0, 32, 120, Easing.CubicIn));
        }
        finally
        {
            root.Children.Remove(overlay);
            if (ReferenceEquals(_activeSheetCancel, cancel))
            {
                _activeSheetCancel = previousCancel;
            }
        }

        return result;
    }

    private bool IsSelectedFolder(string folderId)
    {
        return string.Equals(_state.SelectedFolderId ?? AllFoldersId, folderId, StringComparison.OrdinalIgnoreCase);
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
            var imageAssets = _visibleAssets.Where(item => item.MediaKind == EagleAssetMediaKind.Image).ToList();
            var imageIndex = imageAssets.FindIndex(item => string.Equals(item.Id, asset.Id, StringComparison.Ordinal));
            await Navigation.PushAsync(new ViewerPage(imageAssets, Math.Max(0, imageIndex), _imageSources, _store));
            return;
        }

        if (asset.MediaKind == EagleAssetMediaKind.Audio)
        {
            await Navigation.PushAsync(new AudioPlayerPage(_visibleAssets.ToList(), index, _imageSources, _store));
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
        var imageCount = library.Assets.Count(asset => asset.MediaKind == EagleAssetMediaKind.Image);
        var audioCount = library.Assets.Count(asset => asset.MediaKind == EagleAssetMediaKind.Audio);
        var videoCount = library.Assets.Count(asset => asset.MediaKind == EagleAssetMediaKind.Video);
        var summary = $"{library.SourceLabel}  {_visibleAssets.Count:N0} / {library.Assets.Count:N0} items  IMAGE {imageCount:N0} AUDIO {audioCount:N0} VIDEO {videoCount:N0}  Indexed {library.IndexedAt:yyyy-MM-dd HH:mm}";
        return ShouldShowIndexMessage(library)
            ? summary + "  " + library.IndexMessage
            : summary;
    }

    private static bool ShouldShowIndexMessage(EagleLibrary library)
    {
        if (string.IsNullOrWhiteSpace(library.IndexMessage))
        {
            return false;
        }

        return library.Assets.Count == 0
            || library.IndexMessage.Contains("unresolved", StringComparison.OrdinalIgnoreCase)
            || library.IndexMessage.Contains("recovered", StringComparison.OrdinalIgnoreCase)
            || library.IndexMessage.Contains("read-fail", StringComparison.OrdinalIgnoreCase);
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
            var count = CountAssetsInFolderTree(library, folder.Id);
            items.Add(new FolderMenuItem(folder.Name, folder, depth, count));
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

    private sealed record FolderMenuItem(string DisplayName, EagleFolder Folder, int Depth, int Count);

    private sealed record PickerOption(
        string Id,
        string Title,
        string Detail,
        bool IsSelected = false,
        string Eyebrow = "",
        int Depth = 0);

    private sealed record TagCount(string Name, int Count);

    private sealed class OptionPickerView : ContentView
    {
        private readonly TaskCompletionSource<PickerOption?> _completion = new();
        private readonly IReadOnlyList<PickerOption> _options;
        private readonly VerticalStackLayout _list = new() { Padding = new Thickness(14, 8, 14, 18), Spacing = 8 };
        private readonly string _emptyText;

        public Task<PickerOption?> Completion => _completion.Task;

        public OptionPickerView(string title, string subtitle, IReadOnlyList<PickerOption> options, string searchPlaceholder)
        {
            _options = options;
            _emptyText = title + "がありません";
            BackgroundColor = Color.FromArgb("#F00B0E12");

            var closeButton = CreateSheetButton("閉じる", Color.FromArgb("#172029"), Colors.White);
            closeButton.Clicked += (_, _) => Complete(null);

            var header = new Grid
            {
                Padding = new Thickness(16, 16, 16, 10),
                ColumnDefinitions =
                {
                    new ColumnDefinition(GridLength.Star),
                    new ColumnDefinition(GridLength.Auto)
                },
                Children =
                {
                    new VerticalStackLayout
                    {
                        Spacing = 2,
                        Children =
                        {
                            new Label { Text = title, TextColor = Colors.White, FontSize = 22, FontAttributes = FontAttributes.Bold },
                            new Label { Text = subtitle, TextColor = Color.FromArgb("#98A4B5"), FontSize = 12 }
                        }
                    },
                    closeButton
                }
            };
            Grid.SetColumn(closeButton, 1);

            var search = new SearchBar
            {
                Placeholder = searchPlaceholder,
                TextColor = Colors.White,
                PlaceholderColor = Color.FromArgb("#667386"),
                CancelButtonColor = Color.FromArgb("#21C7A8"),
                BackgroundColor = Color.FromArgb("#12171D"),
                Margin = new Thickness(14, 0, 14, 8)
            };
            search.TextChanged += (_, e) => Render(e.NewTextValue ?? string.Empty);

            var scroll = new ScrollView { Content = _list };
            var root = new Grid
            {
                RowDefinitions =
                {
                    new RowDefinition(GridLength.Auto),
                    new RowDefinition(GridLength.Auto),
                    new RowDefinition(GridLength.Star)
                },
                Children = { header, search, scroll }
            };
            Grid.SetRow(search, 1);
            Grid.SetRow(scroll, 2);
            Content = root;
            Render(string.Empty);
        }

        private void Render(string filter)
        {
            _list.Children.Clear();
            var visibleOptions = _options
                .Where(option => string.IsNullOrWhiteSpace(filter)
                    || option.Title.Contains(filter, StringComparison.CurrentCultureIgnoreCase)
                    || option.Detail.Contains(filter, StringComparison.CurrentCultureIgnoreCase)
                    || option.Eyebrow.Contains(filter, StringComparison.CurrentCultureIgnoreCase))
                .ToList();

            if (visibleOptions.Count == 0)
            {
                _list.Children.Add(new Label
                {
                    Text = _emptyText,
                    TextColor = Color.FromArgb("#98A4B5"),
                    FontSize = 14,
                    HorizontalTextAlignment = TextAlignment.Center,
                    Margin = new Thickness(0, 28, 0, 0)
                });
                return;
            }

            foreach (var option in visibleOptions)
            {
                _list.Children.Add(CreateOptionRow(option));
            }
        }

        private View CreateOptionRow(PickerOption option)
        {
            var title = new Label
            {
                Text = option.Title,
                TextColor = Colors.White,
                FontSize = 16,
                FontAttributes = FontAttributes.Bold,
                LineBreakMode = LineBreakMode.TailTruncation
            };
            var detail = new Label
            {
                Text = option.Detail,
                TextColor = Color.FromArgb("#98A4B5"),
                FontSize = 12,
                LineBreakMode = LineBreakMode.TailTruncation
            };
            var eyebrow = new Label
            {
                Text = option.Eyebrow,
                TextColor = option.IsSelected ? Color.FromArgb("#21C7A8") : Color.FromArgb("#667386"),
                FontSize = 11,
                FontAttributes = FontAttributes.Bold,
                LineBreakMode = LineBreakMode.TailTruncation
            };
            var selected = new Label
            {
                Text = option.IsSelected ? "選択中" : string.Empty,
                TextColor = Color.FromArgb("#21C7A8"),
                FontSize = 12,
                FontAttributes = FontAttributes.Bold,
                VerticalTextAlignment = TextAlignment.Center
            };
            var content = new VerticalStackLayout
            {
                Spacing = 2,
                Children = { eyebrow, title, detail }
            };
            var row = new Grid
            {
                Padding = new Thickness(12 + Math.Min(option.Depth, 8) * 16, 10, 12, 10),
                ColumnDefinitions =
                {
                    new ColumnDefinition(GridLength.Star),
                    new ColumnDefinition(GridLength.Auto)
                },
                ColumnSpacing = 10,
                Children = { content, selected }
            };
            Grid.SetColumn(selected, 1);

            var border = new Border
            {
                BackgroundColor = option.IsSelected ? Color.FromArgb("#162B29") : Color.FromArgb("#10161D"),
                Stroke = option.IsSelected ? Color.FromArgb("#21C7A8") : Color.FromArgb("#23303C"),
                StrokeThickness = 1,
                StrokeShape = new RoundRectangle { CornerRadius = 8 },
                Content = row
            };
            var tap = new TapGestureRecognizer();
            tap.Tapped += (_, _) => Complete(option);
            border.GestureRecognizers.Add(tap);
            return border;
        }

        public void Cancel()
        {
            Complete(null);
        }

        private void Complete(PickerOption? selected)
        {
            _completion.TrySetResult(selected);
        }
    }

    private sealed class TagPopupView : ContentView
    {
        private readonly TaskCompletionSource<List<string>?> _completion = new();
        private readonly HashSet<string> _selected;
        private readonly IReadOnlyList<TagCount> _tags;
        private readonly VerticalStackLayout _list = new() { Padding = new Thickness(14, 8, 14, 86), Spacing = 8 };
        private readonly Label _selectedLabel = new()
        {
            TextColor = Color.FromArgb("#98A4B5"),
            FontSize = 12,
            VerticalTextAlignment = TextAlignment.Center
        };

        public Task<List<string>?> Completion => _completion.Task;

        public TagPopupView(IReadOnlyList<TagCount> tags, IEnumerable<string> selectedTags, int assetCount)
        {
            _tags = tags;
            _selected = new HashSet<string>(selectedTags, StringComparer.CurrentCultureIgnoreCase);
            BackgroundColor = Color.FromArgb("#F00B0E12");

            var closeButton = CreateSheetButton("閉じる", Color.FromArgb("#172029"), Colors.White);
            closeButton.Clicked += (_, _) => Complete(null);

            var header = new Grid
            {
                Padding = new Thickness(16, 16, 16, 10),
                ColumnDefinitions =
                {
                    new ColumnDefinition(GridLength.Star),
                    new ColumnDefinition(GridLength.Auto)
                },
                Children =
                {
                    new VerticalStackLayout
                    {
                        Spacing = 2,
                        Children =
                        {
                            new Label { Text = "タグ", TextColor = Colors.White, FontSize = 22, FontAttributes = FontAttributes.Bold },
                            new Label { Text = $"複数選択  {assetCount:N0} items", TextColor = Color.FromArgb("#98A4B5"), FontSize = 12 }
                        }
                    },
                    closeButton
                }
            };
            Grid.SetColumn(closeButton, 1);

            var search = new SearchBar
            {
                Placeholder = "タグを検索",
                TextColor = Colors.White,
                PlaceholderColor = Color.FromArgb("#667386"),
                CancelButtonColor = Color.FromArgb("#21C7A8"),
                BackgroundColor = Color.FromArgb("#12171D"),
                Margin = new Thickness(14, 0, 14, 8)
            };
            search.TextChanged += (_, e) => Render(e.NewTextValue ?? string.Empty);

            var clearButton = CreateSheetButton("クリア", Color.FromArgb("#172029"), Colors.White);
            clearButton.Clicked += (_, _) =>
            {
                _selected.Clear();
                Render(search.Text ?? string.Empty);
            };
            var doneButton = CreateSheetButton("完了", Color.FromArgb("#21C7A8"), Color.FromArgb("#06130F"));
            doneButton.Clicked += (_, _) => Complete(_selected.OrderBy(tag => tag, StringComparer.CurrentCultureIgnoreCase).ToList());

            var footer = new Grid
            {
                Padding = new Thickness(14, 10),
                BackgroundColor = Color.FromArgb("#D80B0E12"),
                ColumnDefinitions =
                {
                    new ColumnDefinition(GridLength.Star),
                    new ColumnDefinition(GridLength.Auto),
                    new ColumnDefinition(GridLength.Auto)
                },
                ColumnSpacing = 8,
                Children = { _selectedLabel, clearButton, doneButton }
            };
            Grid.SetColumn(clearButton, 1);
            Grid.SetColumn(doneButton, 2);

            var scroll = new ScrollView { Content = _list };
            var root = new Grid
            {
                RowDefinitions =
                {
                    new RowDefinition(GridLength.Auto),
                    new RowDefinition(GridLength.Auto),
                    new RowDefinition(GridLength.Star),
                    new RowDefinition(GridLength.Auto)
                },
                Children = { header, search, scroll, footer }
            };
            Grid.SetRow(search, 1);
            Grid.SetRow(scroll, 2);
            Grid.SetRow(footer, 3);
            Content = root;
            Render(string.Empty);
        }

        private void Render(string filter)
        {
            _list.Children.Clear();
            var visibleTags = _tags
                .Where(tag => string.IsNullOrWhiteSpace(filter) || tag.Name.Contains(filter, StringComparison.CurrentCultureIgnoreCase))
                .ToList();
            foreach (var tag in visibleTags)
            {
                _list.Children.Add(CreateTagRow(tag));
            }

            if (visibleTags.Count == 0)
            {
                _list.Children.Add(new Label
                {
                    Text = "タグがありません",
                    TextColor = Color.FromArgb("#98A4B5"),
                    FontSize = 14,
                    HorizontalTextAlignment = TextAlignment.Center,
                    Margin = new Thickness(0, 28, 0, 0)
                });
            }

            UpdateSelectedLabel();
        }

        private View CreateTagRow(TagCount tag)
        {
            var isSelected = _selected.Contains(tag.Name);
            var checkBox = new CheckBox
            {
                IsChecked = isSelected,
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

                UpdateSelectedLabel();
            };

            var row = new Grid
            {
                Padding = new Thickness(12, 10),
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
            var border = new Border
            {
                BackgroundColor = isSelected ? Color.FromArgb("#162B29") : Color.FromArgb("#10161D"),
                Stroke = isSelected ? Color.FromArgb("#21C7A8") : Color.FromArgb("#23303C"),
                StrokeThickness = 1,
                StrokeShape = new RoundRectangle { CornerRadius = 8 },
                Content = row
            };
            var tap = new TapGestureRecognizer();
            tap.Tapped += (_, _) => checkBox.IsChecked = !checkBox.IsChecked;
            border.GestureRecognizers.Add(tap);
            return border;
        }

        private void UpdateSelectedLabel()
        {
            _selectedLabel.Text = _selected.Count == 0 ? "タグ未選択" : $"{_selected.Count:N0} selected";
        }

        public void Cancel()
        {
            Complete(null);
        }

        private void Complete(List<string>? selectedTags)
        {
            _completion.TrySetResult(selectedTags);
        }
    }

    private static Button CreateSheetButton(string text, Color background, Color textColor)
    {
        return new Button
        {
            Text = text,
            FontSize = 13,
            FontAttributes = FontAttributes.Bold,
            TextColor = textColor,
            BackgroundColor = background,
            BorderColor = Color.FromArgb("#2B3948"),
            BorderWidth = 1,
            CornerRadius = 8,
            HeightRequest = 38,
            Padding = new Thickness(14, 0)
        };
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
