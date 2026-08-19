using CoffeeEagle.Reader.Models;
using CoffeeEagle.Reader.Services;
using Microsoft.Maui.Controls.Shapes;

namespace CoffeeEagle.Reader.Pages;

public sealed partial class BookshelfPage : ContentPage
{

    private const string AllFoldersId = "__all__";

    private const string UnfiledFoldersId = "__unfiled__";

    private const string RemoveLibraryPrefix = "__remove_library__:";


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

    private readonly Label _syncPhaseLabel = new()
    {
        FontSize = 13,
        FontAttributes = FontAttributes.Bold,
        TextColor = Color.FromArgb("#21C7A8")
    };

    private readonly Label _syncTargetLabel = new()
    {
        FontSize = 12,
        TextColor = Colors.White,
        LineBreakMode = LineBreakMode.TailTruncation,
        MaxLines = 2
    };

    private readonly Label _syncMetaLabel = new()
    {
        FontSize = 11,
        TextColor = Color.FromArgb("#98A4B5"),
        LineBreakMode = LineBreakMode.WordWrap
    };

    private readonly Label _syncPercentLabel = new()
    {
        FontSize = 12,
        FontAttributes = FontAttributes.Bold,
        TextColor = Color.FromArgb("#21C7A8"),
        HorizontalTextAlignment = TextAlignment.End,
        VerticalTextAlignment = TextAlignment.Center,
        IsVisible = false
    };

    private readonly ProgressBar _syncProgressBar = new()
    {
        Progress = 0,
        ProgressColor = Color.FromArgb("#21C7A8"),
        BackgroundColor = Color.FromArgb("#26343A"),
        HeightRequest = 6,
        IsVisible = false
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

    private readonly Grid _startupLayer = new()
    {
        BackgroundColor = Color.FromArgb("#0B0E12"),
        InputTransparent = false,
        Opacity = 1
    };

    private readonly Image _startupIcon = new()
    {
        Source = ImageSource.FromFile("icon.png"),
        Aspect = Aspect.AspectFit,
        WidthRequest = 300,
        HeightRequest = 300,
        HorizontalOptions = LayoutOptions.Center,
        VerticalOptions = LayoutOptions.Center,
        Scale = 0.9,
        Opacity = 0
    };

    private readonly Button _libraryButton;

    private readonly Button _folderButton;

    private readonly Button _tagButton;

    private readonly Button _densityButton;

    private readonly Button _refreshButton;

    private readonly VerticalStackLayout _emptyActions;

    private readonly Button _googleDriveSelectButton;

    private readonly Button _deviceFolderSelectButton;

    private readonly Border _syncStatusPanel;

    private EagleReaderState _state = new();

    private EagleLibrary? _activeLibrary;

    private bool _loaded;

    private bool _isBusy;

    private bool _playedStartupAnimation;

    private Action? _activeSheetCancel;

    private IDispatcherTimer? _syncTimer;

    private LibrarySyncProgress? _lastSyncProgress;

    private DateTimeOffset _syncStartedAt;

    private DateTimeOffset _syncLastUpdatedAt;

    private bool _acceptSyncProgress;

    private long _syncGeneration;


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
        _syncStatusPanel = CreateSyncStatusPanel();
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
        _ = PlayStartupAnimationAsync();
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

        if (asset.MediaKind == EagleAssetMediaKind.Video)
        {
            SetBusy(true, "動画を準備中...");
            try
            {
                await _imageSources.OpenVideoAsync(asset);
            }
            catch (Exception ex)
            {
                await DisplayAlertAsync("再生できません", ex.Message, "OK");
            }
            finally
            {
                SetBusy(false);
            }

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
}
