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
    private CancellationTokenSource? _operation;
    private bool _savedMode;

    public OfflineAudioPage(OfflineAudioService offline, EagleLibrary? library, IReadOnlyList<EagleAsset> candidates,
        EagleImageSourceService media, EagleLibraryStore readerStore)
    {
        _offline = offline; _library = library; _candidates = candidates.Where(OfflineAudioService.IsSupported).ToArray();
        _media = media; _readerStore = readerStore;
        Title = "音声の持ち出し";
        BackgroundColor = Color.FromArgb("#0B0E12");
        NavigationPage.SetHasNavigationBar(this, true);
        var source = ActionButton("今の絞り込み", async () => { _savedMode = false; _selected.Clear(); await RefreshAsync(); });
        var saved = ActionButton("端末に保存済み", async () => { _savedMode = true; _selected.Clear(); await RefreshAsync(); });
        var select = ActionButton("全選択 / 解除", async () =>
        {
            var keys = _savedMode ? (await _offline.Store.SnapshotAsync()).Tracks.Select(x => x.Key).ToArray() :
                _candidates.Select(x => OfflineAudioService.RequestFor(_library!, x).Key).ToArray();
            var all = keys.Length > 0 && keys.All(_selected.Contains);
            _selected.Clear(); if (!all) foreach (var key in keys) _selected.Add(key);
            await RefreshAsync();
        });
        var save = ActionButton("選択を端末に保存", () => RunAsync(SaveSelectedAsync, "端末への保存が完了しました。「端末に保存済み」からオフラインで聴けます。"));
        var send = ActionButton("選択をWatchへ送る", () => RunAsync(SendSelectedAsync, "Watchへの保存を確認しました。Watchで「受信を終了」すると音声一覧が表示されます。"));
        var remove = ActionButton("選択を端末から削除", RemoveSelectedAsync);
        var limit = ActionButton("端末の容量上限", ChangeLimitAsync);
        var cancel = new Button { Text = "中止", BackgroundColor = Color.FromArgb("#443039"), TextColor = Colors.White };
        cancel.Clicked += (_, _) => _operation?.Cancel();
        _status.Text = "Watchで「音声を受信」を開き、両端末を近くに置いてください。送信前に端末へ保存します。";
        var header = new VerticalStackLayout { Padding = 14, Spacing = 8, Children =
        {
            new HorizontalStackLayout { Spacing = 6, Children = { source, saved } }, _usage,
            new HorizontalStackLayout { Spacing = 6, Children = { select, limit } },
            _selectionSummary, save, send, remove, _status, _progress, cancel
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
        var snapshot = await _offline.Store.SnapshotAsync();
        _usage.Text = $"端末保存 {Size(snapshot.UsedBytes)} / 上限 {Size(snapshot.LimitBytes)}";
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
            foreach (var asset in _candidates)
            {
                var request = OfflineAudioService.RequestFor(_library, asset);
                var saved = snapshot.Tracks.FirstOrDefault(x => x.Key == request.Key);
                var state = saved is null ? "未保存" : saved.Revision == request.Revision ? "保存済み" : "更新あり";
                _sizes[request.Key] = asset.SizeBytes;
                AddRow(request.Key, asset.Name, $"{state} · {Size(asset.SizeBytes)}", saved);
            }
        }
        if (_list.Children.Count == 0) _list.Children.Add(new Label { Text = _savedMode ? "保存済みの音声はありません。" : "本棚で音声のあるフォルダ・タグを選んでから開いてください。MP3 / M4Aなどに対応します。", TextColor = Colors.White });
        _selected.IntersectWith(_sizes.Keys);
        UpdateSelection();
    }

    private void AddRow(string key, string title, string detail, OfflineTrack? track)
    {
        var check = new CheckBox { IsChecked = _selected.Contains(key), Color = Color.FromArgb("#21C7A8") };
        check.CheckedChanged += (_, e) => { if (e.Value) _selected.Add(key); else _selected.Remove(key); UpdateSelection(); };
        var text = new VerticalStackLayout { VerticalOptions = LayoutOptions.Center, Children =
        { new Label { Text = title, TextColor = Colors.White, MaxLines = 2 }, new Label { Text = detail, TextColor = Colors.Gray, FontSize = 12 } } };
        var row = new Grid { ColumnDefinitions = { new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto) }, ColumnSpacing = 8 };
        row.Add(check); row.Add(text, 1);
        if (track is not null)
        {
            var play = new Button { Text = "聴く", FontSize = 12 };
            play.Clicked += async (_, _) =>
            {
                if (_operation is not null) return;
                var asset = new EagleAsset { Id = track.Key, Name = track.Title, FileName = track.Title + track.Extension,
                    FileUri = new Uri(_offline.Store.PathFor(track)).AbsoluteUri, MediaKind = EagleAssetMediaKind.Audio, SizeBytes = track.Length };
                await Navigation.PushAsync(new AudioPlayerPage([asset], 0, _media, _readerStore));
            };
            row.Add(play, 2);
        }
        _list.Children.Add(row);
    }

    private void UpdateSelection()
    {
        var bytes = _selected.Sum(key => Math.Max(0, _sizes.GetValueOrDefault(key)));
        _selectionSummary.Text = $"選択 {_selected.Count}曲 · 合計 {Size(bytes)}" +
            (_selected.Any(key => _sizes.GetValueOrDefault(key) <= 0) ? "（サイズ未確定を含む）" : "");
    }

    private async Task RunAsync(Func<CancellationToken, Task> operation, string completed)
    {
        if (_operation is not null) return;
        if (_selected.Count == 0) { _status.Text = "音声を選択してください。"; return; }
        using var cancel = new CancellationTokenSource();
        cancel.CancelAfter(TimeSpan.FromMinutes(30));
        _operation = cancel;
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
        foreach (var asset in _candidates.Where(x => _selected.Contains(OfflineAudioService.RequestFor(_library, x).Key)))
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
        if (_selected.Any(key => snapshot.Tracks.All(x => x.Key != key))) throw new IOException("選択した音声の一部が端末にありません。保存し直してください。");
        foreach (var track in snapshot.Tracks.Where(x => _selected.Contains(x.Key)))
        {
            var progress = Progress("Watch転送: " + track.Title);
            await Task.Run(() => WearAudioTransfer.SendAsync(context, peer, _offline.Store, track, progress, ct), ct);
        }
    }

    private async Task RemoveSelectedAsync()
    {
        if (_selected.Count == 0 || !await DisplayAlertAsync("端末の音声を削除", "選択した端末内コピーだけを削除します。Watch・Drive・EAGLEの音声は残ります。", "端末から削除", "キャンセル")) return;
        foreach (var key in _selected) await _offline.Store.RemoveAsync(key);
        _selected.Clear(); await RefreshAsync();
    }

    private async Task ChangeLimitAsync()
    {
        var snapshot = await _offline.Store.SnapshotAsync();
        var value = await DisplayPromptAsync("端末の容量上限", "GB単位で指定してください。保存済み音声は自動削除しません。", "保存", "キャンセル", initialValue: ((double)snapshot.LimitBytes / OfflineAudioStore.GiB).ToString("0.##"), keyboard: Keyboard.Numeric);
        if (value is null) return;
        if (!double.TryParse(value, out var gb) || !double.IsFinite(gb) || gb < 0.0625 || gb > 1024) throw new InvalidOperationException("0.0625〜1024 GBを指定してください。");
        await _offline.Store.SetLimitAsync((long)(gb * OfflineAudioStore.GiB)); await RefreshAsync();
    }

    private static string Size(long bytes) => bytes < 0 ? "不明" : bytes >= OfflineAudioStore.GiB ? $"{bytes / (double)OfflineAudioStore.GiB:0.##} GB" : $"{bytes / (1024d * 1024):0.##} MB";
}
