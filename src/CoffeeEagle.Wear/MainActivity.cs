using Android.App;
using Android.Content;
using Android.OS;
using Android.Views;
using Android.Widget;
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
    private TextView _usage = null!;
    private Button _receiveButton = null!;
    private LinearLayout _playbackPanel = null!;
    private WearAudioReceiver? _receiver;
    private bool _visible;
    private bool _changingReceive;
    private CancellationTokenSource? _ticker;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        var root = new LinearLayout(this) { Orientation = Orientation.Vertical };
        root.SetPadding(Dp(28), Dp(38), Dp(28), Dp(42));
        root.SetBackgroundColor(Color.Black);
        root.AddView(Label("CoffeeEagle", 19));
        _usage = Label("保存済み音声", 12); root.AddView(_usage);
        _playbackPanel = new LinearLayout(this) { Orientation = Orientation.Vertical };
        _now = Label("音声を選んで再生", 15); _playbackPanel.AddView(_now);
        var controls = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        controls.SetGravity(GravityFlags.Center);
        controls.AddView(Button("前", () => Command(AudioPlaybackService.Previous)), new LinearLayout.LayoutParams(0, Dp(48), 1));
        controls.AddView(Button("再生 / 停止", () => Command(AudioPlaybackService.Toggle)), new LinearLayout.LayoutParams(0, Dp(48), 2));
        controls.AddView(Button("次", () => Command(AudioPlaybackService.Next)), new LinearLayout.LayoutParams(0, Dp(48), 1));
        _playbackPanel.AddView(controls);
        var seek = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        seek.AddView(Button("−15秒", () => Command(AudioPlaybackService.Back15)), new LinearLayout.LayoutParams(0, Dp(48), 1));
        seek.AddView(Button("＋15秒", () => Command(AudioPlaybackService.Forward15)), new LinearLayout.LayoutParams(0, Dp(48), 1));
        _playbackPanel.AddView(seek); root.AddView(_playbackPanel);
        var tools = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        _receiveButton = Button("音声を受信", async () => await ToggleReceiveAsync());
        tools.AddView(_receiveButton, new LinearLayout.LayoutParams(0, Dp(48), 2));
        tools.AddView(Button("設定", ShowSettings), new LinearLayout.LayoutParams(0, Dp(48), 1));
        root.AddView(tools);
        _status = Label("保存済み音声をタップして再生", 12); root.AddView(_status);
        _list = new LinearLayout(this) { Orientation = Orientation.Vertical }; root.AddView(_list);
        var scroll = new ScrollView(this) { FillViewport = true }; scroll.AddView(root); SetContentView(scroll);
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
        AudioPlaybackService.Changed -= OnPlaybackChanged;
        _ = StopReceiveAsync();
        base.OnStop();
    }

    private async Task TickAsync(CancellationToken token)
    {
        try { while (!token.IsCancellationRequested) { UpdatePlayback(); await Task.Delay(1000, token); } }
        catch (OperationCanceledException) { }
    }

    private void OnPlaybackChanged() => RunOnUiThread(UpdatePlayback);
    private void UpdatePlayback()
    {
        var player = AudioPlaybackService.Current;
        _playbackPanel.Visibility = player is null ? ViewStates.Gone : ViewStates.Visible;
        _now.Text = player is null ? "音声を選んで再生" : $"{player.Title}\n{player.Status}\n{Time(player.Position)} / {Time(player.Duration)}";
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
        foreach (var track in state.Tracks.OrderBy(x => x.Title))
        {
            var play = Button(track.Title, () =>
            {
                StartForegroundService(new Intent(this, typeof(AudioPlaybackService)).SetAction(AudioPlaybackService.Play).PutExtra("key", track.Key));
            });
            play.LongClick += (_, _) =>
            {
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
            _list.AddView(play);
        }
        _list.AddView(Label("長押しでWatch内の音声を削除", 11));
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
        view.SetTextColor(Color.White); view.SetPadding(0, Dp(5), 0, Dp(5)); return view;
    }
    private Button Button(string text, Action action)
    {
        var button = new Button(this) { Text = text, TextSize = 12 };
        button.SetAllCaps(false); button.SetMinHeight(Dp(48));
        button.Click += (_, _) => action(); return button;
    }
    private int Dp(int value) => (int)(value * Resources!.DisplayMetrics!.Density);
    private static string Size(long b) => $"{b / (double)OfflineAudioStore.GiB:0.##} GB";
    private static string Time(int ms) => TimeSpan.FromMilliseconds(Math.Max(0, ms)).ToString(@"hh\:mm\:ss");
}
