namespace UvexAdv.Nina.Plugin;

/// <summary>
/// Keeps N.I.N.A. as the owner of image naming while requiring enough native
/// tokens to recover observations by target and frame role without parsing a
/// plugin-private sidecar first.
/// </summary>
internal static class NinaImageFilePatternPolicy
{
    internal const string TargetToken = "$$TARGETNAME$$";
    internal const string ImageTypeToken = "$$IMAGETYPE$$";

    internal const string RecommendedPattern =
        "$$DATEMINUS12$$\\$$TARGETNAME$$\\$$IMAGETYPE$$\\" +
        "$$DATETIME$$_$$TARGETNAME$$_$$EXPOSURETIME$$s_G$$GAIN$$_O$$OFFSET$$_$$FRAMENR$$";

    internal static NinaImageFilePatternAssessment Assess(string? filePattern)
    {
        var pattern = filePattern ?? string.Empty;
        var issues = new List<string>();
        var recommendations = new List<string>();
        if (!pattern.Contains(TargetToken, StringComparison.Ordinal))
        {
            issues.Add($"N.I.N.A. 文件模板缺少 {TargetToken}；真实观测无法按目标归档。");
        }
        if (!pattern.Contains(ImageTypeToken, StringComparison.Ordinal))
        {
            recommendations.Add($"建议加入 {ImageTypeToken}，让探针帧与科学帧按官方图像类型分目录。");
        }

        var recommended = string.Equals(pattern, RecommendedPattern, StringComparison.Ordinal);
        var status = issues.Count > 0
            ? $"阻断真实观测：{string.Join(" ", issues)}"
            : recommendations.Count > 0
                ? $"可以运行，但布局建议更新：{string.Join(" ", recommendations)}"
            : recommended
                ? "已使用 OpenAstroSpec 推荐模板：按夜晚、目标和图像类型分目录。"
                : "模板具备目标与图像类型令牌，可以运行；目录布局不是推荐值。";
        return new NinaImageFilePatternAssessment(
            pattern,
            issues.AsReadOnly(),
            recommendations.AsReadOnly(),
            recommended,
            status);
    }
}

internal sealed record NinaImageFilePatternAssessment(
    string CurrentPattern,
    IReadOnlyList<string> BlockingIssues,
    IReadOnlyList<string> Recommendations,
    bool UsesRecommendedPattern,
    string Status)
{
    internal bool IsCompliant => BlockingIssues.Count == 0;
}
