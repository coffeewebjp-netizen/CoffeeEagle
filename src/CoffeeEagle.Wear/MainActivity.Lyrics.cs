using Android.Views;
using Android.Widget;
using CoffeeEagle.Offline;
using Color = Android.Graphics.Color;

namespace CoffeeEagle.Wear;

public sealed partial class MainActivity
{
    private LinearLayout _lyricsPanel = null!;
    private TextView _lyricPrevious = null!, _lyricCurrent = null!, _lyricNext = null!, _lyricTitle = null!, _lyricTime = null!;
    private Button _lyricToggle = null!;
    private bool _showLyrics;
    private string? _lyricsIdentity;
    private LrcLyrics? _lyrics;
    private int _lyricsGeneration;

    private void CreateLyricsPanel(LinearLayout root)
    {
        _lyricsPanel = new LinearLayout(this) { Orientation = Orientation.Vertical, Visibility = ViewStates.Gone };
        var navigation = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        navigation.AddView(Button("戻る", () => ShowPlayer(true)), new LinearLayout.LayoutParams(0, Dp(36), 1));
        _lyricToggle = Button("再生", () => Command(AudioPlaybackService.Toggle));
        navigation.AddView(_lyricToggle, new LinearLayout.LayoutParams(0, Dp(36), 2));
        _lyricsPanel.AddView(navigation);
        _lyricTitle = Label("", 11); _lyricTitle.SetMaxLines(1); _lyricTitle.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        _lyricPrevious = Label("", 12); _lyricNext = Label("", 12);
        foreach (var label in new[] { _lyricPrevious, _lyricNext })
        { label.SetTextColor(Color.ParseColor("#98A4B5")); label.SetMaxLines(2); label.Ellipsize = Android.Text.TextUtils.TruncateAt.End; }
        _lyricCurrent = Label("歌詞を読込中…", 18); _lyricCurrent.SetTextColor(Color.ParseColor("#21C7A8"));
        _lyricCurrent.SetPadding(0, Dp(6), 0, Dp(6));
        _lyricTime = Label("", 11);
        foreach (var view in new[] { _lyricTitle, _lyricPrevious, _lyricCurrent, _lyricNext, _lyricTime }) _lyricsPanel.AddView(view);
        root.AddView(_lyricsPanel);
    }

    private void ShowLyrics()
    {
        _showLyrics = true; _lyricsIdentity = null;
        UpdatePlayback(); _scroll.ScrollTo(0, 0);
    }

    private void UpdateLyrics(OfflineTrack? track, int position)
    {
        _lyricToggle.Text = AudioPlaybackService.Current?.IsPlaying == true ? "一時停止" : "再生";
        _lyricTitle.Text = track?.Title ?? "音声を選んでください";
        _lyricTime.Text = Time(position);
        var identity = track is null ? "empty" : track.Key + track.Revision;
        if (_lyricsIdentity != identity)
        {
            _lyricsIdentity = identity;
            _lyrics = null; _lyricPrevious.Text = _lyricNext.Text = "";
            _lyricCurrent.Text = track is null ? "音声を選んでください" : "歌詞を読込中…";
            var generation = ++_lyricsGeneration;
            if (track is not null) _ = LoadLyricsAsync(track, generation);
        }
        if (_lyrics is null) return;
        var words = _lyrics.WindowAt(position);
        if (_lyricPrevious.Text != words.Previous) _lyricPrevious.Text = words.Previous;
        if (_lyricCurrent.Text != words.Current) _lyricCurrent.Text = words.Current;
        if (_lyricNext.Text != words.Next) _lyricNext.Text = words.Next;
    }

    private async Task LoadLyricsAsync(OfflineTrack track, int generation)
    {
        try
        {
            var bytes = await WearAudioStore.Current.Lyrics.ReadAsync(track.Key, track.Revision);
            if (!_visible || generation != _lyricsGeneration) return;
            _lyrics = bytes is null ? null : LrcLyrics.Parse(bytes);
            if (_lyrics is null) _lyricCurrent.Text = "歌詞なし\nスマホで同名の .lrc を保存して送り直してください";
            else if (!_lyrics.IsTimed && string.IsNullOrEmpty(_lyrics.PlainText))
            { _lyrics = null; _lyricCurrent.Text = "表示できる歌詞がありません"; }
            else UpdateLyrics(track, AudioPlaybackService.Current?.Position ?? 0);
        }
        catch (Exception)
        {
            if (_visible && generation == _lyricsGeneration) _lyricCurrent.Text = "歌詞を開けません\nスマホから送り直してください";
        }
    }
}
