using System.Buffers.Binary;
using System.Text.Json;

namespace CoffeeEagle.Offline;

public sealed record AudioEnvelope(int Version, string TransferId, AudioRequest Audio, byte[]? Artwork = null);

public static class AudioWire
{
    public const string ChannelPrefix = "/coffeeeagle/audio/v1/";
    public const string ArtworkChannelPrefix = "/coffeeeagle/audio/v2/";
    public const string AckPrefix = "/coffeeeagle/audio-ack/v1/";
    public const string Capability = "coffeeeagle_audio_receive_v1";
    public const string ArtworkCapability = "coffeeeagle_audio_receive_v2";
    private const int MaxHeaderBytes = 32 * 1024;
    private static int HeaderLimit(int version) => version == 1 ? 4096 : MaxHeaderBytes;

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
        if (envelope.Version is not (1 or 2) || !IsTransferId(envelope.TransferId) || envelope.Audio is null ||
            (envelope.Version == 1 && envelope.Artwork is not null))
            throw new InvalidDataException("この転送バージョンに対応していません。");
        envelope.Audio.Validate(transferred: true);
        if (envelope.Artwork is not null) OfflineArtworkStore.Validate(envelope.Artwork);
    }
}
