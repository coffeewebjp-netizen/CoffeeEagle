using System.Buffers.Binary;
using System.Text.Json;

namespace CoffeeEagle.Offline;

public sealed record AudioEnvelope(int Version, string TransferId, AudioRequest Audio, byte[]? Artwork = null, byte[]? Lyrics = null);

public static class AudioWire
{
    public const string ChannelPrefix = "/coffeeeagle/audio/v1/";
    public const string ArtworkChannelPrefix = "/coffeeeagle/audio/v2/";
    public const string LyricsChannelPrefix = "/coffeeeagle/audio/v3/";
    public const string AckPrefix = "/coffeeeagle/audio-ack/v1/";
    public const string Capability = "coffeeeagle_audio_receive_v1";
    public const string ArtworkCapability = "coffeeeagle_audio_receive_v2";
    public const string LyricsCapability = "coffeeeagle_audio_receive_v3";
    private const int MaxHeaderBytes = 128 * 1024;
    private static int HeaderLimit(int version) => version == 1 ? 4096 : version == 2 ? 32 * 1024 : MaxHeaderBytes;

    public static bool IsTransferId(string id) => Guid.TryParseExact(id, "N", out _);

    public static async Task WriteHeaderAsync(Stream output, AudioEnvelope envelope, CancellationToken ct)
    {
        Validate(envelope);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope);
        if (bytes.Length > HeaderLimit(envelope.Version)) throw new InvalidDataException("転送情報が大きすぎます。");
        var length = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        await output.WriteAsync(length, ct);
        await output.WriteAsync(bytes, ct);
    }

    public static async Task<AudioEnvelope> ReadHeaderAsync(Stream input, CancellationToken ct)
    {
        var length = new byte[4];
        await input.ReadExactlyAsync(length, ct);
        var size = BinaryPrimitives.ReadInt32BigEndian(length);
        if (size <= 0 || size > MaxHeaderBytes) throw new InvalidDataException("転送情報のサイズが無効です。");
        var bytes = new byte[size];
        await input.ReadExactlyAsync(bytes, ct);
        var envelope = JsonSerializer.Deserialize<AudioEnvelope>(bytes) ?? throw new InvalidDataException("転送情報が空です。");
        Validate(envelope);
        if (size > HeaderLimit(envelope.Version)) throw new InvalidDataException("転送情報が大きすぎます。");
        return envelope;
    }

    private static void Validate(AudioEnvelope envelope)
    {
        if (envelope.Version is not (1 or 2 or 3) || !IsTransferId(envelope.TransferId) || envelope.Audio is null ||
            (envelope.Version == 1 && envelope.Artwork is not null) || (envelope.Version < 3 && envelope.Lyrics is not null))
            throw new InvalidDataException("この転送バージョンに対応していません。");
        envelope.Audio.Validate(transferred: true);
        if (envelope.Artwork is not null) OfflineArtworkStore.Validate(envelope.Artwork);
        if (envelope.Lyrics is not null) LrcLyrics.Parse(envelope.Lyrics);
    }

    public static Task ApplyLyricsAsync(OfflineAudioStore store, AudioEnvelope envelope, CancellationToken ct = default)
    {
        Validate(envelope);
        // In v3 null is a confirmed missing sidecar. Legacy senders cannot erase saved lyrics.
        return envelope.Version == 3 ? store.Lyrics.SaveAsync(envelope.Audio.Key, envelope.Audio.Revision, envelope.Lyrics, ct) : Task.CompletedTask;
    }
}
