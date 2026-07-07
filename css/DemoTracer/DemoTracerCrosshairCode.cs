namespace DemoTracer;

public sealed partial class DemoTracerPlugin
{
    private static bool TryDecodeCrosshairShareCodeToPaintConfig(
        string code,
        out NativeHudReticlePaintConfig config,
        out string reason)
    {
        config = default;
        reason = string.Empty;

        var normalized = NormalizeCrosshairCode(code);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            reason = "invalid_crosshair_code";
            return false;
        }

        var body = normalized.Replace("CSGO", "", StringComparison.OrdinalIgnoreCase)
            .Replace("-", "", StringComparison.Ordinal);
        if (body.Length != 25)
        {
            reason = "invalid_crosshair_code_length";
            return false;
        }

        Span<byte> bytes = stackalloc byte[18];
        const string alphabet = "ABCDEFGHJKLMNOPQRSTUVWXYZabcdefhijkmnopqrstuvwxyz23456789";
        for (var i = body.Length - 1; i >= 0; i--)
        {
            var index = alphabet.IndexOf(body[i]);
            if (index < 0)
            {
                reason = "invalid_crosshair_code_alphabet";
                return false;
            }

            if (!MultiplyBigEndian(bytes, alphabet.Length) ||
                !AddBigEndian(bytes, index))
            {
                reason = "invalid_crosshair_code_overflow";
                return false;
            }
        }

        var checksum = 0;
        for (var i = 1; i < bytes.Length; i++)
            checksum = (checksum + bytes[i]) & 0xFF;
        if (bytes[0] != checksum)
        {
            reason = $"invalid_crosshair_checksum_{bytes[0]}_{checksum}";
            return false;
        }

        var packedFlags = bytes[13] >> 4;
        config = new NativeHudReticlePaintConfig
        {
            Size = BotControllerNative.HudReticlePaintConfigByteSize,
            Style = (bytes[13] & 0xF) >> 1,
            Color = bytes[10] & 7,
            DrawOutline = (bytes[10] & 8) != 0 ? 1 : 0,
            Dot = (packedFlags & 1) != 0 ? 1 : 0,
            GapUseWeaponValue = 0,
            UseAlpha = (packedFlags & 4) != 0 ? 1 : 0,
            TStyle = (packedFlags & 8) != 0 ? 1 : 0,
            Gap100 = SignedByte(bytes[2]) * 10,
            Size100 = bytes[14] * 10,
            Thickness100 = bytes[12] * 10,
            Outline100 = bytes[3] * 50,
            Alpha = bytes[7],
            Red = bytes[4],
            Green = bytes[5],
            Blue = bytes[6]
        };
        return true;

        static bool MultiplyBigEndian(Span<byte> value, int factor)
        {
            var carry = 0;
            for (var i = value.Length - 1; i >= 0; i--)
            {
                var next = value[i] * factor + carry;
                value[i] = (byte)(next & 0xFF);
                carry = next >> 8;
            }
            return carry == 0;
        }

        static bool AddBigEndian(Span<byte> value, int addend)
        {
            var carry = addend;
            for (var i = value.Length - 1; i >= 0 && carry != 0; i--)
            {
                var next = value[i] + carry;
                value[i] = (byte)(next & 0xFF);
                carry = next >> 8;
            }
            return carry == 0;
        }

        static int SignedByte(byte value)
            => value >= 128 ? value - 256 : value;
    }
}
