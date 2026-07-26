using System.Text.Json;
using MecchaCamouflage.Core;

namespace MecchaCamouflage.Controller;

/// <summary>Research-only albedo snapshots retained beside a run after a controlled paint.</summary>
public sealed record ResearchTextureDeltaArtifact(
    bool Success,
    int TextureSize,
    int ChangedPixels,
    string BeforePngPath,
    string AfterPngPath,
    string DeltaMaskPath,
    string Component,
    string TargetSource,
    string Error);

/// <summary>
/// Stages one pinned component's native albedo RGBA exports and renders a real changed-pixel
/// mask. It never calls into the game; callers provide the two already-recorded probe artifacts.
/// Metallic and roughness changes are intentionally not represented by this albedo-only PNG.
/// </summary>
public static class ResearchTextureDeltaArtifacts
{
    public static ResearchTextureDeltaArtifact StageAndRender(
        string beforeProbeArtifactPath,
        string afterProbeArtifactPath,
        string outputDirectory,
        string? expectedComponent = null)
    {
        try
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(beforeProbeArtifactPath);
            ArgumentException.ThrowIfNullOrWhiteSpace(afterProbeArtifactPath);
            ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
            var before = ReadSelectedAlbedoDump(beforeProbeArtifactPath);
            var after = ReadSelectedAlbedoDump(afterProbeArtifactPath);
            if (!string.Equals(before.Component, after.Component, StringComparison.OrdinalIgnoreCase))
            {
                return Failure(
                    "before and after texture exports selected different component addresses; no delta was rendered");
            }
            if (!string.IsNullOrWhiteSpace(expectedComponent) &&
                (!string.Equals(before.Component, expectedComponent, StringComparison.OrdinalIgnoreCase) ||
                 !string.Equals(after.Component, expectedComponent, StringComparison.OrdinalIgnoreCase)))
            {
                return Failure(
                    "texture export does not match the pinned discovery receiver; no delta was rendered");
            }
            if (!string.IsNullOrWhiteSpace(before.Outer) &&
                !string.IsNullOrWhiteSpace(after.Outer) &&
                !string.Equals(before.Outer, after.Outer, StringComparison.OrdinalIgnoreCase))
            {
                return Failure(
                    "before and after texture exports selected different component outers; no delta was rendered");
            }
            if (!after.BaselineComponentMatch)
            {
                return Failure(
                    "after texture export was not compared with a baseline for the same selected component");
            }
            if (before.TextureSize != after.TextureSize)
                return Failure("before and after albedo exports use different sizes");

            var expectedBytes = checked(before.TextureSize * before.TextureSize * 4);
            var beforeRgba = File.ReadAllBytes(before.Path);
            var afterRgba = File.ReadAllBytes(after.Path);
            if (beforeRgba.Length != expectedBytes || afterRgba.Length != expectedBytes)
                return Failure("native albedo RGBA export length does not match its reported texture size");

            var directory = Path.GetFullPath(outputDirectory);
            Directory.CreateDirectory(directory);
            var stagedBeforeRaw = Path.Combine(directory, "albedo-before.rgba");
            var stagedAfterRaw = Path.Combine(directory, "albedo-after.rgba");
            File.Copy(before.Path, stagedBeforeRaw, overwrite: true);
            File.Copy(after.Path, stagedAfterRaw, overwrite: true);

            var beforePng = Path.Combine(directory, "albedo-before.png");
            var afterPng = Path.Combine(directory, "albedo-after.png");
            var maskPng = Path.Combine(directory, "albedo-delta-mask.png");
            UvReplayAtlasPng.Write(beforePng, new UvReplayAtlas(before.TextureSize, before.TextureSize, beforeRgba));
            UvReplayAtlasPng.Write(afterPng, new UvReplayAtlas(after.TextureSize, after.TextureSize, afterRgba));
            var mask = new byte[expectedBytes];
            var changedPixels = 0;
            for (var pixel = 0; pixel < before.TextureSize * before.TextureSize; ++pixel)
            {
                var offset = pixel * 4;
                var changed = beforeRgba[offset] != afterRgba[offset] ||
                              beforeRgba[offset + 1] != afterRgba[offset + 1] ||
                              beforeRgba[offset + 2] != afterRgba[offset + 2] ||
                              beforeRgba[offset + 3] != afterRgba[offset + 3];
                if (changed)
                {
                    ++changedPixels;
                    mask[offset] = 70;
                    mask[offset + 1] = 210;
                    mask[offset + 2] = 255;
                    mask[offset + 3] = 255;
                }
                else
                {
                    mask[offset] = 8;
                    mask[offset + 1] = 8;
                    mask[offset + 2] = 12;
                    mask[offset + 3] = 255;
                }
            }
            UvReplayAtlasPng.Write(maskPng, new UvReplayAtlas(before.TextureSize, before.TextureSize, mask));
            return new ResearchTextureDeltaArtifact(
                true,
                before.TextureSize,
                changedPixels,
                beforePng,
                afterPng,
                maskPng,
                before.Component,
                before.Source,
                "");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException or OverflowException)
        {
            return Failure(ex.Message);
        }
    }

    private static ResearchTextureDeltaArtifact Failure(string error) => new(false, 0, 0, "", "", "", "", "", error);

    private static TextureDump ReadSelectedAlbedoDump(string probeArtifactPath)
    {
        using var wrapper = JsonDocument.Parse(File.ReadAllText(probeArtifactPath));
        var reply = RequiredProperty(wrapper.RootElement, "Reply");
        var raw = RequiredProperty(reply, "Raw").GetString();
        if (string.IsNullOrWhiteSpace(raw))
            throw new InvalidDataException("research texture probe artifact has no native reply");
        using var native = JsonDocument.Parse(raw);
        var metadata = RequiredProperty(native.RootElement, "metadata");
        var inventory = RequiredProperty(metadata, "runtime_paint_component_inventory");
        if (inventory.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("research texture probe inventory is not an array");
        var targetMarker = TryProperty(metadata, "research_texture_export_target_component", out var explicitTarget) &&
                           explicitTarget.ValueKind == JsonValueKind.String &&
                           !string.IsNullOrWhiteSpace(explicitTarget.GetString())
            ? "matches_texture_export_target"
            : "matches_resolved_component";
        TextureDump? selected = null;
        foreach (var component in inventory.EnumerateArray())
        {
            if (!TryProperty(component, targetMarker, out var matches) ||
                matches.ValueKind is not JsonValueKind.True)
            {
                continue;
            }
            if (selected is not null)
                throw new InvalidDataException("research texture probe selected more than one component");
            var delta = RequiredProperty(component, "texture_delta");
            if (!TryProperty(delta, "albedo_dump_written", out var written) || written.ValueKind is not JsonValueKind.True)
                throw new InvalidDataException("selected component did not write an albedo dump");
            if (!TryProperty(delta, "albedo_dump_texture_size", out var size) || !size.TryGetInt32(out var textureSize) || textureSize <= 0)
                throw new InvalidDataException("selected component albedo dump has no valid texture size");
            var path = RequiredProperty(delta, "albedo_dump_path").GetString();
            if (string.IsNullOrWhiteSpace(path))
                throw new InvalidDataException("selected component albedo dump has no path");
            var componentAddress = RequiredProperty(component, "component").GetString();
            if (string.IsNullOrWhiteSpace(componentAddress))
                throw new InvalidDataException("selected component has no stable address");
            var outer = TryProperty(component, "outer", out var outerElement) && outerElement.ValueKind == JsonValueKind.String
                ? outerElement.GetString() ?? ""
                : "";
            var baselineComponentMatch = TryProperty(delta, "baseline_component_match", out var baselineMatch) &&
                                         baselineMatch.ValueKind is JsonValueKind.True;
            var source = TryProperty(metadata, "research_texture_export_target_source", out var sourceElement) &&
                         sourceElement.ValueKind == JsonValueKind.String
                ? sourceElement.GetString() ?? ""
                : targetMarker;
            selected = new TextureDump(
                componentAddress,
                outer,
                Path.GetFullPath(path),
                textureSize,
                baselineComponentMatch,
                source);
        }
        return selected ?? throw new InvalidDataException(
            "research texture probe did not include its selected export component");
    }

    private static JsonElement RequiredProperty(JsonElement element, string name)
    {
        if (TryProperty(element, name, out var value))
            return value;
        throw new InvalidDataException("research texture probe is missing " + name);
    }

    private static bool TryProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }
        value = default;
        return false;
    }

    private sealed record TextureDump(
        string Component,
        string Outer,
        string Path,
        int TextureSize,
        bool BaselineComponentMatch,
        string Source);
}
