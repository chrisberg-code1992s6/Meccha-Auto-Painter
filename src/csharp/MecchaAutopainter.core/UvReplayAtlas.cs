using System.IO.Compression;

namespace MecchaCamouflage.Core;

/// <summary>Replay pass carried by a research-only UV stroke-plan sidecar.</summary>
public enum UvReplayPass
{
    Fill,
    Paint
}

/// <summary>
/// One actual (post-limit) stroke from the native direct-paint replay plan.
/// </summary>
public sealed record UvReplayStroke(
    double U,
    double V,
    double PlannerRadiusUv,
    UvReplayPass Pass,
    string Region,
    string BodyRegion);

/// <summary>Portable representation of the native research-only UV replay sidecar.</summary>
public sealed record UvReplayPlan(int TextureSize, IReadOnlyList<UvReplayStroke> Strokes);

/// <summary>RGBA image returned by the pure atlas rasterizer.</summary>
public sealed record UvReplayAtlas(int Width, int Height, byte[] Rgba)
{
    public byte[] RgbaAt(int x, int y)
    {
        if (x < 0 || y < 0 || x >= Width || y >= Height)
            throw new ArgumentOutOfRangeException($"({x}, {y}) is outside the UV replay atlas.");
        var offset = checked((y * Width + x) * 4);
        return [Rgba[offset], Rgba[offset + 1], Rgba[offset + 2], Rgba[offset + 3]];
    }
}

/// <summary>
/// Produces a pass-aware UV diagnostic, not a simulation of the game's mesh/world-space brush.
/// The atlas is two columns (Fill, Paint), both rendered from the
/// direct planner radius that the game receives.
/// </summary>
public static class UvReplayAtlasRasterizer
{
    public static readonly byte[] BackgroundColor = [8, 8, 12, 255];
    public static readonly byte[] FillColor = [255, 170, 64, 255];
    public static readonly byte[] PaintColor = [70, 210, 255, 255];

    public static UvReplayAtlas Render(UvReplayPlan plan, int? tileSize = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(plan.Strokes);
        // Full-size game atlases can be 65536 texels. The diagnostic preserves UV ratios at a
        // bounded portable resolution rather than allocating a multi-gigabyte PNG.
        var size = tileSize ?? Math.Clamp(plan.TextureSize, 16, 1024);
        if (size is < 16 or > 2048)
            throw new ArgumentOutOfRangeException(nameof(tileSize), "The UV replay tile size must be from 16 through 2048.");

        var width = checked(size * 2);
        var height = size;
        var rgba = new byte[checked(width * height * 4)];
        for (var offset = 0; offset < rgba.Length; offset += 4)
        {
            rgba[offset] = BackgroundColor[0];
            rgba[offset + 1] = BackgroundColor[1];
            rgba[offset + 2] = BackgroundColor[2];
            rgba[offset + 3] = BackgroundColor[3];
        }

        foreach (var stroke in plan.Strokes)
        {
            if (!IsFiniteUv(stroke.U) || !IsFiniteUv(stroke.V))
                continue;
            var column = PassColumn(stroke.Pass);
            var color = PassColor(stroke.Pass);
            DrawCircle(rgba, width, height, column * size, 0, size, stroke.U, stroke.V, stroke.PlannerRadiusUv, color);
        }

        return new UvReplayAtlas(width, height, rgba);
    }

    private static bool IsFiniteUv(double value) => double.IsFinite(value) && value >= 0.0 && value <= 1.0;

    private static int PassColumn(UvReplayPass pass) => pass switch
    {
        UvReplayPass.Fill => 0,
        UvReplayPass.Paint => 1,
        _ => throw new ArgumentOutOfRangeException(nameof(pass), pass, "Unknown replay pass.")
    };

    private static byte[] PassColor(UvReplayPass pass) => pass switch
    {
        UvReplayPass.Fill => FillColor,
        UvReplayPass.Paint => PaintColor,
        _ => throw new ArgumentOutOfRangeException(nameof(pass), pass, "Unknown replay pass.")
    };

    private static void DrawCircle(
        byte[] rgba,
        int imageWidth,
        int imageHeight,
        int tileLeft,
        int tileTop,
        int tileSize,
        double u,
        double v,
        double radiusUv,
        byte[] color)
    {
        if (!double.IsFinite(radiusUv) || radiusUv <= 0.0)
            return;
        var centerX = tileLeft + Math.Clamp((int)Math.Round(u * (tileSize - 1)), 0, tileSize - 1);
        var centerY = tileTop + Math.Clamp((int)Math.Round((1.0 - v) * (tileSize - 1)), 0, tileSize - 1);
        var radius = Math.Max(1, (int)Math.Round(radiusUv * tileSize));
        var radiusSquared = radius * radius;
        var minY = Math.Max(tileTop, centerY - radius);
        var maxY = Math.Min(tileTop + tileSize - 1, centerY + radius);
        var minX = Math.Max(tileLeft, centerX - radius);
        var maxX = Math.Min(tileLeft + tileSize - 1, centerX + radius);
        for (var y = minY; y <= maxY; ++y)
        {
            var dy = y - centerY;
            for (var x = minX; x <= maxX; ++x)
            {
                var dx = x - centerX;
                if (dx * dx + dy * dy > radiusSquared)
                    continue;
                var offset = checked((y * imageWidth + x) * 4);
                rgba[offset] = color[0];
                rgba[offset + 1] = color[1];
                rgba[offset + 2] = color[2];
                rgba[offset + 3] = color[3];
            }
        }
    }

}

/// <summary>Minimal dependency-free PNG writer for research artifacts.</summary>
public static class UvReplayAtlasPng
{
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];

    public static void Write(string path, UvReplayAtlas atlas)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(atlas);
        if (atlas.Width <= 0 || atlas.Height <= 0 ||
            atlas.Rgba.Length != checked(atlas.Width * atlas.Height * 4))
        {
            throw new ArgumentException("The UV replay atlas does not have a valid RGBA layout.", nameof(atlas));
        }

        var parent = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrWhiteSpace(parent))
            Directory.CreateDirectory(parent);

        using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        output.Write(Signature);
        var ihdr = new byte[13];
        WriteBigEndian(ihdr, 0, (uint)atlas.Width);
        WriteBigEndian(ihdr, 4, (uint)atlas.Height);
        ihdr[8] = 8; // bit depth
        ihdr[9] = 6; // RGBA
        WriteChunk(output, "IHDR", ihdr);
        WriteChunk(output, "IDAT", CompressRows(atlas));
        WriteChunk(output, "IEND", []);
    }

    private static byte[] CompressRows(UvReplayAtlas atlas)
    {
        using var uncompressed = new MemoryStream(checked(atlas.Height * (atlas.Width * 4 + 1)));
        for (var y = 0; y < atlas.Height; ++y)
        {
            uncompressed.WriteByte(0); // no PNG row filter; diagnostics favor transparency over compression ratio
            uncompressed.Write(atlas.Rgba, y * atlas.Width * 4, atlas.Width * 4);
        }

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
            uncompressed.WriteTo(zlib);
        return compressed.ToArray();
    }

    private static void WriteChunk(Stream output, string type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        WriteBigEndian(length, (uint)data.Length);
        output.Write(length);
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        output.Write(typeBytes);
        output.Write(data);
        var crc = Crc32(typeBytes, data);
        Span<byte> crcBytes = stackalloc byte[4];
        WriteBigEndian(crcBytes, crc);
        output.Write(crcBytes);
    }

    private static void WriteBigEndian(byte[] target, int offset, uint value)
    {
        target[offset] = (byte)(value >> 24);
        target[offset + 1] = (byte)(value >> 16);
        target[offset + 2] = (byte)(value >> 8);
        target[offset + 3] = (byte)value;
    }

    private static void WriteBigEndian(Span<byte> target, uint value)
    {
        target[0] = (byte)(value >> 24);
        target[1] = (byte)(value >> 16);
        target[2] = (byte)(value >> 8);
        target[3] = (byte)value;
    }

    private static uint Crc32(byte[] type, byte[] data)
    {
        var crc = 0xffffffffu;
        foreach (var value in type)
            crc = Crc32Step(crc, value);
        foreach (var value in data)
            crc = Crc32Step(crc, value);
        return ~crc;
    }

    private static uint Crc32Step(uint crc, byte value)
    {
        crc ^= value;
        for (var bit = 0; bit < 8; ++bit)
            crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xedb88320u : crc >> 1;
        return crc;
    }
}
