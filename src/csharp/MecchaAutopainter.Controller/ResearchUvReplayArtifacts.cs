using System.Text.Json;
using MecchaCamouflage.Core;

namespace MecchaCamouflage.Controller;

/// <summary>Result of retaining a native replay-plan sidecar with a research run.</summary>
public sealed record ResearchUvReplayArtifact(bool Success, string PlanPath, string AtlasPath, string Error);

/// <summary>
/// Keeps the native, post-truncation UV plan beside its controller-owned run artifacts and turns
/// it into a portable PNG. This is deliberately outside the injected DLL so the production bridge
/// does not gain an image-codec dependency.
/// </summary>
public static class ResearchUvReplayArtifacts
{
    private const string PlanFileName = "uv-replay-plan.json";
    private const string AtlasFileName = "uv-replay-atlas.png";

    public static ResearchUvReplayArtifact StageAndRender(BridgeReply reply, string artifactDirectory)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(reply);
            ArgumentException.ThrowIfNullOrWhiteSpace(artifactDirectory);
            if (!reply.Ok || !reply.Success)
            {
                return Failure(
                    "native paint did not complete successfully; its planned replay sidecar was intentionally not staged");
            }
            var sourcePath = ReadNativeSidecarPath(reply);
            if (string.IsNullOrWhiteSpace(sourcePath))
                return Failure("native replay-plan sidecar was not reported");

            var source = Path.GetFullPath(sourcePath);
            if (!File.Exists(source))
                return Failure("native replay-plan sidecar was not found: " + source);

            var directory = Path.GetFullPath(artifactDirectory);
            Directory.CreateDirectory(directory);
            var planPath = Path.Combine(directory, PlanFileName);
            File.Copy(source, planPath, overwrite: true);
            var plan = ReadPlan(planPath);
            var atlas = UvReplayAtlasRasterizer.Render(plan);
            var atlasPath = Path.Combine(directory, AtlasFileName);
            UvReplayAtlasPng.Write(atlasPath, atlas);
            return new ResearchUvReplayArtifact(true, planPath, atlasPath, "");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException)
        {
            return Failure(ex.Message);
        }
    }

    private static ResearchUvReplayArtifact Failure(string error) => new(false, "", "", error);

    private static string ReadNativeSidecarPath(BridgeReply reply)
    {
        if (string.IsNullOrWhiteSpace(reply.Raw))
            return "";
        using var document = JsonDocument.Parse(reply.Raw);
        var root = document.RootElement;
        if (!root.TryGetProperty("metadata", out var metadata) || metadata.ValueKind != JsonValueKind.Object ||
            !metadata.TryGetProperty("research_uv_replay_plan_written", out var written) || written.ValueKind != JsonValueKind.True ||
            !metadata.TryGetProperty("research_uv_replay_plan_path", out var path) || path.ValueKind != JsonValueKind.String)
        {
            return "";
        }
        return path.GetString() ?? "";
    }

    private static UvReplayPlan ReadPlan(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        if (!root.TryGetProperty("schema", out var schema) ||
            !string.Equals(schema.GetString(), "meccha_uv_replay_plan_v1", StringComparison.Ordinal))
        {
            throw new InvalidDataException("native replay-plan sidecar has an unsupported schema");
        }
        if (!root.TryGetProperty("texture_size", out var textureSize) || !textureSize.TryGetInt32(out var size))
            throw new InvalidDataException("native replay-plan sidecar has no texture size");
        if (!root.TryGetProperty("strokes", out var strokes) || strokes.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("native replay-plan sidecar has no strokes array");

        var parsed = new List<UvReplayStroke>();
        foreach (var item in strokes.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("native replay-plan sidecar contains a non-object stroke");
            parsed.Add(new UvReplayStroke(
                RequiredDouble(item, "u"),
                RequiredDouble(item, "v"),
                RequiredDouble(item, "planner_radius_uv"),
                RequiredPass(item),
                RequiredText(item, "region"),
                RequiredText(item, "body_region")));
        }
        return new UvReplayPlan(size, parsed);
    }

    private static double RequiredDouble(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var value) || !value.TryGetDouble(out var parsed) || !double.IsFinite(parsed))
            throw new InvalidDataException("native replay-plan stroke has no valid " + name);
        return parsed;
    }

    private static string RequiredText(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new InvalidDataException("native replay-plan stroke has no valid " + name);
        }
        return value.GetString()!;
    }

    private static UvReplayPass RequiredPass(JsonElement item)
    {
        var value = RequiredText(item, "pass");
        return value switch
        {
            "fill" => UvReplayPass.Fill,
            "paint" => UvReplayPass.Paint,
            _ => throw new InvalidDataException("native replay-plan stroke has an unknown pass: " + value)
        };
    }
}
