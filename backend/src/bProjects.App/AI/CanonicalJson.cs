using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace bProjects.App.AI;

/// <summary>
/// Forma canonica del JSON per gli hash dei dati di lavoro (WorkManifest, WorkOutput inline): proprietà ordinate per code unit UTF-16,
/// nessuno spazio, array nell'ordine dato, escape minimo delle stringhe come RFC 8785. I numeri interi sono resi esattamente; i numeri
/// non interi usano la forma più breve «R» di .NET con esponente minuscolo (i payload di dominio contengono interi e stringhe).
/// L'hash è lo SHA-256 esadecimale minuscolo del testo UTF-8 canonico: JSONB non conserva spaziatura né ordine, quindi mai l'hash del testo grezzo.
/// </summary>
public static class CanonicalJson
{
    public static string Canonicalize(string json)
    {
        using var document = JsonDocument.Parse(json);
        return Canonicalize(document.RootElement);
    }

    public static string Canonicalize(JsonElement element)
    {
        var builder = new StringBuilder();
        Write(builder, element);
        return builder.ToString();
    }

    public static string Sha256Hex(string json) => HashText(Canonicalize(json));

    public static string Sha256Hex(JsonElement element) => HashText(Canonicalize(element));

    /// <summary>SHA-256 esadecimale minuscolo di un testo già canonico.</summary>
    public static string HashText(string canonical) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));

    private static void Write(StringBuilder builder, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                builder.Append('{');
                var first = true;
                foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    if (!first)
                    {
                        builder.Append(',');
                    }

                    first = false;
                    WriteString(builder, property.Name);
                    builder.Append(':');
                    Write(builder, property.Value);
                }

                builder.Append('}');
                break;
            case JsonValueKind.Array:
                builder.Append('[');
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    if (index++ > 0)
                    {
                        builder.Append(',');
                    }

                    Write(builder, item);
                }

                builder.Append(']');
                break;
            case JsonValueKind.String:
                WriteString(builder, element.GetString()!);
                break;
            case JsonValueKind.Number:
                builder.Append(element.TryGetInt64(out var integer)
                    ? integer.ToString(CultureInfo.InvariantCulture)
                    : element.GetDouble().ToString("R", CultureInfo.InvariantCulture).Replace("E+", "e+").Replace("E-", "e-"));
                break;
            case JsonValueKind.True:
                builder.Append("true");
                break;
            case JsonValueKind.False:
                builder.Append("false");
                break;
            default:
                builder.Append("null");
                break;
        }
    }

    private static void WriteString(StringBuilder builder, string value)
    {
        builder.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '\b': builder.Append("\\b"); break;
                case '\f': builder.Append("\\f"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                case < ' ': builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture)); break;
                default: builder.Append(c); break;
            }
        }

        builder.Append('"');
    }
}
