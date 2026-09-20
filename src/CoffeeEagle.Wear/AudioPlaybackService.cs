using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Media;
using Android.Media.Session;
using Android.OS;
using CoffeeEagle.Offline;
using OperationCanceledException = System.OperationCanceledException;

namespace CoffeeEagle.Wear;

[Service(Exported = false, ForegroundServiceType = ForegroundService.TypeMediaPlayback)]
public sealed class AudioPlaybackService : Service, AudioManager.IOnAudioFocusChangeListener
{
    public const string Play = "coffeeeagle.play", Toggle = "coffeeeagle.toggle", Stop = "coffeeeagle.stop",
        Next = "coffeeeagle.next", Previous = "coffeeeagle.previous", Back15 = "coffeeeagle.back15", Forward15 = "coffeeeagle.forward15";
    private const string NotificationChannelId = "offline-audio";
    public static AudioPlaybackService? Current { get; private set; }
    public static event Action? Changed;
    private MediaPlayer? _player;
    private MediaSession? _session;
    private AudioManager _audio = null!;
    private AudioFocusRequestClass? _focus;
    private NoisyReceiver? _noisy;
    private List<OfflineTrack> _queue = [];
    private OfflineTrack? _track;
    private bool _prepared;
    private bool _playWhenReady;
    private int _generation;
    private bool _destroyed;
    private CancellationTokenSource _lifetime = new();
    public string? Key => _track?.Key;
    public string Title => _track?.Title ?? "CoffeeEagle Audio";
    public string Status { get; private set; } = "準備中";
    public int Position { get { try { return _prepared ? _player?.CurrentPosition ?? 0 : 0; } catch { return 0; } } }
    public int Duration { get { try { return _prepared ? _player?.Duration ?? 0 : 0; } catch { return 0; } } }
    private bool Playing { get { try { return _prepared && (_player?.IsPlaying ?? false); } catch { return false; } } }

    public override void OnCreate()
    {
        base.OnCreate(); Current = this;
        _audio = (AudioManager)GetSystemService(AudioService)!;
        ((NotificationManager)GetSystemService(NotificationService)!).CreateNotificationChannel(new NotificationChannel(NotificationChannelId, "オフライン音声", NotificationImportance.Low));
        _session = new MediaSession(this, "CoffeeEagleAudio");
        _session.SetCallback(new SessionCallbacks(this));
        _session.Active = true;
        _noisy = new NoisyReceiver(this);
        if (OperatingSystem.IsAndroidVersionAtLeast(33))
            RegisterReceiver(_noisy, new IntentFilter(AudioManager.ActionAudioBecomingNoisy), ReceiverFlags.NotExported);
        else RegisterReceiver(_noisy, new IntentFilter(AudioManager.ActionAudioBecomingNoisy));
        _ = PersistLoopAsync(_lifetime.Token);
    }

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        // Required before async preparation and before requesting focus on modern Android.
        StartForeground(1, Notification());
        switch (intent?.Action)
        {
            case Play: _ = LoadAsync(intent.GetStringExtra("key")); break;
            case Toggle:
                if (Playing || (!_prepared && _playWhenReady)) Pause();
                else { _playWhenReady = true; Resume(); }
                break;
            case Stop: StopPlayback(); break;
            case Next: Move(1); break;
            case Previous: Move(-1); break;
            case Back15: Seek(Position - 15000); break;
            case Forward15: Seek(Position + 15000); break;
        }
        return StartCommandResult.NotSticky;
    }
    public override IBinder? OnBind(Intent? intent) => null;

    private async Task LoadAsync(string? key)
    {
        var generation = ++_generation;
        SavePosition(); ReleasePlayer();
        _playWhenReady = true;
        Status = "読み込み中"; Publish();
        try
        {
            var snapshot = await WearAudioStore.Current.SnapshotAsync(_lifetime.Token);
            if (_destroyed || generation != _generation) return;
            _queue = snapshot.Tracks.OrderBy(x => x.Title).ToList();
            _track = _queue.FirstOrDefault(x => x.Key == key) ?? throw new IOException("保存済み音声が見つかりません。");
            if (!await WearAudioStore.Current.VerifyAsync(_track, _lifetime.Token)) throw new IOException("音声が不完全です。スマホから再送してください。");
            if (_destroyed || generation != _generation) return;
            var player = new MediaPlayer(); _player = player;
            player.SetAudioAttributes(new AudioAttributes.Builder()!.SetUsage(AudioUsageKind.Media)!.SetContentType(AudioContentType.Music)!.Build());
            player.SetWakeMode(this, WakeLockFlags.Partial);
            player.Prepared += (_, _) =>
            {
                if (_player != player || _destroyed) return;
                _prepared = true;
                var position = GetSharedPreferences("playback", FileCreationMode.Private)!.GetInt(_track!.Sha256, 0);
                if (position > 0 && position < player.Duration - 1000) player.SeekTo(position);
                if (_playWhenReady) Resume(); else Publish();
            };
            player.Completion += (_, _) =>
            {
                if (_player != player) return;
                GetSharedPreferences("playback", FileCreationMode.Private)!.Edit()!.Remove(_track!.Sha256)!.Apply();
                var index = _queue.FindIndex(x => x.Key == Key);
                if (index >= 0 && index + 1 < _queue.Count) _ = LoadAsync(_queue[index + 1].Key);
                else { Pause(); Seek(0); }
            };
            player.Error += (_, e) =>
            {
                e.Handled = true;
                if (_player == player) { Status = "この音声は再生できません。MP3などで再送してください。"; ReleasePlayer(); Publish(); }
            };
            player.SetDataSource(WearAudioStore.Current.PathFor(_track));
            player.PrepareAsync();
            _session!.SetMetadata(new MediaMetadata.Builder()!.PutString(MediaMetadata.MetadataKeyTitle, _track.Title)!.Build());
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_destroyed && generation == _generation) { Status = ex.Message; ReleasePlayer(); Publish(); } }
    }

    private bool HasHeadphones() => (_audio.GetDevices(GetDevicesTargets.Outputs) ?? []).Any(x =>
        x.Type is AudioDeviceType.BluetoothA2dp or AudioDeviceType.WiredHeadphones or AudioDeviceType.WiredHeadset or AudioDeviceType.UsbHeadset ||
        (OperatingSystem.IsAndroidVersionAtLeast(31) && x.Type == AudioDeviceType.BleHeadset));

    private void Resume()
    {
        if (!_prepared || _player is null) return;
        if (!HasHeadphones()) { Status = "Bluetoothイヤホンを接続してください"; Publish(); return; }
        _focus ??= new AudioFocusRequestClass.Builder(AudioFocus.Gain)!.SetAudioAttributes(new AudioAttributes.Builder()!.SetUsage(AudioUsageKind.Media)!.Build()!)!
            .SetOnAudioFocusChangeListener(this)!.SetWillPauseWhenDucked(true)!.Build()!;
        if (_audio.RequestAudioFocus(_focus) != AudioFocusRequest.Granted) { Status = "他の音声の終了後に再生してください"; Publish(); return; }
        try { _player.Start(); Status = "再生中"; Publish(); }
        catch (Exception ex) { Status = ex.Message; Publish(); }
    }

    private void Pause()
    {
        _playWhenReady = false;
        if (Playing) _player!.Pause();
        SavePosition();
        if (_focus is not null) _audio.AbandonAudioFocusRequest(_focus);
        Status = "一時停止"; Publish();
    }

    public void OnAudioFocusChange(AudioFocus focusChange)
    {
        if (focusChange is AudioFocus.Loss or AudioFocus.LossTransient or AudioFocus.LossTransientCanDuck) Pause();
    }
    private void Seek(int position)
    {
        if (!_prepared) return;
        _player?.SeekTo(Math.Clamp(position, 0, Math.Max(0, Duration - 1))); SavePosition(); Publish();
    }
    private void Move(int delta)
    {
        var index = _queue.FindIndex(x => x.Key == Key);
        var next = index + delta;
        if (next >= 0 && next < _queue.Count) _ = LoadAsync(_queue[next].Key);
    }
    private void StopPlayback()
    {
        SavePosition(); ++_generation; ReleasePlayer();
        if (_focus is not null) _audio.AbandonAudioFocusRequest(_focus);
        _session!.Active = false;
        StopForeground(StopForegroundFlags.Remove); StopSelf();
    }
    private void ReleasePlayer()
    {
        _prepared = false;
        if (_player is null) return;
        try { _player.Release(); } catch { }
        _player.Dispose(); _player = null;
    }
    private void SavePosition()
    {
        if (_track is not null && _prepared) GetSharedPreferences("playback", FileCreationMode.Private)!.Edit()!.PutInt(_track.Sha256, Position)!.Apply();
    }
    private async Task PersistLoopAsync(CancellationToken ct)
    {
        try { while (!ct.IsCancellationRequested) { await Task.Delay(15000, ct); SavePosition(); } }
        catch (OperationCanceledException) { }
    }
    private void Publish()
    {
        if (_destroyed) return;
        _session?.SetPlaybackState(new PlaybackState.Builder()!.SetActions(PlaybackState.ActionPlay | PlaybackState.ActionPause | PlaybackState.ActionPlayPause |
            PlaybackState.ActionStop | PlaybackState.ActionSkipToNext | PlaybackState.ActionSkipToPrevious | PlaybackState.ActionSeekTo)!
            .SetState(Playing ? PlaybackStateCode.Playing : PlaybackStateCode.Paused, Position, Playing ? 1 : 0)!.Build());
        ((NotificationManager)GetSystemService(NotificationService)!).Notify(1, Notification());
        Changed?.Invoke();
    }
    private PendingIntent ActionIntent(string action, int id) => PendingIntent.GetService(this, id,
        new Intent(this, typeof(AudioPlaybackService)).SetAction(action), PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable)!;
    private Notification Notification()
    {
        var launch = PendingIntent.GetActivity(this, 0, new Intent(this, typeof(MainActivity)), PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
        return new Notification.Builder(this, NotificationChannelId)!
            .SetSmallIcon(Android.Resource.Drawable.IcMediaPlay)!.SetContentTitle(Title)!.SetContentText(Status)!
            .SetContentIntent(launch)!.SetOnlyAlertOnce(true)!.SetOngoing(Playing)!.SetVisibility(NotificationVisibility.Public)!
            .AddAction(new Notification.Action.Builder(Android.Graphics.Drawables.Icon.CreateWithResource(this, Android.Resource.Drawable.IcMediaPrevious), "前", ActionIntent(Previous, 1))!.Build())!
            .AddAction(new Notification.Action.Builder(Android.Graphics.Drawables.Icon.CreateWithResource(this, Playing ? Android.Resource.Drawable.IcMediaPause : Android.Resource.Drawable.IcMediaPlay), Playing ? "停止" : "再生", ActionIntent(Toggle, 2))!.Build())!
            .AddAction(new Notification.Action.Builder(Android.Graphics.Drawables.Icon.CreateWithResource(this, Android.Resource.Drawable.IcMediaNext), "次", ActionIntent(Next, 3))!.Build())!
            .AddAction(new Notification.Action.Builder(Android.Graphics.Drawables.Icon.CreateWithResource(this, Android.Resource.Drawable.IcMenuCloseClearCancel), "終了", ActionIntent(Stop, 4))!.Build())!
            .SetStyle(new Notification.MediaStyle()!.SetMediaSession(_session!.SessionToken)!.SetShowActionsInCompactView(0, 1, 2))!.Build();
    }

    public override void OnDestroy()
    {
        SavePosition(); _destroyed = true; ++_generation; _lifetime.Cancel(); ReleasePlayer();
        if (_focus is not null) _audio.AbandonAudioFocusRequest(_focus);
        if (_noisy is not null) UnregisterReceiver(_noisy);
        _session?.Release(); _session?.Dispose();
        Current = null; Changed?.Invoke(); base.OnDestroy();
    }
    private sealed class NoisyReceiver(AudioPlaybackService owner) : BroadcastReceiver
    { public override void OnReceive(Context? context, Intent? intent) { if (intent?.Action == AudioManager.ActionAudioBecomingNoisy) owner.Pause(); } }
    private sealed class SessionCallbacks(AudioPlaybackService owner) : MediaSession.Callback
    {
        public override void OnPlay() => owner.Resume();
        public override void OnPause() => owner.Pause();
        public override void OnStop() => owner.StopPlayback();
        public override void OnSkipToNext() => owner.Move(1);
        public override void OnSkipToPrevious() => owner.Move(-1);
        public override void OnSeekTo(long position) => owner.Seek((int)Math.Clamp(position, 0, int.MaxValue));
    }
}
