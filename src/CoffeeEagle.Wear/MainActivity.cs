using Android.App;
using Android.Content;
using Android.OS;
using Android.Views;
using Android.Widget;
using Android.Media;
using Orientation = Android.Widget.Orientation;
using Android.Graphics.Drawables;
using CoffeeEagle.Offline;
using CoffeeEagle.WearTransport;
using Color = Android.Graphics.Color;
using OperationCanceledException = System.OperationCanceledException;

namespace CoffeeEagle.Wear;

[Activity(Label = "CoffeeEagle Audio", MainLauncher = true, Exported = true)]
public sealed class MainActivity : Activity
{
    private LinearLayout _list = null!;
    private TextView _status = null!;
    private TextView _now = null!;
    private TextView _playerStatus = null!;
    private TextView _usage = null!;
    private Button _receiveButton = null!;
    private LinearLayout _playbackPanel = null!;
    private LinearLayout _galleryPanel = null!;
    private LinearLayout _root = null!;
    private TextView _heading = null!;
    private ScrollView _scroll = null!;
    private SeekBar _seek = null!;
    private TextView _time = null!;
    private Button _toggle = null!;
    private FrameLayout _art = null!;
    private bool _showPlayer, _dragging;
    private string? _artIdentity;
    private WearAudioReceiver? _receiver;
    private bool _visible;
    private bool _changingReceive;
    private CancellationTokenSource? _ticker;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        var root = new LinearLayout(this) { Orientation = Orientation.Vertical };
        _root = root;
        root.SetPadding(Dp(28), Dp(38), Dp(28), Dp(42));
        root.SetBackgroundColor(Color.ParseColor("#0B0E12"));
        _heading = Label("CoffeeEagle", 19); root.AddView(_heading);
        _galleryPanel = new LinearLayout(this) { Orientation = Orientation.Vertical };
        root.AddView(_galleryPanel);
        _usage = Label("保存済み音声", 12); _galleryPanel.AddView(_usage);
        _list = new LinearLayout(this) { Orientation = Orientation.Vertical }; _galleryPanel.AddView(_list);
        _galleryPanel.AddView(Button("再生画面へ", () => ShowPlayer(true)));
        _playbackPanel = new LinearLayout(this) { Orientation = Orientation.Vertical };
        var navigation = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        navigation.AddView(Button("一覧", () => ShowPlayer(false)), new LinearLayout.LayoutParams(0, Dp(32), 1));
        navigation.AddView(Button("音量", ShowVolume), new LinearLayout.LayoutParams(0, Dp(32), 1));
        _playbackPanel.AddView(navigation);
        _art = new FrameLayout(this);
        _playbackPanel.AddView(_art, new LinearLayout.LayoutParams(Dp(32), Dp(32)) { Gravity = GravityFlags.Center });
        _now = Label("音声を選んで再生", 12); _now.SetMaxLines(1); _now.Ellipsize = Android.Text.TextUtils.TruncateAt.End; _playbackPanel.AddView(_now);
        _playerStatus = Label("", 10); _playerStatus.SetMaxLines(1); _playerStatus.Ellipsize = Android.Text.TextUtils.TruncateAt.End; _playbackPanel.AddView(_playerStatus);
        _seek = new SeekBar(this) { ContentDescription = "再生位置" };
        _seek.StartTrackingTouch += (_, _) => _dragging = true;
        _seek.ProgressChanged += (_, e) => { if (e.FromUser) _time.Text = $"{Time(e.Progress)} / {Time(AudioPlaybackService.Current?.Duration ?? 0)}"; };
        _seek.StopTrackingTouch += (_, _) =>
        {
            if (AudioPlaybackService.Current is not null)
                StartForegroundService(new Intent(this, typeof(AudioPlaybackService)).SetAction(AudioPlaybackService.SeekTo).PutExtra("position", _seek.Progress));
            _dragging = false;
        };
        _playbackPanel.AddView(_seek, new LinearLayout.LayoutParams(-1, Dp(24)));
        _time = Label("0:00 / 0:00", 11); _playbackPanel.AddView(_time);
        var controls = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        controls.SetGravity(GravityFlags.Center);
        controls.AddView(Button("前", () => Command(AudioPlaybackService.Previous)), new LinearLayout.LayoutParams(0, Dp(40), 1));
        _toggle = Button("再生", () => Command(AudioPlaybackService.Toggle));
        controls.AddView(_toggle, new LinearLayout.LayoutParams(0, Dp(40), 2));
        controls.AddView(Button("次", () => Command(AudioPlaybackService.Next)), new LinearLayout.LayoutParams(0, Dp(40), 1));
        _playbackPanel.AddView(controls);
        var seek = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        seek.AddView(Button("−15秒", () => Command(AudioPlaybackService.Back15)), new LinearLayout.LayoutParams(0, Dp(48), 1));
        seek.AddView(Button("＋15秒", () => Command(AudioPlaybackService.Forward15)), new LinearLayout.LayoutParams(0, Dp(48), 1));
        _playbackPanel.AddView(seek); root.AddView(_playbackPanel);
        var tools = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        _receiveButton = Button("音声を受信", async () => await ToggleReceiveAsync());
        tools.AddView(_receiveButton, new LinearLayout.LayoutParams(0, Dp(48), 2));
        tools.AddView(Button("設定", ShowSettings), new LinearLayout.LayoutParams(0, Dp(48), 1));
        _galleryPanel.AddView(tools);
        _status = Label("サムネイルをタップして再生", 12); _galleryPanel.AddView(_status);
        _scroll = new ScrollView(this) { FillViewport = true }; _scroll.AddView(root); SetContentView(_scroll);
    }

    protected override void OnStart()
    {
        base.OnStart(); _visible = true;
        AudioPlaybackService.Changed += OnPlaybackChanged;
        _ = RefreshSafeAsync();
        _ticker = new CancellationTokenSource();
        _ = TickAsync(_ticker.Token);
    }

    protected override void OnStop()
    {
        _visible = false;
        _ticker?.Cancel();
        _ticker?.Dispose(); _ticker = null;
        AudioPlaybackService.Changed -= OnPlaybackChanged;
        _ = StopReceiveAsync();
        base.OnStop();
    }

    private async Task TickAsync(CancellationToken token)
    {
        try { while (!token.IsCancellationRequested) { if (_showPlayer) UpdatePlayback(); await Task.Delay(1000, token); } }
        catch (OperationCanceledException) { }
    }

    private void OnPlaybackChanged() => RunOnUiThread(UpdatePlayback);
    private void ShowPlayer(bool show)
    {
        if (show && _receiver is not null) _ = StopReceiveAsync();
        _showPlayer = show; _heading.Visibility = show ? ViewStates.Gone : ViewStates.Visible;
        _root.SetPadding(Dp(24), Dp(show ? 18 : 38), Dp(24), Dp(38));
        UpdatePlayback(); _scroll.ScrollTo(0, 0);
    }
    private void UpdatePlayback()
    {
        var player = AudioPlaybackService.Current;
        _playbackPanel.Visibility = _showPlayer ? ViewStates.Visible : ViewStates.Gone;
        _galleryPanel.Visibility = _showPlayer ? ViewStates.Gone : ViewStates.Visible;
        _now.Text = player?.Title ?? "一覧から音声を選んでください";
        _playerStatus.Text = player?.Status == "Bluetoothイヤホンを接続してください" ? "イヤホンを接続してください" : player?.Status ?? "";
        _toggle.Text = player?.IsPlaying == true ? "一時停止" : "再生";
        _seek.Enabled = player?.Duration > 0;
        if (!_dragging)
        {
            _seek.Max = Math.Max(1, player?.Duration ?? 0); _seek.Progress = player?.Position ?? 0;
            _time.Text = $"{Time(player?.Position ?? 0)} / {Time(player?.Duration ?? 0)}";
        }
        var identity = player?.Track is { } track ? track.Key + track.Revision : "empty";
        if (_artIdentity != identity)
        {
            _artIdentity = identity; _art.RemoveAllViews(); _art.AddView(Artwork(player?.Track), new FrameLayout.LayoutParams(-1, -1));
        }
    }

    private void Command(string action)
    {
        if (AudioPlaybackService.Current is null) { _status.Text = "下の保存一覧から音声を選んでください。"; return; }
        StartForegroundService(new Intent(this, typeof(AudioPlaybackService)).SetAction(action));
    }

    private async Task ToggleReceiveAsync()
    {
        if (_changingReceive) return;
        _changingReceive = true;
        try
        {
            if (_receiver is not null) { await StopReceiveAsync(); await RefreshAsync(); return; }
            var receiver = new WearAudioReceiver(this, WearAudioStore.Current, text => RunOnUiThread(() => { if (_visible) _status.Text = text; }));
            _receiver = receiver;
            Window?.AddFlags(WindowManagerFlags.KeepScreenOn);
            _status.Text = "接続準備中…";
            await receiver.StartAsync();
            if (!_visible || _receiver != receiver) { await receiver.DisposeAsync(); return; }
            _receiveButton.Text = "受信を終了";
            _status.Text = "受信待機中。スマホで音声を選び「Watchへ送る」を押してください。この画面を開いたままにします。";
            _scroll.Post(() => _scroll.FullScroll(FocusSearchDirection.Down));
        }
        catch (Exception ex) { await StopReceiveAsync(); _status.Text = "接続できません: " + ex.Message; }
        finally { _changingReceive = false; }
    }

    private async Task StopReceiveAsync()
    {
        var receiver = _receiver; _receiver = null;
        Window?.ClearFlags(WindowManagerFlags.KeepScreenOn);
        _receiveButton.Text = "音声を受信";
        if (receiver is not null) await receiver.DisposeAsync();
        if (_visible) _status.Text = "受信を終了しました。保存一覧を更新して再生できます。";
    }

    private async Task RefreshSafeAsync()
    {
        try { await RefreshAsync(); } catch (Exception ex) { _status.Text = ex.Message; }
    }

    private async Task RefreshAsync()
    {
        var state = await WearAudioStore.Current.SnapshotAsync();
        _usage.Text = $"{state.Tracks.Count}曲 · {Size(state.UsedBytes)} / {Size(state.LimitBytes)}";
        _list.RemoveAllViews();
        if (state.Tracks.Count == 0) _list.AddView(Label("まだ音声がありません。スマホから送ってください。", 14));
        LinearLayout? row = null; var index = 0;
        foreach (var track in state.Tracks.OrderBy(x => x.Title))
        {
            if (index++ % 2 == 0) { row = new LinearLayout(this) { Orientation = Orientation.Horizontal }; _list.AddView(row); }
            var play = new LinearLayout(this) { Orientation = Orientation.Vertical, Background = Rounded("#19232D"), Clickable = true, Focusable = true, ContentDescription = track.Title + "を再生" };
            play.SetPadding(Dp(4), Dp(4), Dp(4), Dp(4));
            play.AddView(Artwork(track), new LinearLayout.LayoutParams(-1, Dp(70)));
            var name = Label(track.Title, 11); name.SetMaxLines(2); name.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
            play.AddView(name, new LinearLayout.LayoutParams(-1, Dp(38)));
            play.Click += (_, _) =>
            {
                StartForegroundService(new Intent(this, typeof(AudioPlaybackService)).SetAction(AudioPlaybackService.Play).PutExtra("key", track.Key));
                ShowPlayer(true);
            };
            play.LongClick += (_, e) =>
            {
                e.Handled = true;
                new AlertDialog.Builder(this)!.SetTitle("Watchから削除")!.SetMessage(track.Title + "\nスマホ・Driveの音声は残ります。")!
                    .SetNegativeButton("キャンセル", (_, _) => { })!
                    .SetPositiveButton("削除", async (_, _) =>
                    {
                        try
                        {
                            if (AudioPlaybackService.Current?.Key == track.Key) Command(AudioPlaybackService.Stop);
                            await WearAudioStore.Current.RemoveAsync(track.Key); await RefreshAsync();
                        }
                        catch (Exception ex) { _status.Text = ex.Message; }
                    })!.Show();
            };
            row!.AddView(play, new LinearLayout.LayoutParams(0, -2, 1) { LeftMargin = Dp(3), RightMargin = Dp(3), BottomMargin = Dp(6) });
        }
        if (index % 2 == 1) row!.AddView(new View(this), new LinearLayout.LayoutParams(0, 1, 1));
        _artIdentity = null; UpdatePlayback();
    }

    private GradientDrawable Rounded(string color)
    {
        var background = new GradientDrawable(); background.SetColor(Color.ParseColor(color)); background.SetCornerRadius(Dp(10)); return background;
    }
    private View Artwork(OfflineTrack? track)
    {
        var tile = new FrameLayout(this) { Background = Rounded("#203B38") };
        var note = Label("♫", 34); note.SetTextColor(Color.ParseColor("#21C7A8")); tile.AddView(note, new FrameLayout.LayoutParams(-1, -1));
        if (track is not null)
        {
            var path = WearAudioStore.Current.Artwork.PathFor(track.Key, track.Revision);
            if (File.Exists(path) && new FileInfo(path).Length <= OfflineArtworkStore.MaxImageBytes)
            {
                using var bounds = new Android.Graphics.BitmapFactory.Options { InJustDecodeBounds = true };
                Android.Graphics.BitmapFactory.DecodeFile(path, bounds)?.Dispose();
                if (bounds.OutWidth is > 0 and <= 320 && bounds.OutHeight is > 0 and <= 320)
                {
                    using var bitmap = Android.Graphics.BitmapFactory.DecodeFile(path);
                    if (bitmap is not null)
                    {
                        var picture = new ImageView(this); picture.SetScaleType(ImageView.ScaleType.CenterCrop);
                        picture.SetImageBitmap(bitmap); tile.AddView(picture, new FrameLayout.LayoutParams(-1, -1));
                    }
                }
            }
        }
        return tile;
    }
    private void ShowVolume()
    {
        try
        {
            var audio = (AudioManager)GetSystemService(AudioService)!;
            var panel = new LinearLayout(this) { Orientation = Orientation.Vertical };
            panel.SetPadding(Dp(16), Dp(4), Dp(16), Dp(4));
            var max = audio.GetStreamMaxVolume(Android.Media.Stream.Music);
            var slider = new SeekBar(this) { Max = max, Progress = audio.GetStreamVolume(Android.Media.Stream.Music), ContentDescription = "メディア音量" };
            var value = Label($"{slider.Progress} / {max}", 13);
            slider.ProgressChanged += (_, e) =>
            {
                if (!e.FromUser) return;
                audio.SetStreamVolume(Android.Media.Stream.Music, e.Progress, (VolumeNotificationFlags)0);
                value.Text = $"{audio.GetStreamVolume(Android.Media.Stream.Music)} / {max}";
            };
            panel.AddView(value); panel.AddView(slider);
            new AlertDialog.Builder(this)!.SetTitle("イヤホンの音量")!.SetView(panel)!.SetPositiveButton("閉じる", (_, _) => { })!.Show();
        }
        catch (Exception ex) { _now.Text = ex.Message; }
    }

    private void ChangeLimit()
    {
        var values = new[] { "0.5 GB", "1 GB", "2 GB", "4 GB", "8 GB" };
        var limits = new[] { 0.5, 1, 2, 4, 8 };
        new AlertDialog.Builder(this)!.SetTitle("Watchの保存上限")!.SetItems(values, async (_, e) =>
        {
            try { await WearAudioStore.Current.SetLimitAsync((long)(limits[e.Which] * OfflineAudioStore.GiB)); await RefreshAsync(); }
            catch (Exception ex) { _status.Text = ex.Message; }
        })!.Show();
    }

    private void ShowSettings()
    {
        new AlertDialog.Builder(this)!.SetTitle("設定")!.SetItems(new[] { "イヤホンを接続", "保存一覧を更新", "保存容量の上限", "再生を終了" }, async (_, e) =>
        {
            try
            {
                switch (e.Which)
                {
                    case 0: StartActivity(new Intent(Android.Provider.Settings.ActionBluetoothSettings)); break;
                    case 1: await RefreshSafeAsync(); break;
                    case 2: ChangeLimit(); break;
                    case 3: Command(AudioPlaybackService.Stop); break;
                }
            }
            catch (Exception ex) { _status.Text = ex.Message; }
        })!.Show();
    }

    private TextView Label(string text, float size)
    {
        var view = new TextView(this) { Text = text, TextSize = size, Gravity = GravityFlags.Center };
        view.SetTextColor(Color.White); view.SetPadding(0, Dp(2), 0, Dp(2)); return view;
    }
    private Button Button(string text, Action action)
    {
        var button = new Button(this) { Text = text, TextSize = 12 };
        button.SetAllCaps(false); button.SetMinHeight(Dp(48));
        button.SetMinimumWidth(0); button.SetPadding(Dp(2), 0, Dp(2), 0); button.SetTextColor(Color.ParseColor("#21C7A8"));
        var background = new Android.Graphics.Drawables.RippleDrawable(Android.Content.Res.ColorStateList.ValueOf(Color.ParseColor("#41675D")), Rounded("#20352F"), null);
        button.Background = new InsetDrawable(background, Dp(3), Dp(3), Dp(3), Dp(3));
        button.Click += (_, _) => action(); return button;
    }
    private int Dp(int value) => (int)(value * Resources!.DisplayMetrics!.Density);
    private static string Size(long b) => b < OfflineAudioStore.GiB ? $"{b / (1024d * 1024):0.#} MB" : $"{b / (double)OfflineAudioStore.GiB:0.##} GB";
    private static string Time(int ms) { var t = TimeSpan.FromMilliseconds(Math.Max(0, ms)); return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss"); }
}
