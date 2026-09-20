using CoffeeEagle.Offline;
using CoffeeEagle.Reader.Models;
using CoffeeEagle.Reader.Services;
using CoffeeEagle.WearTransport;

namespace CoffeeEagle.Reader.Pages;

public sealed class OfflineAudioPage : ContentPage
{
    private readonly OfflineAudioService _offline;
    private readonly EagleLibrary? _library;
    private readonly IReadOnlyList<EagleAsset> _candidates;
    private readonly EagleImageSourceService _media;
    private readonly EagleLibraryStore _readerStore;
    private readonly VerticalStackLayout _list = new() { Spacing = 10 };
    private readonly Label _status = new() { TextColor = Colors.White, FontSize = 14 };
    private readonly Label _usage = new() { TextColor = Color.FromArgb("#98A4B5"), FontSize = 13 };
    private readonly Label _selectionSummary = new() { TextColor = Color.FromArgb("#21C7A8"), FontSize = 13 };
    private readonly Dictionary<string, long> _sizes = [];
    private readonly HashSet<string> _selected = [];
    private readonly ProgressBar _progress = new() { ProgressColor = Color.FromArgb("#21C7A8") };
    private readonly List<Button> _actions = [];
    private Button _cancel = null!;
    private CancellationTokenSource? _operation;
    private bool _savedMode;
    private Task _targetWrite = Task.CompletedTask;

    public OfflineAudioPage(OfflineAudioService offline, EagleLibrary? library, IReadOnlyList<EagleAsset> candidates,
        EagleImageSourceService media, EagleLibraryStore readerStore)
    {
        _offline = offline; _library = library; _candidates = candidates.Where(OfflineAudioService.IsSupported).ToArray();
        _media = media; _readerStore = readerStore;
        Title = "音声の持ち出し";
        BackgroundColor = Color.FromArgb("#0B0E12");
        NavigationPage.SetHasNavigationBar(this, true);
        var source = ActionButton("今の絞り込み", async () => { _savedMode = false; await RefreshAsync(); });
        var saved = ActionButton("スマホに保存済み", async () => { _savedMode = true; await RefreshAsync(); });
        var select = ActionButton("表示分のON / OFF", async () =>
        {
            await _targetWrite;
            var keys = _savedMode ? (await _offline.Store.SnapshotAsync()).Tracks.Select(x => x.Key).ToArray() :
                _candidates.Select(x => OfflineAudioService.RequestFor(_library!, x).Key).ToArray();
            var all = keys.Length > 0 && keys.All(_selected.Contains);
            await _offline.Targets.SetAsync(keys, !all);
            await RefreshAsync();
        });
        var save = ActionButton("スマホだけに保存", () => RunAsync(SaveSelectedAsync, "スマホへの保存が完了しました。「スマホに保存済み」からオフラインで聴けます。"));
        var send = ActionButton("選んだ曲をWatchに保存", () => RunAsync(SendSelectedAsync, "Watchへの保存を確認しました。Watchで「受信を終了」すると音声一覧が表示されます。"));
        send.BackgroundColor = Color.FromArgb("#21C7A8"); send.TextColor = Color.FromArgb("#0B0E12");
        var remove = ActionButton("スマホの保存分を削除", RemoveSelectedAsync);
        remove.BackgroundColor = Color.FromArgb("#443039");
        var limit = ActionButton("スマホの保存容量を設定", ChangeLimitAsync);
        var phoneStorage = new VerticalStackLayout { IsVisible = false, Spacing = 8, Children =
        {
            new Label { Text = "下の操作も、Watch用にチェックした曲が対象です。", TextColor = Colors.LightGray, FontSize = 13 },
            save,
            new Label { Text = "曲のデータをスマホにダウンロードします。スマホだけでオフライン再生したいときに使います。", TextColor = Colors.LightGray, FontSize = 13 },
            remove,
            new Label { Text = "曲のデータをスマホから削除し、空き容量を増やします。Watch・Driveの曲とチェックは残ります。", TextColor = Colors.LightGray, FontSize = 13 },
            limit
        } };
        Button? manage = null;
        manage = ActionButton("スマホの保存を管理 ▾", () =>
        {
            phoneStorage.IsVisible = !phoneStorage.IsVisible;
            manage!.Text = phoneStorage.IsVisible ? "スマホの保存を管理 ▴" : "スマホの保存を管理 ▾";
            return Task.CompletedTask;
        });
        _cancel = new Button { Text = "中止", IsVisible = false, BackgroundColor = Color.FromArgb("#443039"), TextColor = Colors.White };
        _cancel.Clicked += (_, _) => _operation?.Cancel();
        _progress.IsVisible = false;
        _status.Text = "Watchで「音声を受信」を開き、両端末を近くに置いてください。";
        var guide = new Border
        {
            BackgroundColor = Color.FromArgb("#14251F"), Stroke = Color.FromArgb("#315449"),
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 10 },
            Padding = 12,
            Content = new VerticalStackLayout { Spacing = 8, Children =
            {
                new Label { Text = "普段は「Watchに保存」だけでOK", TextColor = Color.FromArgb("#21C7A8"), FontSize = 14, FontAttributes = FontAttributes.Bold },
                new Label { Text = "選んだ曲をWatchに保存\nスマホにない曲をダウンロードしてから、Watchへ送ります。", TextColor = Colors.White, FontSize = 13 },
                new Label { Text = "スマホだけに保存\nスマホでオフライン再生したいときに使います。", TextColor = Colors.White, FontSize = 13 },
                new Label { Text = "スマホの保存分を削除\nスマホの空き容量を増やします。Watch・Driveの曲とチェックは残ります。", TextColor = Colors.White, FontSize = 13 },
                new Label { Text = "スマホだけの操作は、下の「スマホの保存を管理」から行えます。", TextColor = Colors.LightGray, FontSize = 12 }
            } }
        };
        var header = new VerticalStackLayout { Padding = 14, Spacing = 8, Children =
        {
            new Label { Text = "Watchに入れたい曲にチェックします。選択は記憶され、チェックを外しても保存済みの曲は消えません。動画は対象外です。", TextColor = Colors.LightGray, FontSize = 13 },
            new HorizontalStackLayout { Spacing = 6, Children = { source, saved } }, _usage,
            select, _selectionSummary, send,
            guide,
            _status, _progress, _cancel, manage, phoneStorage
        } };
        _list.Padding = new Thickness(14, 0, 14, 24);
        Content = new ScrollView { Content = new VerticalStackLayout { Children = { header, _list } } };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        MainActivity.ForegroundLost += CancelOnBackground;
        try { await RefreshAsync(); } catch (Exception ex) { _status.Text = ex.Message; }
    }
    private void CancelOnBackground() => _operation?.Cancel();
    protected override void OnDisappearing()
    {
        MainActivity.ForegroundLost -= CancelOnBackground;
        _operation?.Cancel(); base.OnDisappearing();
    }

    private Button ActionButton(string text, Func<Task> action)
    {
        var button = new Button { Text = text, FontSize = 13, BackgroundColor = Color.FromArgb("#20352F"), TextColor = Colors.White };
        button.Clicked += async (_, _) => { try { await action(); } catch (Exception ex) { _status.Text = ex.Message; } };
        _actions.Add(button);
        return button;
    }

    private async Task RefreshAsync()
    {
        await _targetWrite;
        var targets = await _offline.Targets.ReadAsync();
        _selected.Clear(); _selected.UnionWith(targets);
        var snapshot = await _offline.Store.SnapshotAsync();
        _usage.Text = $"スマホの保存容量 {Size(snapshot.UsedBytes)} / 上限 {Size(snapshot.LimitBytes)}";
        _list.Children.Clear();
        _sizes.Clear();
        if (_savedMode)
        {
            foreach (var track in snapshot.Tracks.OrderBy(x => x.Title))
            {
                _sizes[track.Key] = track.Length;
                AddRow(track.Key, track.Title, $"保存済み · {Size(track.Length)}", track);
            }
        }
        else if (_library is not null)
        {
            foreach (var asset in _library.Assets.Where(OfflineAudioService.IsSupported))
                _sizes[OfflineAudioService.RequestFor(_library, asset).Key] = asset.SizeBytes;
            foreach (var asset in _candidates)
            {
                var request = OfflineAudioService.RequestFor(_library, asset);
                var saved = snapshot.Tracks.FirstOrDefault(x => x.Key == request.Key);
                var state = saved is null ? "未保存" : saved.Revision == request.Revision ? "保存済み" : "更新あり";
                _sizes[request.Key] = asset.SizeBytes;
                AddRow(request.Key, asset.Name, $"{state} · {Size(asset.SizeBytes)}", saved, asset);
            }
        }
        if (_list.Children.Count == 0) _list.Children.Add(new Label { Text = _savedMode ? "保存済みの音声はありません。" : "本棚で音声のあるフォルダ・タグを選んでから開いてください。MP3 / M4Aなどに対応します。", TextColor = Colors.White });
        UpdateSelection();
    }

    private void AddRow(string key, string title, string detail, OfflineTrack? track, EagleAsset? source = null)
    {
        var check = new CheckBox { IsChecked = _selected.Contains(key), Color = Color.FromArgb("#21C7A8") };
        var reverting = false;
        check.CheckedChanged += (_, e) =>
        {
            if (reverting) return;
            var previous = _targetWrite;
            _targetWrite = PersistAsync();
            async Task PersistAsync()
            {
                check.IsEnabled = false;
                try
                {
                    await previous;
                    await _offline.Targets.SetAsync([key], e.Value);
                    if (e.Value) _selected.Add(key); else _selected.Remove(key);
                    UpdateSelection();
                }
                catch (Exception ex) { reverting = true; check.IsChecked = _selected.Contains(key); reverting = false; _status.Text = "同期対象を保存できません: " + ex.Message; }
                finally { check.IsEnabled = true; }
            }
        };
        var text = new VerticalStackLayout { VerticalOptions = LayoutOptions.Center, Children =
        { new Label { Text = title, TextColor = Colors.White, MaxLines = 2 }, new Label { Text = detail, TextColor = Colors.Gray, FontSize = 12 } } };
        var row = new Grid { ColumnDefinitions = { new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Star) }, ColumnSpacing = 8 };
        var tile = new Grid { WidthRequest = 60, HeightRequest = 60, BackgroundColor = Color.FromArgb("#20352F") };
        tile.Add(new Label { Text = "♫", FontSize = 30, TextColor = Color.FromArgb("#21C7A8"), HorizontalTextAlignment = TextAlignment.Center, VerticalTextAlignment = TextAlignment.Center });
        var art = track is null ? null : _offline.Store.Artwork.PathFor(track.Key, track.Revision);
        if (art is not null && File.Exists(art)) tile.Add(new Image { Source = ImageSource.FromFile(art), Aspect = Aspect.AspectFill });
        else if (source is not null && !string.IsNullOrEmpty(source.ThumbnailUri)) tile.Add(new Image { Source = _media.CreateThumbnailSource(source), Aspect = Aspect.AspectFill });
        row.Add(check); row.Add(tile, 1); row.Add(text, 2);
        if (track is not null)
        {
            var play = new TapGestureRecognizer();
            play.Tapped += async (_, _) =>
            {
                if (_operation is not null) return;
                var asset = new EagleAsset { Id = track.Key, Name = track.Title, FileName = track.Title + track.Extension,
                    FileUri = new Uri(_offline.Store.PathFor(track)).AbsoluteUri, MediaKind = EagleAssetMediaKind.Audio, SizeBytes = track.Length };
                await Navigation.PushAsync(new AudioPlayerPage([asset], 0, _media, _readerStore));
            };
            tile.GestureRecognizers.Add(play);
            text.Children.Add(new Label { Text = "サムネイルをタップして再生", TextColor = Color.FromArgb("#21C7A8"), FontSize = 11 });
        }
        _list.Children.Add(row);
    }

    private void UpdateSelection()
    {
        var keys = ScopedTargets();
        var bytes = keys.Sum(key => Math.Max(0, _sizes.GetValueOrDefault(key)));
        _selectionSummary.Text = (_savedMode ? "Watchに入れる曲（スマホ保存済み）" : "Watchに入れる曲（ライブラリ全体）") + $" {keys.Count}曲 · {Size(bytes)}" +
            (keys.Any(key => _sizes.GetValueOrDefault(key) <= 0) ? "（サイズ未確定を含む）" : "");
    }
    private HashSet<string> ScopedTargets() => _selected.Where(_sizes.ContainsKey).ToHashSet();

    private async Task RunAsync(Func<CancellationToken, Task> operation, string completed)
    {
        if (_operation is not null) return;
        await _targetWrite;
        if (ScopedTargets().Count == 0) { _status.Text = "Watchに入れたい曲にチェックしてください。"; return; }
        using var cancel = new CancellationTokenSource();
        cancel.CancelAfter(TimeSpan.FromMinutes(30));
        _operation = cancel;
        _progress.Progress = 0; _progress.IsVisible = true; _cancel.IsVisible = true;
        foreach (var button in _actions) button.IsEnabled = false;
        _list.IsEnabled = false;
        DeviceDisplay.Current.KeepScreenOn = true;
        try { await operation(cancel.Token); _status.Text = completed; }
        catch (OperationCanceledException) { _status.Text = "中止しました。完了済みの音声は残っています。再実行できます。"; }
        catch (Exception ex) { _status.Text = "処理を停止しました: " + ex.Message; }
        finally
        {
            DeviceDisplay.Current.KeepScreenOn = false;
            _operation = null;
            _progress.IsVisible = false; _cancel.IsVisible = false;
            foreach (var button in _actions) button.IsEnabled = true;
            _list.IsEnabled = true;
            await RefreshAsync();
        }
    }

    private IProgress<AudioProgress> Progress(string title) => new Progress<AudioProgress>(p =>
    {
        _progress.Progress = p.Total > 0 ? Math.Clamp((double)p.Bytes / p.Total, 0, 1) : 0;
        _status.Text = $"{title}\n{Size(p.Bytes)} / {(p.Total > 0 ? Size(p.Total) : "サイズ確認中")}";
    });

    private async Task SaveSelectedAsync(CancellationToken ct)
    {
        if (_savedMode || _library is null) return;
        foreach (var asset in _library.Assets.Where(OfflineAudioService.IsSupported).Where(x => _selected.Contains(OfflineAudioService.RequestFor(_library, x).Key)))
            await _offline.SaveAsync(_library, asset, Progress("端末保存: " + asset.Name), ct);
    }

    private async Task SendSelectedAsync(CancellationToken ct)
    {
        var context = Android.App.Application.Context;
        var peers = await WearAudioTransfer.FindReceiversAsync(context, ct);
        if (peers.Count == 0) throw new IOException("近くのWatchでCoffeeEagle Audioの「音声を受信」を開いてください。両方のアプリの署名が一致している必要があります。");
        WatchPeer? peer = peers.Count == 1 ? peers[0] : null;
        if (peer is null)
        {
            var names = peers.Select((x, i) => $"{i + 1}. {x.Name}").ToArray();
            var choice = await DisplayActionSheetAsync("送信先Watch", "キャンセル", null, names);
            var index = Array.IndexOf(names, choice); if (index < 0) throw new OperationCanceledException();
            peer = peers[index];
        }
        await SaveSelectedAsync(ct);
        var snapshot = await _offline.Store.SnapshotAsync(ct);
        var targets = ScopedTargets();
        if (targets.Any(key => snapshot.Tracks.All(x => x.Key != key))) throw new IOException("選択した音声の一部が端末にありません。保存し直してください。");
        foreach (var track in snapshot.Tracks.Where(x => targets.Contains(x.Key)))
        {
            var progress = Progress("Watch転送: " + track.Title);
            await Task.Run(() => WearAudioTransfer.SendAsync(context, peer, _offline.Store, track, progress, ct), ct);
        }
    }

    private async Task RemoveSelectedAsync()
    {
        await _targetWrite;
        var targets = ScopedTargets();
        if (targets.Count == 0 || !await DisplayAlertAsync("スマホの保存分を削除", $"チェックした {targets.Count}曲のうち、スマホに保存してあるデータを削除します。Watch・Drive・EAGLEの音声とチェックは残ります。", "スマホから削除", "キャンセル")) return;
        foreach (var key in targets) await _offline.Store.RemoveAsync(key);
        await RefreshAsync();
    }

    private async Task ChangeLimitAsync()
    {
        var snapshot = await _offline.Store.SnapshotAsync();
        var value = await DisplayPromptAsync("スマホの保存容量", "上限をGB単位で指定してください。保存済み音声は自動削除しません。", "保存", "キャンセル", initialValue: ((double)snapshot.LimitBytes / OfflineAudioStore.GiB).ToString("0.##"), keyboard: Keyboard.Numeric);
        if (value is null) return;
        if (!double.TryParse(value, out var gb) || !double.IsFinite(gb) || gb < 0.0625 || gb > 1024) throw new InvalidOperationException("0.0625〜1024 GBを指定してください。");
        await _offline.Store.SetLimitAsync((long)(gb * OfflineAudioStore.GiB)); await RefreshAsync();
    }

    private static string Size(long bytes) => bytes < 0 ? "不明" : bytes >= OfflineAudioStore.GiB ? $"{bytes / (double)OfflineAudioStore.GiB:0.##} GB" : $"{bytes / (1024d * 1024):0.##} MB";
}
