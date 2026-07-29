using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.OS;
using Android.Views;
using Android.Widget;
using AndroidButton = Android.Widget.Button;
using AndroidColor = Android.Graphics.Color;
using AndroidHandler = Android.OS.Handler;
using AndroidUri = Android.Net.Uri;

namespace CoffeeEagle.Reader;

[Activity(
    Label = "Video",
    ScreenOrientation = ScreenOrientation.Unspecified,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public sealed class VideoPlayerActivity : Activity
{
    private const string ExtraSource = "net.coffeewebjp.coffeeeagle.reader.extra.VIDEO_SOURCE";
    private const string ExtraTitle = "net.coffeewebjp.coffeeeagle.reader.extra.VIDEO_TITLE";
    private const string ExtraIsFilePath = "net.coffeewebjp.coffeeeagle.reader.extra.VIDEO_IS_FILE_PATH";
    private const int RewindMilliseconds = 5000;

    private VideoView? _videoView;
    private SeekBar? _seekBar;
    private TextView? _timeView;
    private AndroidButton? _playButton;
    private AndroidButton? _rewindButton;
    private AndroidHandler? _handler;
    private PositionUpdateRunnable? _positionRunnable;
    private int _resumePosition;
    private bool _isPrepared;
    private bool _isUserSeeking;
    private bool _wasPlaying = true;

    public static void Start(string source, string title, bool isFilePath)
    {
        var activity = MainActivity.Current ?? throw new InvalidOperationException("Android activity is not ready.");
        var intent = new Intent(activity, typeof(VideoPlayerActivity));
        intent.PutExtra(ExtraSource, source);
        intent.PutExtra(ExtraTitle, title);
        intent.PutExtra(ExtraIsFilePath, isFilePath);
        intent.AddFlags(ActivityFlags.GrantReadUriPermission);
        activity.StartActivity(intent);
    }

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        Window?.SetFlags(WindowManagerFlags.KeepScreenOn, WindowManagerFlags.KeepScreenOn);
        _resumePosition = savedInstanceState?.GetInt(nameof(_resumePosition), 0) ?? 0;
        _wasPlaying = savedInstanceState?.GetBoolean(nameof(_wasPlaying), true) ?? true;

        var source = Intent?.GetStringExtra(ExtraSource) ?? string.Empty;
        var title = Intent?.GetStringExtra(ExtraTitle) ?? "Video";
        var isFilePath = Intent?.GetBooleanExtra(ExtraIsFilePath, false) ?? false;
        if (string.IsNullOrWhiteSpace(source))
        {
            Toast.MakeText(this, "動画ファイルを開けませんでした。", ToastLength.Short)?.Show();
            Finish();
            return;
        }

        _handler = CreateMainHandler();
        _videoView = new VideoView(this);
        _videoView.Prepared += (_, _) =>
        {
            _isPrepared = true;
            if (_seekBar is not null)
            {
                _seekBar.Max = Math.Max(1, _videoView.Duration);
            }

            if (_resumePosition > 0)
            {
                _videoView.SeekTo(_resumePosition);
            }

            if (_wasPlaying)
            {
                _videoView.Start();
            }

            UpdatePlaybackControls();
            StartPositionUpdates();
        };
        _videoView.Completion += (_, _) =>
        {
            _resumePosition = 0;
            _wasPlaying = false;
            UpdatePlaybackControls();
            UpdatePositionViews();
        };

        SetContentView(CreateLayout(title, _videoView));

        if (isFilePath)
        {
            _videoView.SetVideoPath(source);
        }
        else
        {
            _videoView.SetVideoURI(AndroidUri.Parse(source));
        }

        _videoView.RequestFocus();
    }

    protected override void OnPause()
    {
        StopPositionUpdates();
        if (_videoView is not null)
        {
            _resumePosition = _videoView.CurrentPosition;
            _wasPlaying = _videoView.IsPlaying;
            _videoView.Pause();
            UpdatePlaybackControls();
        }

        base.OnPause();
    }

    protected override void OnResume()
    {
        base.OnResume();
        if (_videoView is not null && _resumePosition > 0)
        {
            _videoView.SeekTo(_resumePosition);
            if (_wasPlaying && _isPrepared)
            {
                _videoView.Start();
            }

            UpdatePlaybackControls();
        }

        if (_isPrepared)
        {
            StartPositionUpdates();
        }
    }

    protected override void OnSaveInstanceState(Bundle outState)
    {
        if (_videoView is not null)
        {
            _resumePosition = _videoView.CurrentPosition;
            _wasPlaying = _videoView.IsPlaying;
        }

        outState.PutInt(nameof(_resumePosition), _resumePosition);
        outState.PutBoolean(nameof(_wasPlaying), _wasPlaying);
        base.OnSaveInstanceState(outState);
    }

    protected override void OnDestroy()
    {
        StopPositionUpdates();
        _videoView?.StopPlayback();
        _videoView = null;
        _seekBar = null;
        _timeView = null;
        _playButton = null;
        _rewindButton = null;
        _handler = null;
        base.OnDestroy();
    }

    private Android.Views.View CreateLayout(string title, VideoView videoView)
    {
        var root = new FrameLayout(this);
        root.SetBackgroundColor(AndroidColor.Black);

        var videoLayout = new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent,
            ViewGroup.LayoutParams.MatchParent,
            GravityFlags.Center);
        root.AddView(videoView, videoLayout);

        root.AddView(CreateTopBar(title), new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent,
            ViewGroup.LayoutParams.WrapContent,
            GravityFlags.Top));

        root.AddView(CreateTransportBar(), new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent,
            ViewGroup.LayoutParams.WrapContent,
            GravityFlags.Bottom));
        return root;
    }

    private Android.Views.View CreateTopBar(string title)
    {
        var backButton = new AndroidButton(this)
        {
            Text = "戻る"
        };
        backButton.SetTextColor(AndroidColor.White);
        backButton.SetBackgroundColor(AndroidColor.Rgb(23, 32, 41));
        backButton.Click += (_, _) => Finish();

        var titleView = new TextView(this)
        {
            Text = title,
            TextSize = 16
        };
        titleView.SetTextColor(AndroidColor.White);
        titleView.SetSingleLine(true);
        titleView.Gravity = GravityFlags.CenterVertical;
        titleView.SetPadding(12, 0, 0, 0);

        var topBar = new LinearLayout(this)
        {
            Orientation = Orientation.Horizontal
        };
        topBar.SetGravity(GravityFlags.CenterVertical);
        topBar.SetPadding(14, 10, 14, 10);
        topBar.SetBackgroundColor(AndroidColor.Argb(188, 11, 14, 18));
        topBar.AddView(backButton, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));
        topBar.AddView(titleView, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1));
        return topBar;
    }

    private Android.Views.View CreateTransportBar()
    {
        _seekBar = new SeekBar(this)
        {
            Max = 1
        };
        _seekBar.ProgressChanged += (_, args) =>
        {
            if ((!_isUserSeeking && !args.FromUser) || _videoView is null || _timeView is null)
            {
                return;
            }

            _timeView.Text = $"{FormatTime(args.Progress)} / {FormatTime(_videoView.Duration)}";
        };
        _seekBar.StartTrackingTouch += (_, _) => _isUserSeeking = true;
        _seekBar.StopTrackingTouch += (_, _) =>
        {
            if (_videoView is not null && _isPrepared && _seekBar is not null)
            {
                _videoView.SeekTo(_seekBar.Progress);
            }

            _isUserSeeking = false;
            UpdatePositionViews();
        };

        _timeView = new TextView(this)
        {
            Text = "00:00 / 00:00",
            TextSize = 13
        };
        _timeView.SetTextColor(AndroidColor.White);
        _timeView.Gravity = GravityFlags.CenterVertical | GravityFlags.Right;

        _rewindButton = CreateTransportButton("-5秒");
        _rewindButton.Click += (_, _) =>
        {
            if (_videoView is null || !_isPrepared)
            {
                return;
            }

            _videoView.SeekTo(Math.Max(0, _videoView.CurrentPosition - RewindMilliseconds));
            UpdatePositionViews();
        };

        _playButton = CreateTransportButton("一時停止");
        _playButton.Click += (_, _) => TogglePlayback();

        var rewindParams = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent)
        {
            RightMargin = 8
        };
        var playParams = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent)
        {
            RightMargin = 12
        };

        var buttonRow = new LinearLayout(this)
        {
            Orientation = Orientation.Horizontal
        };
        buttonRow.SetGravity(GravityFlags.CenterVertical);
        buttonRow.AddView(_rewindButton, rewindParams);
        buttonRow.AddView(_playButton, playParams);
        buttonRow.AddView(_timeView, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1));

        var controls = new LinearLayout(this)
        {
            Orientation = Orientation.Vertical
        };
        controls.SetPadding(16, 8, 16, 14);
        controls.SetBackgroundColor(AndroidColor.Argb(188, 11, 14, 18));
        controls.AddView(_seekBar, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        controls.AddView(buttonRow, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        return controls;
    }

    private AndroidButton CreateTransportButton(string text)
    {
        var button = new AndroidButton(this)
        {
            Text = text
        };
        button.SetTextColor(AndroidColor.White);
        button.SetBackgroundColor(AndroidColor.Rgb(23, 32, 41));
        return button;
    }

    private void TogglePlayback()
    {
        if (_videoView is null || !_isPrepared)
        {
            return;
        }

        if (_videoView.IsPlaying)
        {
            _videoView.Pause();
        }
        else
        {
            if (_videoView.Duration > 0 && _videoView.CurrentPosition >= _videoView.Duration - 750)
            {
                _videoView.SeekTo(0);
            }

            _videoView.Start();
        }

        _wasPlaying = _videoView.IsPlaying;
        UpdatePlaybackControls();
        UpdatePositionViews();
    }

    private void StartPositionUpdates()
    {
        if (_handler is null)
        {
            _handler = CreateMainHandler();
        }

        StopPositionUpdates();
        _positionRunnable = new PositionUpdateRunnable(this);
        _handler.Post(_positionRunnable);
    }

    private void StopPositionUpdates()
    {
        if (_handler is not null && _positionRunnable is not null)
        {
            _handler.RemoveCallbacks(_positionRunnable);
        }

        _positionRunnable = null;
    }

    private void OnPositionTick()
    {
        UpdatePositionViews();
        if (_handler is not null && _positionRunnable is not null)
        {
            _handler.PostDelayed(_positionRunnable, 500);
        }
    }

    private void UpdatePlaybackControls()
    {
        if (_playButton is null)
        {
            return;
        }

        _playButton.Text = _videoView?.IsPlaying == true ? "一時停止" : "再生";
    }

    private void UpdatePositionViews()
    {
        if (_videoView is null || !_isPrepared)
        {
            if (_timeView is not null)
            {
                _timeView.Text = "00:00 / 00:00";
            }

            return;
        }

        if (_seekBar is not null)
        {
            _seekBar.Max = Math.Max(1, _videoView.Duration);
            if (!_isUserSeeking)
            {
                _seekBar.Progress = Math.Clamp(_videoView.CurrentPosition, 0, _seekBar.Max);
            }
        }

        if (_timeView is not null)
        {
            _timeView.Text = $"{FormatTime(_videoView.CurrentPosition)} / {FormatTime(_videoView.Duration)}";
        }
    }

    private static string FormatTime(int milliseconds)
    {
        var time = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
        return time.TotalHours >= 1
            ? $"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}"
            : $"{time.Minutes:00}:{time.Seconds:00}";
    }

    private static AndroidHandler CreateMainHandler()
    {
        var looper = Looper.MainLooper
            ?? Looper.MyLooper()
            ?? throw new InvalidOperationException("Android looper is not ready.");
        return new AndroidHandler(looper);
    }

    private sealed class PositionUpdateRunnable : Java.Lang.Object, Java.Lang.IRunnable
    {
        private readonly VideoPlayerActivity _activity;

        public PositionUpdateRunnable(VideoPlayerActivity activity)
        {
            _activity = activity;
        }

        public void Run()
        {
            _activity.OnPositionTick();
        }
    }
}
