using System.Buffers.Binary;
using System.Text.Json;

namespace CoffeeEagle.Offline;

public sealed record AudioEnvelope(int Version, string TransferId, AudioRequest Audio);

public static class AudioWire
{
    public const string ChannelPrefix = "/coffeeeagle/audio/v1/";
    public const string AckPrefix = "/coffeeeagle/audio-ack/v1/";
    public const string Capability = "coffeeeagle_audio_receive_v1";
    private const int MaxHeaderBytes = 4096;

    public static bool IsTransferId(string id) => Guid.TryParseExact(id, "N", out _);

    public static async Task WriteHeaderAsync(Stream output, AudioEnvelope envelope, CancellationToken ct)
    {
        Validate(envelope);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope);
        if (bytes.Length > MaxHeaderBytes) throw new InvalidDataException("転送情報が大きすぎます。");
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
        return envelope;
    }

    private static void Validate(AudioEnvelope envelope)
    {
        if (envelope.Version != 1 || !IsTransferId(envelope.TransferId) || envelope.Audio is null)
            throw new InvalidDataException("この転送バージョンに対応していません。");
        envelope.Audio.Validate(transferred: true);
    }
}
