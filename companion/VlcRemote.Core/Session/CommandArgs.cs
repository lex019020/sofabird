using System.Globalization;
using System.Text.Json;

namespace VlcRemote.Core.Session;

/// <summary>Типизированный доступ к <c>args</c> команды с понятными ошибками <c>bad_request</c>.</summary>
public readonly struct CommandArgs
{
    private readonly JsonElement? _el;

    public CommandArgs(JsonElement? el) => _el = el is { ValueKind: JsonValueKind.Object } ? el : null;

    public bool Has(string name) => _el is { } el && el.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null;

    public int? GetInt(string name)
    {
        if (!TryGet(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.Number when v.TryGetInt32(out var i) => i,
            JsonValueKind.Number when v.TryGetDouble(out var d) => (int)Math.Round(d),
            JsonValueKind.String when int.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) => p,
            _ => throw ProtocolException.BadRequest($"arg '{name}' must be an integer"),
        };
    }

    public int RequireInt(string name) => GetInt(name) ?? throw ProtocolException.BadRequest($"arg '{name}' is required");

    public double? GetDouble(string name)
    {
        if (!TryGet(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.GetDouble(),
            JsonValueKind.String when double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var p) => p,
            _ => throw ProtocolException.BadRequest($"arg '{name}' must be a number"),
        };
    }

    public double RequireDouble(string name) => GetDouble(name) ?? throw ProtocolException.BadRequest($"arg '{name}' is required");

    public bool? GetBool(string name)
    {
        if (!TryGet(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => v.GetDouble() != 0,
            JsonValueKind.String when bool.TryParse(v.GetString(), out var b) => b,
            _ => throw ProtocolException.BadRequest($"arg '{name}' must be a boolean"),
        };
    }

    public bool RequireBool(string name) => GetBool(name) ?? throw ProtocolException.BadRequest($"arg '{name}' is required");

    public string? GetString(string name)
    {
        if (!TryGet(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => v.GetRawText(),
            _ => throw ProtocolException.BadRequest($"arg '{name}' must be a string"),
        };
    }

    public string RequireString(string name)
    {
        var s = GetString(name);
        if (string.IsNullOrEmpty(s)) throw ProtocolException.BadRequest($"arg '{name}' is required");
        return s;
    }

    private bool TryGet(string name, out JsonElement value)
    {
        value = default;
        if (_el is not { } el || !el.TryGetProperty(name, out value)) return false;
        return value.ValueKind != JsonValueKind.Null;
    }
}
