using Android.Content;
using Android.Gms.Wearable;
using Android.Runtime;
using CoffeeEagle.Offline;
using System.Text;
using System.Threading.Channels;

namespace CoffeeEagle.WearTransport;

public sealed record WatchPeer(string Id, string Name);

/// <summary>Foreground, nearby companion transfer. Only a verified receiver acknowledgement means success.</summary>
public static class WearAudioTransfer
{
    public static async Task<IReadOnlyList<WatchPeer>> FindReceiversAsync(Context context, CancellationToken ct)
    {
        var info = await WearableClass.GetCapabilityClient(context)
            .GetCapabilityAsync(AudioWire.Capability, CapabilityClient.FilterReachable).WaitAsync(TimeSpan.FromSeconds(15), ct);
        return info.Nodes.Where(x => x.IsNearby).Select(x => new WatchPeer(x.Id, x.DisplayName)).ToArray();
    }

    public static async Task SendAsync(Context context, WatchPeer peer, OfflineAudioStore store, OfflineTrack track,
        IProgress<AudioProgress>? progress, CancellationToken ct)
    {
        if (!await store.VerifyAsync(track, ct)) throw new IOException("端末の音声が不完全です。保存し直してください。");
        using var transfer = CancellationTokenSource.CreateLinkedTokenSource(ct);
        transfer.CancelAfter(TimeSpan.FromMinutes(30));
        var token = transfer.Token;
        var id = Guid.NewGuid().ToString("N");
        var channels = WearableClass.GetChannelClient(context);
        var messages = WearableClass.GetMessageClient(context);
        using var replies = new Replies(peer.Id, id);
        await messages.AddListenerAsync(replies).WaitAsync(TimeSpan.FromSeconds(15), token);
        ChannelClient.IChannel? channel = null;
        Task? proximity = null;
        try
        {
            await RequireNearbyAsync(context, peer.Id, token);
            channel = await channels.OpenChannelAsync(peer.Id, AudioWire.ChannelPrefix + id).WaitAsync(TimeSpan.FromSeconds(15), token);
            using var abort = token.Register(() => _ = QuietCloseAsync(channels, channel));
            proximity = GuardProximityAsync(context, peer.Id, transfer);
            using var javaOutput = await channels.GetOutputStreamAsync(channel).WaitAsync(TimeSpan.FromSeconds(20), token);
            using var output = new OutputStreamInvoker(javaOutput);
            await AudioWire.WriteHeaderAsync(output, new(1, id, track.Request), token);
            await output.FlushAsync(token);
            var ready = await replies.NextAsync(token);
            if (ready == "stored") return; // Receiver already verified this revision and content hash.
            if (ready != "ready") throw new IOException(ready);
            await using var file = File.OpenRead(store.PathFor(track));
            var buffer = new byte[65536];
            long count = 0;
            int read;
            while ((read = await file.ReadAsync(buffer, token)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), token);
                count += read;
                progress?.Report(new(count, track.Length));
            }
            await output.FlushAsync(token);
            var result = await replies.NextAsync(token);
            if (result != "stored") throw new IOException(result);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new IOException("Watchとの近距離接続が切れたか、転送がタイムアウトしました。近くで再送してください。");
        }
        finally
        {
            transfer.Cancel();
            if (channel is not null) await QuietCloseAsync(channels, channel);
            if (proximity is not null) { try { await proximity; } catch { } }
            try { await messages.RemoveListenerAsync(replies).WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
        }
    }

    internal static async Task RequireNearbyAsync(Context context, string nodeId, CancellationToken ct)
    {
        var nodes = await WearableClass.GetNodeClient(context).GetConnectedNodesAsync().WaitAsync(TimeSpan.FromSeconds(10), ct);
        if (!nodes.Any(x => x.Id == nodeId && x.IsNearby)) throw new IOException("端末を近くで接続してから転送してください。");
    }

    private static async Task GuardProximityAsync(Context context, string node, CancellationTokenSource cancel)
    {
        try
        {
            while (!cancel.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(3), cancel.Token);
                await RequireNearbyAsync(context, node, cancel.Token);
            }
        }
        catch { cancel.Cancel(); }
    }

    internal static async Task QuietCloseAsync(ChannelClient client, ChannelClient.IChannel channel)
    {
        try { await client.CloseAsync(channel).WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
    }

    private sealed class Replies(string nodeId, string id) : Java.Lang.Object, MessageClient.IOnMessageReceivedListener
    {
        private readonly Channel<string> _queue = Channel.CreateBounded<string>(4);
        public void OnMessageReceived(IMessageEvent message)
        {
            if (message.SourceNodeId != nodeId || message.Path != AudioWire.AckPrefix + id) return;
            var data = message.GetData();
            if (data is null || data.Length > 1024) return;
            _queue.Writer.TryWrite(Encoding.UTF8.GetString(data));
        }
        public async Task<string> NextAsync(CancellationToken ct) =>
            await _queue.Reader.ReadAsync(ct).AsTask().WaitAsync(TimeSpan.FromSeconds(45), ct);
    }
}

/// <summary>Registered only while the watch explicitly displays its receive screen.</summary>
public sealed class WearAudioReceiver : ChannelClient.ChannelCallback, IAsyncDisposable
{
    private readonly Context _context;
    private readonly OfflineAudioStore _store;
    private readonly Action<string> _status;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _singleTransfer = new(1, 1);
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly HashSet<Task> _active = [];
    private bool _started;

    public WearAudioReceiver(Context context, OfflineAudioStore store, Action<string> status)
    { _context = context.ApplicationContext!; _store = store; _status = status; }

    public async Task StartAsync()
    {
        await _lifecycleGate.WaitAsync();
        try
        {
            _lifetime.Token.ThrowIfCancellationRequested();
            await WearableClass.GetChannelClient(_context).RegisterChannelCallbackAsync(this);
            _started = true;
            await WearableClass.GetCapabilityClient(_context).AddLocalCapabilityAsync(AudioWire.Capability);
            _lifetime.Token.ThrowIfCancellationRequested();
        }
        finally { _lifecycleGate.Release(); }
    }

    public override void OnChannelOpened(ChannelClient.IChannel channel)
    {
        if (!channel.Path.StartsWith(AudioWire.ChannelPrefix, StringComparison.Ordinal)) return;
        if (_lifetime.IsCancellationRequested)
        {
            _ = WearAudioTransfer.QuietCloseAsync(WearableClass.GetChannelClient(_context), channel);
            return;
        }
        // Native stream operations must never block the UI callback thread.
        var task = Task.Run(() => ReceiveAsync(channel));
        lock (_active) _active.Add(task);
        _ = task.ContinueWith(t => { lock (_active) _active.Remove(t); }, TaskScheduler.Default);
    }

    private async Task ReceiveAsync(ChannelClient.IChannel channel)
    {
        var client = WearableClass.GetChannelClient(_context);
        var id = channel.Path[AudioWire.ChannelPrefix.Length..];
        if (!AudioWire.IsTransferId(id)) { await WearAudioTransfer.QuietCloseAsync(client, channel); return; }
        if (!await _singleTransfer.WaitAsync(0))
        {
            await ReplyAsync(channel.NodeId, id, "別の音声を受信中です。後で再送してください。");
            await WearAudioTransfer.QuietCloseAsync(client, channel);
            return;
        }
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        cancel.CancelAfter(TimeSpan.FromMinutes(30));
        using var abort = cancel.Token.Register(() => _ = WearAudioTransfer.QuietCloseAsync(client, channel));
        try
        {
            await WearAudioTransfer.RequireNearbyAsync(_context, channel.NodeId, cancel.Token);
            using var javaInput = await client.GetInputStreamAsync(channel).WaitAsync(TimeSpan.FromSeconds(20), cancel.Token);
            using var input = new InputStreamInvoker(javaInput);
            var header = await AudioWire.ReadHeaderAsync(input, cancel.Token).WaitAsync(TimeSpan.FromSeconds(20), cancel.Token);
            if (header.TransferId != id) throw new InvalidDataException("転送IDが一致しません。");
            var snapshot = await _store.SnapshotAsync(cancel.Token);
            var existing = snapshot.Tracks.FirstOrDefault(x => x.Key == header.Audio.Key && x.Revision == header.Audio.Revision && x.Sha256 == header.Audio.Sha256);
            if (existing is not null && await _store.VerifyAsync(existing, cancel.Token))
            {
                await ReplyAsync(channel.NodeId, id, "stored");
                _status("保存済みです: " + existing.Title);
                return;
            }
            var used = snapshot.Tracks.Where(x => x.Key != header.Audio.Key).Sum(x => x.Length);
            if (header.Audio.Length > snapshot.LimitBytes - used) throw new IOException("Watchの保存上限を超えます。音声を整理するか上限を変更してください。");
            await ReplyAsync(channel.NodeId, id, "ready");
            _status("受信中: " + header.Audio.Title);
            using var content = new LimitedInput(input, header.Audio.Length);
            var lastPercent = -1;
            var progress = new InlineProgress(p =>
            {
                var percent = (int)(p.Bytes * 100 / p.Total);
                if (percent != lastPercent) { lastPercent = percent; _status($"受信 {percent}%\n{header.Audio.Title}"); }
            });
            await _store.SaveAsync(header.Audio, _ => Task.FromResult<Stream>(content), progress, cancel.Token);
            await ReplyAsync(channel.NodeId, id, "stored");
            _status("保存完了: " + header.Audio.Title);
        }
        catch (Exception ex)
        {
            _status(_lifetime.IsCancellationRequested ? "受信を中止しました。再送できます。" : "受信失敗: " + ex.Message);
            await ReplyAsync(channel.NodeId, id, "保存できませんでした。Watchの表示を確認して再送してください。");
        }
        finally
        {
            await WearAudioTransfer.QuietCloseAsync(client, channel);
            _singleTransfer.Release();
        }
    }

    private async Task ReplyAsync(string node, string id, string status)
    {
        try { await WearableClass.GetMessageClient(_context).SendMessageAsync(node, AudioWire.AckPrefix + id, Encoding.UTF8.GetBytes(status)).WaitAsync(TimeSpan.FromSeconds(5)); }
        catch { /* The sender times out and can safely repeat the same verified transfer. */ }
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        await _lifecycleGate.WaitAsync();
        try
        {
            if (_started)
            {
                try { await WearableClass.GetCapabilityClient(_context).RemoveLocalCapabilityAsync(AudioWire.Capability).WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
                try { await WearableClass.GetChannelClient(_context).UnregisterChannelCallbackAsync(this).WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
                _started = false;
            }
        }
        finally { _lifecycleGate.Release(); }
        Task[] active;
        lock (_active) active = _active.ToArray();
        try { await Task.WhenAll(active).WaitAsync(TimeSpan.FromSeconds(10)); } catch { }
        // Keep the Java callback alive until its operations have stopped.
    }

    private sealed class InlineProgress(Action<AudioProgress> report) : IProgress<AudioProgress>
    { public void Report(AudioProgress value) => report(value); }

    private sealed class LimitedInput(Stream source, long remaining) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (remaining == 0) return 0;
            var read = await source.ReadAsync(buffer[..(int)Math.Min(buffer.Length, remaining)], ct);
            remaining -= read;
            return read;
        }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override void Flush() { }
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }
}
