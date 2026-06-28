using Android.Media;
using AndroidUri = Android.Net.Uri;
using CoffeeEagle.Reader.Models;
using CoffeeEagle.Reader.Services;

namespace CoffeeEagle.Reader.Pages;

public sealed class AudioPlayerPage : ContentPage
{
    private readonly IReadOnlyList<EagleAsset> _assets;
    private int _index;
    private readonly EagleImageSourceService _mediaSources;
    private readonly EagleLibraryStore _store;
    private MediaPlayer? _player;
    private bool _isPrepared;
    private bool _isSeeking;
    private bool _disposed;
    private bool _continuousPlayback = true;

    private readonly Label _titleLabel = new()
    {
        TextColor = Colors.White,
        FontSize = 18,
        FontAttributes = FontAttributes.Bold,
        HorizontalTextAlignment = TextAlignment.Center,
        LineBreakMode = LineBreakMode.TailTruncation,
        MaxLines = 2
    };

    private readonly Label _metaLabel = new()
    {
        TextColor = Color.FromArgb("#98A4B5"),
        FontSize = 13,
        HorizontalTextAlignment = TextAlignment.Center
    };

    private readonly Slider _positionSlider = new()
    {
        Minimum = 0,
        Maximum = 1,
        MinimumTrackColor = Color.FromArgb("#21C7A8"),
        MaximumTrackColor = Color.FromArgb("#2B3948"),
        ThumbColor = Colors.White
    };

    private readonly Label _timeLabel = new()
    {
        TextColor = Color.FromArgb("#B7C1CE"),
        FontSize = 13,
        HorizontalTextAlignment = TextAlignment.Center
    };

    private readonly Button _playButton = CreateControlButton("再生", width: 96);
    private readonly Button _previousButton = CreateControlButton("前", width: 64);
    private readonly Button _nextButton = CreateControlButton("次", width: 64);
    private readonly Button _continuousButton = CreateControlButton("連続 ON", width: 96);

    public AudioPlayerPage(IReadOnlyList<EagleAsset> assets, int startIndex, EagleImageSourceService mediaSources, EagleLibraryStore store)
    {
        _mediaSources = mediaSources;
        _store = store;
        _assets = assets.Where(asset => asset.MediaKind == EagleAssetMediaKind.Audio).ToList();
        var startAsset = assets.ElementAtOrDefault(startIndex);
        _index = startAsset is null ? 0 : Math.Max(0, _assets.ToList().FindIndex(asset => asset.Id == startAsset.Id));

        Title = "Audio";
        BackgroundColor = Color.FromArgb("#0B0E12");
        NavigationPage.SetHasNavigationBar(this, false);
        Content = CreateLayout();
        WireEvents();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        var state = await _store.LoadAsync();
        _continuousPlayback = state.AudioContinuousPlayback;
        UpdateContinuousButton();

        if (_player is null)
        {
            await LoadCurrentAsync(autoPlay: true);
            StartPositionTimer();
        }
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        DisposePlayer();
    }

    private View CreateLayout()
    {
        var backButton = CreateControlButton("戻る", width: 72);
        backButton.Clicked += async (_, _) => await Navigation.PopAsync();

        var top = new Grid
        {
            Padding = new Thickness(14, 12),
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Star)
            },
            Children = { backButton, _metaLabel }
        };
        Grid.SetColumn(_metaLabel, 1);

        var transportControls = new HorizontalStackLayout
        {
            Spacing = 12,
            HorizontalOptions = LayoutOptions.Center,
            Children = { _previousButton, _playButton, _nextButton }
        };

        var optionControls = new HorizontalStackLayout
        {
            HorizontalOptions = LayoutOptions.Center,
            Children = { _continuousButton }
        };

        var panel = new VerticalStackLayout
        {
            Padding = new Thickness(22),
            Spacing = 22,
            VerticalOptions = LayoutOptions.Center,
            Children =
            {
                new Label
                {
                    Text = "AUDIO",
                    TextColor = Color.FromArgb("#21C7A8"),
                    FontSize = 13,
                    FontAttributes = FontAttributes.Bold,
                    HorizontalTextAlignment = TextAlignment.Center
                },
                _titleLabel,
                _positionSlider,
                _timeLabel,
                transportControls,
                optionControls
            }
        };

        return new Grid
        {
            RowDefinitions =
            {
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Star)
            },
            Children = { top, panel }
        };
    }

    private void WireEvents()
    {
        _playButton.Clicked += (_, _) => TogglePlayback();
        _previousButton.Clicked += async (_, _) => await MoveAsync(-1);
        _nextButton.Clicked += async (_, _) => await MoveAsync(1);
        _continuousButton.Clicked += async (_, _) => await ToggleContinuousPlaybackAsync();
        _positionSlider.DragStarted += (_, _) => _isSeeking = true;
        _positionSlider.DragCompleted += (_, _) =>
        {
            if (_player is not null && _isPrepared)
            {
                _player.SeekTo((int)_positionSlider.Value);
            }

            _isSeeking = false;
        };
    }

    private async Task LoadCurrentAsync(bool autoPlay)
    {
        DisposePlayer();
        _disposed = false;
        _isPrepared = false;
        _positionSlider.Value = 0;
        _positionSlider.Maximum = 1;
        _playButton.Text = "読込中";
        _playButton.IsEnabled = false;

        if (_assets.Count == 0)
        {
            _titleLabel.Text = "再生できる音声がありません";
            _metaLabel.Text = string.Empty;
            _timeLabel.Text = "00:00 / 00:00";
            return;
        }

        var asset = _assets[_index];
        _titleLabel.Text = asset.Name;
        _metaLabel.Text = $"{_index + 1:N0} / {_assets.Count:N0}  {asset.Extension?.TrimStart('.').ToUpperInvariant()}  {FormatBytes(asset.SizeBytes)}";
        _timeLabel.Text = "00:00 / 00:00";

        var uriString = asset.FileUri ?? asset.ThumbnailUri;
        if (string.IsNullOrWhiteSpace(uriString))
        {
            await DisplayAlertAsync("再生できません", "音声ファイルのURIがありません。", "OK");
            _playButton.Text = "再生";
            return;
        }

        try
        {
            var activity = MainActivity.Current ?? throw new InvalidOperationException("Android activity is not ready.");
            var playbackPath = await _mediaSources.GetPlaybackPathAsync(asset);
            var uri = GoogleDriveLibraryService.IsDriveFileUri(uriString)
                ? null
                : AndroidUri.Parse(playbackPath) ?? throw new InvalidOperationException("URIを読み取れませんでした。");
            _player = new MediaPlayer();
            _player.Prepared += (_, _) =>
            {
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    if (_player is null)
                    {
                        return;
                    }

                    _isPrepared = true;
                    _positionSlider.Maximum = Math.Max(1, _player.Duration);
                    _playButton.IsEnabled = true;
                    _playButton.Text = autoPlay ? "一時停止" : "再生";
                    UpdateTimeLabel();
                    if (autoPlay)
                    {
                        _player.Start();
                    }
                });
            };
            _player.Completion += (_, _) => MainThread.BeginInvokeOnMainThread(async () => await HandleCompletionAsync());
            if (uri is null)
            {
                _player.SetDataSource(playbackPath);
            }
            else
            {
                _player.SetDataSource(activity, uri);
            }
            _player.PrepareAsync();
        }
        catch (Exception ex)
        {
            DisposePlayer();
            _playButton.Text = "再生";
            _playButton.IsEnabled = true;
            await DisplayAlertAsync("再生できません", ex.Message, "OK");
        }
    }

    private async Task HandleCompletionAsync()
    {
        if (_continuousPlayback && _index < _assets.Count - 1)
        {
            await MoveAsync(1);
            return;
        }

        if (_player is null || !_isPrepared)
        {
            return;
        }

        try
        {
            _player.SeekTo(0);
        }
        catch
        {
            // Ignore player state races at completion.
        }

        _positionSlider.Value = 0;
        _playButton.Text = "再生";
        UpdateTimeLabel();
    }

    private async Task ToggleContinuousPlaybackAsync()
    {
        _continuousPlayback = !_continuousPlayback;
        UpdateContinuousButton();

        var state = await _store.LoadAsync();
        state.AudioContinuousPlayback = _continuousPlayback;
        await _store.SaveAsync(state);
    }

    private void UpdateContinuousButton()
    {
        _continuousButton.Text = _continuousPlayback ? "連続 ON" : "連続 OFF";
        _continuousButton.BackgroundColor = _continuousPlayback
            ? Color.FromArgb("#21423D")
            : Color.FromArgb("#172029");
    }

    private async Task MoveAsync(int delta)
    {
        if (_assets.Count == 0)
        {
            return;
        }

        var next = Math.Clamp(_index + delta, 0, _assets.Count - 1);
        if (next == _index && delta != 0)
        {
            return;
        }

        _index = next;
        await LoadCurrentAsync(autoPlay: true);
    }

    private void TogglePlayback()
    {
        if (_player is null || !_isPrepared)
        {
            return;
        }

        if (_player.IsPlaying)
        {
            _player.Pause();
            _playButton.Text = "再生";
        }
        else
        {
            _player.Start();
            _playButton.Text = "一時停止";
        }
    }

    private void StartPositionTimer()
    {
        Dispatcher.StartTimer(TimeSpan.FromMilliseconds(500), () =>
        {
            if (_disposed)
            {
                return false;
            }

            if (_player is not null && _isPrepared)
            {
                if (!_isSeeking)
                {
                    _positionSlider.Value = Math.Clamp(_player.CurrentPosition, 0, _positionSlider.Maximum);
                }

                _playButton.Text = _player.IsPlaying ? "一時停止" : "再生";
                UpdateTimeLabel();
            }

            return true;
        });
    }

    private void UpdateTimeLabel()
    {
        if (_player is null || !_isPrepared)
        {
            _timeLabel.Text = "00:00 / 00:00";
            return;
        }

        _timeLabel.Text = $"{FormatTime(_player.CurrentPosition)} / {FormatTime(_player.Duration)}";
    }

    private void DisposePlayer()
    {
        _disposed = true;
        _isPrepared = false;
        if (_player is null)
        {
            return;
        }

        try
        {
            if (_player.IsPlaying)
            {
                _player.Stop();
            }
        }
        catch
        {
            // Ignore player state races during page navigation.
        }

        _player.Release();
        _player.Dispose();
        _player = null;
    }

    private static string FormatTime(int milliseconds)
    {
        var time = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
        return time.TotalHours >= 1
            ? $"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}"
            : $"{time.Minutes:00}:{time.Seconds:00}";
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0)
        {
            return string.Empty;
        }

        string[] units = ["B", "KB", "MB", "GB"];
        var size = (double)bytes;
        var unitIndex = 0;
        while (size >= 1024 && unitIndex < units.Length - 1)
        {
            size /= 1024;
            unitIndex++;
        }

        return unitIndex == 0 ? $"{bytes} B" : $"{size:0.0} {units[unitIndex]}";
    }

    private static Button CreateControlButton(string text, double width)
    {
        return new Button
        {
            Text = text,
            WidthRequest = width,
            HeightRequest = 42,
            Padding = new Thickness(10, 0),
            FontSize = 14,
            FontAttributes = FontAttributes.Bold,
            TextColor = Colors.White,
            BackgroundColor = Color.FromArgb("#172029"),
            BorderColor = Color.FromArgb("#2B3948"),
            BorderWidth = 1,
            CornerRadius = 8
        };
    }
}

