using CoffeeEagle.Reader.Models;
using CoffeeEagle.Reader.Services;

namespace CoffeeEagle.Reader.Pages;

public sealed class ViewerPage : ContentPage
{
    private readonly IReadOnlyList<EagleAsset> _assets;
    private readonly EagleImageSourceService _imageSources;
    private readonly EagleLibraryStore _store;
    private const uint PageTurnDuration = 170;
    private const int PageTransitionPrepareMilliseconds = 24;
    private const int AdjacentPreloadRadius = 2;

    private readonly Image _frontImage = CreateViewerImage();
    private readonly Image _backImage = CreateViewerImage();
    private readonly Grid _imageDeck;
    private readonly Grid _chrome = new()
    {
        BackgroundColor = Colors.Transparent,
        Padding = new Thickness(12, 8)
    };
    private readonly Label _titleLabel = new()
    {
        TextColor = Colors.White,
        FontAttributes = FontAttributes.Bold,
        FontSize = 15,
        LineBreakMode = LineBreakMode.TailTruncation,
        VerticalTextAlignment = TextAlignment.Center
    };
    private readonly Label _counterLabel = new()
    {
        TextColor = Color.FromArgb("#B7C1CE"),
        FontSize = 12,
        HorizontalTextAlignment = TextAlignment.Center,
        VerticalTextAlignment = TextAlignment.Center
    };
    private readonly Label _tagLabel = new()
    {
        TextColor = Color.FromArgb("#D6A73B"),
        FontSize = 12,
        LineBreakMode = LineBreakMode.TailTruncation,
        MaxLines = 1
    };
    private readonly Button _autoButton = CreateControlButton("自動 OFF", width: 82, fontSize: 13);
    private readonly Button _intervalButton = CreateControlButton("5秒", width: 58, fontSize: 13);

    private int _index;
    private bool _chromeVisible = true;
    private bool _autoAdvanceEnabled;
    private int _autoAdvanceSeconds = 5;
    private int _autoTimerVersion;
    private bool _isAnimating;
    private Image _activeImage = null!;
    private Image _bufferImage = null!;
    private CancellationTokenSource? _preloadCancellation;

    public ViewerPage(IReadOnlyList<EagleAsset> assets, int startIndex, EagleImageSourceService imageSources, EagleLibraryStore store)
    {
        _assets = assets;
        _imageSources = imageSources;
        _store = store;
        _index = Math.Clamp(startIndex, 0, Math.Max(0, assets.Count - 1));

        BackgroundColor = Colors.Black;
        NavigationPage.SetHasNavigationBar(this, false);
        _activeImage = _frontImage;
        _bufferImage = _backImage;
        _bufferImage.IsVisible = false;
        _imageDeck = new Grid
        {
            BackgroundColor = Colors.Black,
            HorizontalOptions = LayoutOptions.Fill,
            VerticalOptions = LayoutOptions.Fill,
            Children = { _frontImage, _backImage }
        };
        Content = CreateLayout();
        AddGestures();
        ShowAsset();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        var state = await _store.LoadAsync();
        _autoAdvanceEnabled = state.ImageAutoAdvanceEnabled;
        _autoAdvanceSeconds = Math.Clamp(state.ImageAutoAdvanceSeconds <= 0 ? 5 : state.ImageAutoAdvanceSeconds, 1, 600);
        UpdateAutoControls();
        RestartAutoAdvanceTimer();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _autoTimerVersion++;
        _preloadCancellation?.Cancel();
        _preloadCancellation = null;
    }

    private View CreateLayout()
    {
        var backButton = CreateControlButton("←", width: 42, fontSize: 24);
        backButton.Clicked += async (_, _) => await Navigation.PopAsync();

        var previousButton = CreateControlButton("‹", width: 42, fontSize: 24);
        previousButton.Clicked += async (_, _) => await MoveAsync(-1);

        var nextButton = CreateControlButton("›", width: 42, fontSize: 24);
        nextButton.Clicked += async (_, _) => await MoveAsync(1);
        _autoButton.Clicked += async (_, _) => await ToggleAutoAdvanceAsync();
        _intervalButton.Clicked += async (_, _) => await ChangeAutoAdvanceSecondsAsync();

        var top = new Grid
        {
            Padding = new Thickness(8, 6),
            BackgroundColor = Color.FromArgb("#B00B0E12"),
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto)
            },
            ColumnSpacing = 10,
            Children = { backButton, _titleLabel, _counterLabel }
        };
        Grid.SetColumn(_titleLabel, 1);
        Grid.SetColumn(_counterLabel, 2);

        var bottom = new Grid
        {
            Padding = new Thickness(8, 6),
            BackgroundColor = Color.FromArgb("#B00B0E12"),
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Auto)
            },
            ColumnSpacing = 8,
            Children = { previousButton, _tagLabel, _intervalButton, _autoButton, nextButton }
        };
        Grid.SetColumn(_tagLabel, 1);
        Grid.SetColumn(_intervalButton, 2);
        Grid.SetColumn(_autoButton, 3);
        Grid.SetColumn(nextButton, 4);

        _chrome.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        _chrome.RowDefinitions.Add(new RowDefinition(GridLength.Star));
        _chrome.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        _chrome.Children.Add(top);
        _chrome.Children.Add(bottom);
        Grid.SetRow(bottom, 2);

        return new Grid
        {
            Children = { _imageDeck, _chrome }
        };
    }

    private void AddGestures()
    {
        if (Content is View root)
        {
            AddViewerGestures(root);
        }

        AddViewerGestures(_imageDeck);
    }

    private void AddViewerGestures(View view)
    {
        view.GestureRecognizers.Add(CreateSwipeGesture(SwipeDirection.Left, 1));
        view.GestureRecognizers.Add(CreateSwipeGesture(SwipeDirection.Right, -1));
        view.GestureRecognizers.Add(CreateTapGesture());
    }

    private SwipeGestureRecognizer CreateSwipeGesture(SwipeDirection direction, int delta)
    {
        var gesture = new SwipeGestureRecognizer { Direction = direction };
        gesture.Swiped += async (_, _) => await MoveAsync(delta);
        return gesture;
    }

    private TapGestureRecognizer CreateTapGesture()
    {
        var gesture = new TapGestureRecognizer();
        gesture.Tapped += (_, _) => ToggleChrome();
        return gesture;
    }

    private async Task MoveAsync(int delta, bool wrap = false)
    {
        if (_assets.Count == 0 || _isAnimating)
        {
            return;
        }

        var next = _index + delta;
        if (wrap)
        {
            if (next >= _assets.Count)
            {
                next = 0;
            }
            else if (next < 0)
            {
                next = _assets.Count - 1;
            }
        }
        else
        {
            next = Math.Clamp(next, 0, _assets.Count - 1);
        }

        if (next == _index)
        {
            return;
        }

        await ShowAssetAsync(next, delta >= 0 ? 1 : -1);
    }

    private async Task ShowAssetAsync(int nextIndex, int direction)
    {
        _isAnimating = true;
        _preloadCancellation?.Cancel();
        var outgoing = _activeImage;
        var incoming = _bufferImage;
        var slideDistance = GetSlideDistance();

        try
        {
            incoming.Source = await CreatePreparedFullSourceAsync(_assets[nextIndex]);
            incoming.IsVisible = true;
            incoming.Opacity = 1;
            incoming.TranslationX = direction * slideDistance;
            outgoing.IsVisible = true;
            outgoing.Opacity = 1;
            outgoing.TranslationX = 0;

            await Task.Delay(PageTransitionPrepareMilliseconds);
            await Task.WhenAll(
                outgoing.TranslateToAsync(-direction * slideDistance, 0, PageTurnDuration, Easing.CubicOut),
                incoming.TranslateToAsync(0, 0, PageTurnDuration, Easing.CubicOut));

            _index = nextIndex;
            _activeImage = incoming;
            _bufferImage = outgoing;
            _bufferImage.IsVisible = false;
            _bufferImage.Source = null;
            UpdateAssetMetadata();
            ScheduleAdjacentPreload();
        }
        finally
        {
            _activeImage.IsVisible = true;
            _activeImage.Opacity = 1;
            _activeImage.TranslationX = 0;
            _bufferImage.Opacity = 1;
            _bufferImage.TranslationX = 0;
            _isAnimating = false;
        }
    }

    private void ShowAsset(bool resetVisualState = true)
    {
        if (_assets.Count == 0)
        {
            _activeImage.Source = null;
            _bufferImage.Source = null;
            _titleLabel.Text = string.Empty;
            _counterLabel.Text = "0 / 0";
            _tagLabel.Text = string.Empty;
            return;
        }

        var asset = _assets[_index];
        _activeImage.Source = _imageSources.CreateFullSource(asset);
        UpdateAssetMetadata();

        if (resetVisualState)
        {
            ResetImageLayer(_activeImage, visible: true, clearSource: false);
            ResetImageLayer(_bufferImage, visible: false, clearSource: true);
        }

        ScheduleAdjacentPreload();
    }

    private void UpdateAssetMetadata()
    {
        if (_assets.Count == 0)
        {
            return;
        }

        var asset = _assets[_index];
        _titleLabel.Text = asset.Name;
        _counterLabel.Text = $"{_index + 1:N0} / {_assets.Count:N0}";
        _tagLabel.Text = asset.Tags.Count == 0 ? string.Empty : string.Join("  ", asset.Tags);
    }

    private static void ResetImageLayer(Image image, bool visible, bool clearSource)
    {
        image.IsVisible = visible;
        image.Opacity = 1;
        image.TranslationX = 0;
        if (clearSource)
        {
            image.Source = null;
        }
    }

    private double GetSlideDistance()
    {
        var width = _imageDeck.Width > 0 ? _imageDeck.Width : Width;
        return Math.Max(width > 0 ? width : 360, 1);
    }

    private async Task<ImageSource?> CreatePreparedFullSourceAsync(EagleAsset asset)
    {
        try
        {
            await _imageSources.PreloadAsync(asset);
        }
        catch
        {
            // Source creation below still handles normal fallback behavior.
        }

        return _imageSources.CreateFullSource(asset);
    }

    private void ScheduleAdjacentPreload()
    {
        _preloadCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _preloadCancellation = cancellation;

        var assets = GetAdjacentPreloadAssets().ToArray();
        if (assets.Length == 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            foreach (var asset in assets)
            {
                if (cancellation.IsCancellationRequested)
                {
                    return;
                }

                try
                {
                    await _imageSources.PreloadAsync(asset, cancellation.Token);
                }
                catch
                {
                    // Adjacent preloading is opportunistic.
                }
            }
        }, cancellation.Token);
    }

    private IEnumerable<EagleAsset> GetAdjacentPreloadAssets()
    {
        if (_assets.Count <= 1)
        {
            yield break;
        }

        for (var offset = -AdjacentPreloadRadius; offset <= AdjacentPreloadRadius; offset++)
        {
            if (offset == 0)
            {
                continue;
            }

            var index = _index + offset;
            if (index < 0 || index >= _assets.Count)
            {
                continue;
            }

            yield return _assets[index];
        }
    }

    private async Task ToggleAutoAdvanceAsync()
    {
        _autoAdvanceEnabled = !_autoAdvanceEnabled;
        UpdateAutoControls();
        RestartAutoAdvanceTimer();
        await SaveViewerSettingsAsync();
    }

    private async Task ChangeAutoAdvanceSecondsAsync()
    {
        var input = await DisplayPromptAsync(
            "自動めくり",
            "秒数を入力してください。",
            initialValue: _autoAdvanceSeconds.ToString(),
            keyboard: Keyboard.Numeric);
        if (string.IsNullOrWhiteSpace(input))
        {
            return;
        }

        if (!int.TryParse(input.Trim(), out var seconds))
        {
            await DisplayAlertAsync("自動めくり", "秒数は数値で入力してください。", "OK");
            return;
        }

        _autoAdvanceSeconds = Math.Clamp(seconds, 1, 600);
        UpdateAutoControls();
        RestartAutoAdvanceTimer();
        await SaveViewerSettingsAsync();
    }

    private async Task SaveViewerSettingsAsync()
    {
        var state = await _store.LoadAsync();
        state.ImageAutoAdvanceEnabled = _autoAdvanceEnabled;
        state.ImageAutoAdvanceSeconds = _autoAdvanceSeconds;
        await _store.SaveAsync(state);
    }

    private void RestartAutoAdvanceTimer()
    {
        _autoTimerVersion++;
        if (!_autoAdvanceEnabled || _assets.Count <= 1)
        {
            return;
        }

        var timerVersion = _autoTimerVersion;
        Dispatcher.StartTimer(TimeSpan.FromSeconds(_autoAdvanceSeconds), () =>
        {
            if (timerVersion != _autoTimerVersion || !_autoAdvanceEnabled)
            {
                return false;
            }

            MainThread.BeginInvokeOnMainThread(async () =>
            {
                if (timerVersion == _autoTimerVersion && _autoAdvanceEnabled)
                {
                    await MoveAsync(1, wrap: true);
                }
            });
            return timerVersion == _autoTimerVersion && _autoAdvanceEnabled;
        });
    }

    private void UpdateAutoControls()
    {
        _autoButton.Text = _autoAdvanceEnabled ? "自動 ON" : "自動 OFF";
        _autoButton.BackgroundColor = _autoAdvanceEnabled
            ? Color.FromArgb("#21423D")
            : Color.FromArgb("#172029");
        _intervalButton.Text = $"{_autoAdvanceSeconds}秒";
    }

    private void ToggleChrome()
    {
        _chromeVisible = !_chromeVisible;
        _chrome.IsVisible = _chromeVisible;
    }

    private static Image CreateViewerImage()
    {
        return new Image
        {
            Aspect = Aspect.AspectFit,
            BackgroundColor = Colors.Black,
            HorizontalOptions = LayoutOptions.Fill,
            VerticalOptions = LayoutOptions.Fill
        };
    }

    private static Button CreateControlButton(string text, double width, double fontSize)
    {
        return new Button
        {
            Text = text,
            WidthRequest = width,
            HeightRequest = 38,
            Padding = new Thickness(8, 0),
            FontSize = fontSize,
            FontAttributes = FontAttributes.Bold,
            TextColor = Colors.White,
            BackgroundColor = Color.FromArgb("#172029"),
            BorderColor = Color.FromArgb("#2B3948"),
            BorderWidth = 1,
            CornerRadius = 8
        };
    }
}
