using System.Globalization;
using System.Text;

namespace VlcRemote.Core.Bluetooth;

/// <summary>Platform-independent Bluetooth helpers: address formatting and SDP attribute encoding.</summary>
public static class BluetoothFormat
{
    /// <summary>SDP attribute id of the primary-language service name (Bluetooth Assigned Numbers, 0x0100).</summary>
    public const ushort SdpServiceNameAttributeId = 0x0100;

    /// <summary>
    /// Formats a 48-bit Bluetooth device address (as returned by WinRT <c>BluetoothAdapter.BluetoothAddress</c>)
    /// as <c>AA:BB:CC:DD:EE:FF</c>: upper case, most significant byte first. Bits above 48 are ignored.
    /// </summary>
    public static string FormatAddress(ulong address)
    {
        var sb = new StringBuilder(17);
        for (var shift = 40; shift >= 0; shift -= 8)
        {
            if (sb.Length > 0) sb.Append(':');
            sb.Append(((byte)(address >> shift)).ToString("X2", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    /// <summary>Parses <c>AA:BB:CC:DD:EE:FF</c> (or with <c>-</c>, any case) back to the 48-bit value.</summary>
    public static bool TryParseAddress(string? text, out ulong address)
    {
        address = 0;
        if (text is null) return false;
        var parts = text.Trim().Split(':', '-');
        if (parts.Length != 6) return false;
        foreach (var p in parts)
        {
            if (p.Length != 2 || !byte.TryParse(p, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var b))
                return false;
            address = (address << 8) | b;
        }
        return true;
    }

    /// <summary>
    /// Encodes a text string as an SDP data element: type 4 (text), size index 5 (8-bit length follows) → header
    /// <c>0x25</c>, then the length and the UTF-8 bytes. Strings longer than 255 bytes use size index 6 (<c>0x26</c>,
    /// 16-bit big-endian length).
    /// </summary>
    public static byte[] EncodeSdpText(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length <= byte.MaxValue)
        {
            var result = new byte[bytes.Length + 2];
            result[0] = 0x25;
            result[1] = (byte)bytes.Length;
            bytes.CopyTo(result, 2);
            return result;
        }
        if (bytes.Length > ushort.MaxValue) throw new ArgumentException("SDP text is too long", nameof(value));
        var longResult = new byte[bytes.Length + 3];
        longResult[0] = 0x26;
        longResult[1] = (byte)(bytes.Length >> 8);
        longResult[2] = (byte)bytes.Length;
        bytes.CopyTo(longResult, 3);
        return longResult;
    }
}
