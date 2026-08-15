using System.Text;

namespace gatOS.Paint;

/// <summary>Pure transformation of KSA's part fragment shader source.</summary>
public static class PaintShaderTransform
{
    private const string Marker = "// --- gatOS paint: state bits 11..31 ---";

    /// <summary>Injects the paint block immediately after the sampledColor declaration.</summary>
    public static bool TryInject(string source, PaintBlendMode mode, out string transformed, out string? error)
    {
        transformed = source;
        error = null;
        if (source.Contains(Marker, StringComparison.Ordinal)) return true;
        var normalized = source.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (!normalized.Contains("inStateFlags", StringComparison.Ordinal))
        {
            error = "inStateFlags varying was not found";
            return false;
        }

        var lines = normalized.Split('\n');
        var anchor = -1;
        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.StartsWith("vec3 sampledColor", StringComparison.Ordinal)
                && trimmed.EndsWith(';'))
            {
                anchor = i;
                break;
            }
        }
        if (anchor < 0)
        {
            error = "vec3 sampledColor albedo anchor was not found";
            return false;
        }

        var apply = mode switch
        {
            PaintBlendMode.Tint =>
                "            sampledColor = gatosPaint * (dot(sampledColor, vec3(0.2126, 0.7152, 0.0722)) * 2.0);",
            PaintBlendMode.Replace => "            sampledColor = gatosPaint;",
            _ => "            sampledColor *= gatosPaint;",
        };
        var snippet = $$"""

    {{Marker}}
    {
        uint gatosPaintPacked = inStateFlags >> {{PaintBits.Shift}}u;
        if (gatosPaintPacked != 0u)
        {
            vec3 gatosPaint = gammaToLinear(vec3(
                float((gatosPaintPacked >> 14u) & 0x7Fu),
                float((gatosPaintPacked >> 7u) & 0x7Fu),
                float( gatosPaintPacked        & 0x7Fu)) * (1.0 / 127.0));
{{apply}}
        }
    }
""";

        var builder = new StringBuilder(normalized.Length + snippet.Length + 1);
        for (var i = 0; i < lines.Length; i++)
        {
            builder.Append(lines[i]).Append('\n');
            if (i == anchor) builder.Append(snippet);
        }
        transformed = builder.ToString();
        return true;
    }
}
