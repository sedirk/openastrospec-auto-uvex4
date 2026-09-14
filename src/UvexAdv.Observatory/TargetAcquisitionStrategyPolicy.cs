namespace UvexAdv.Observatory;

public enum TargetAcquisitionBranch
{
    DirectStellarPosition,
    ShortExposureSep,
    CatalogWcsGeometry,
    CalibratedGhost,
    CalibratedBrightWings,
    BoundedNeighborWcs,
}

public sealed record TargetAcquisitionBranchDescriptor(
    TargetAcquisitionBranch Id, string Label, string Prerequisites);

public sealed record TargetAcquisitionStrategyDecision(
    TargetObservabilityClass RequestedClass,
    TargetObservabilityClass EffectiveClass,
    string Summary,
    IReadOnlyList<TargetAcquisitionBranchDescriptor> OrderedBranches,
    bool UsesCatalogPositionOnly,
    GateResult Gate);

public sealed record TargetAcquisitionIdentificationDecision(
    TargetIdentification Identification, TargetAcquisitionBranch Branch,
    bool IsFallback, string Reason);

/// <summary>
/// One frozen strategy for both production entry points. Branch order is a
/// conditional priority, not permission to erase a failed identity or renew
/// acquisition budgets. Catalogue metadata recommends the observable class;
/// only fresh, physically validated WCS/frame evidence can locate the target.
/// </summary>
public static class TargetAcquisitionStrategyPolicy
{
    public const string Version = "target-acquisition-priority-v1";

    public static TargetAcquisitionStrategyDecision Resolve(
        TargetObservabilityClass requestedClass, EquatorialTarget target,
        TargetCatalogMetadata? metadata)
    {
        var effective = requestedClass;
        var reason = "手动选择；只改变识别策略，不改变目录目标或运动预算。";
        var gate = GateResult.Pass("TARGET_STRATEGY_RESOLVED", reason);
        if (!Enum.IsDefined(requestedClass))
        {
            effective = TargetObservabilityClass.DirectStellar;
            gate = GateResult.Fail("TARGET_STRATEGY_INVALID", "目标识别策略无效；未授权采集或移动。");
            reason = gate.Message;
        }
        else if (requestedClass == TargetObservabilityClass.AutoFromPlanetarium)
        {
            var bound = metadata is not null && TargetCatalogClassifier.IsBoundTo(metadata, target);
            var classification = TargetCatalogClassifier.Classify(bound ? metadata : null);
            effective = classification.PreferredClass;
            reason = classification.Reason;
            if (classification.IsUnsupportedObjectType)
                gate = GateResult.Fail("TARGET_STRATEGY_UNSUPPORTED_OBJECT", reason);
            else if (!bound || !classification.HasKnownObjectType)
            {
                // No inference that a missing centroid means "invisible".
                effective = TargetObservabilityClass.DirectStellar;
                reason = (metadata is not null && !bound ? "星图信息与当前目标不匹配，未沿用旧目标类型。" : "") +
                    reason + " 暂按恒星优先；仍需本轮 WCS 与星像验证。";
                gate = GateResult.Warn("TARGET_STRATEGY_METADATA_UNAVAILABLE", reason);
            }
            else gate = GateResult.Pass("TARGET_STRATEGY_RESOLVED", reason);
        }
        if (TargetCatalogClassifier.IsBoundTo(metadata, target) &&
            TargetCatalogClassifier.Classify(metadata) is { IsUnsupportedObjectType: true } unsupported)
        {
            reason = unsupported.Reason;
            gate = GateResult.Fail("TARGET_STRATEGY_UNSUPPORTED_OBJECT", reason);
        }
        var catalogOnly = effective is TargetObservabilityClass.FaintPointSource or
            TargetObservabilityClass.CompactExtended or TargetObservabilityClass.ExtendedNebula or
            TargetObservabilityClass.InvisibleInG3;
        var branches = catalogOnly
            ? new[] { Describe(TargetAcquisitionBranch.CatalogWcsGeometry), Describe(TargetAcquisitionBranch.CalibratedGhost),
                Describe(TargetAcquisitionBranch.CalibratedBrightWings), Describe(TargetAcquisitionBranch.BoundedNeighborWcs) }
            : new[] { Describe(TargetAcquisitionBranch.DirectStellarPosition), Describe(TargetAcquisitionBranch.ShortExposureSep),
                Describe(TargetAcquisitionBranch.CatalogWcsGeometry), Describe(TargetAcquisitionBranch.CalibratedGhost),
                Describe(TargetAcquisitionBranch.CalibratedBrightWings), Describe(TargetAcquisitionBranch.BoundedNeighborWcs) };
        var summary = (catalogOnly
            ? "目录位置优先：正式 WCS 定位 → 无解时有界邻场解算；不要求目标呈圆形或测到目标峰。"
            : "恒星优先：本帧星像 → 过曝时 SEP 短帧 → 星像缺失但正式 WCS 有效时用目录几何；无解时有界邻场。") +
            " " + reason + " 不接受歧义星像，也不绕过设备安全或预算限制。";
        return new(requestedClass, effective, summary, Array.AsReadOnly(branches), catalogOnly, gate);
    }

    public static TargetAcquisitionBranchDescriptor Describe(TargetAcquisitionBranch branch) => branch switch
    {
        TargetAcquisitionBranch.DirectStellarPosition => new(branch, "WCS + 本帧星像", "目录匹配唯一；不以最亮星替代目标。"),
        TargetAcquisitionBranch.ShortExposureSep => new(branch, "SEP 短曝光复核", "长帧过曝时必须位置复核；沿原最多三帧验证，失败不能改走目录几何绕过。"),
        TargetAcquisitionBranch.CatalogWcsGeometry => new(branch, "正式 WCS 目录几何", "目标在正式解算视场内：未过曝但星像不可用，或计划本身是目录目标；不虚构目标峰或精确入缝。"),
        TargetAcquisitionBranch.CalibratedGhost => new(branch, "已标定鬼影辅助", "条件分支：既有鬼影标定、外部身份确认同一目录目标和显式开关均有效；不取无关亮星。"),
        TargetAcquisitionBranch.CalibratedBrightWings => new(branch, "已标定亮星翼部", "条件分支：既有亮目标例外、独立身份确认同一目录目标及焦点证据有效；不取无关亮星。"),
        TargetAcquisitionBranch.BoundedNeighborWcs => new(branch, "有界邻场解算", "仅兼容的无解/视场缺失；继承原运动、次数、耗时和回程预算。"),
        _ => throw new ArgumentOutOfRangeException(nameof(branch)),
    };

    /// <summary>
    /// This is called only AFTER the production owner has validated the formal
    /// same-frame WCS, detector identity and mount binding. Missing local flux
    /// may fall back to that stronger geometry; ambiguity never chooses a star
    /// or applies a measured offset, and is retained explicitly in the record.
    /// </summary>
    public static TargetAcquisitionIdentificationDecision IdentifyFromVerifiedWcs(
        TargetAcquisitionStrategyDecision strategy, MonochromeFrame frame,
        IReadOnlyList<StarCandidate> candidates, PixelPoint prediction,
        double recognitionRadiusPixels)
    {
        var projected = TargetIdentification.FromCatalogWcs(prediction, frame.Width, frame.Height,
            "正式 WCS 保留目录目标身份；目录几何不声明实测目标峰或精确入缝。");
        if (strategy.Gate.Disposition != GateDisposition.Passed)
            return new(projected with { Gate = strategy.Gate }, TargetAcquisitionBranch.CatalogWcsGeometry, false, strategy.Gate.Message);
        if (projected.Gate.Disposition != GateDisposition.Passed || strategy.UsesCatalogPositionOnly)
            return new(projected, TargetAcquisitionBranch.CatalogWcsGeometry, false, projected.Gate.Message);
        if (G3CatalogTargetPositionPolicy.NeedsShortPositionCheck(frame, prediction, recognitionRadiusPixels))
            return new(projected, TargetAcquisitionBranch.ShortExposureSep, true,
                "长帧目录区域过曝；切换 SEP 短帧验证，不把饱和图斑当成已确认的位置。");
        var measured = SlitTargetIdentifier.Identify(frame, candidates, prediction, recognitionRadiusPixels);
        if (measured.Gate.Disposition == GateDisposition.Passed && measured.Target is not null)
            return new(measured with
            {
                Gate = GateResult.Pass("TARGET_CATALOG_WCS_REFINED",
                    $"正式 WCS 保留目标身份；本帧唯一星像修正像素位置 {measured.PredictionResidualPixels:F2}px。", measured.Gate.Metrics),
                Authority = TargetIdentificationAuthority.CatalogWcsProjection,
                CatalogPositionRefinedFromSameFrame = true,
            }, TargetAcquisitionBranch.DirectStellarPosition, false, measured.Gate.Message);
        if (measured.Gate.Code is "TARGET_NOT_FOUND" or "TARGET_AMBIGUOUS")
            return new(projected with { Gate = GateResult.Pass("TARGET_CATALOG_WCS_LOCAL_UNMEASURED",
                (measured.Gate.Code == "TARGET_AMBIGUOUS" ? "多个星像候选均不采用，不应用局部质心偏移；" : "本帧未测到目录目标峰；") +
                "切换至同帧正式 WCS 的目录几何，不改变目标类型。精调仍需新鲜导星/位置证据。") },
                TargetAcquisitionBranch.CatalogWcsGeometry, true, measured.Gate.Message);
        return new(measured, TargetAcquisitionBranch.DirectStellarPosition, false,
            $"{measured.Gate.Code}: {measured.Gate.Message} 身份歧义不以其他分支掩盖。");
    }

    public static bool MayTryBoundedNeighbor(GateResult gate) => gate.Code is
        "G3_PLATE_SOLVE_FAILED" or "G3_PLATE_SOLVE_LADDER_EXHAUSTED_STRUCTURED_FIELD" or
        "G3_PLATE_SOLVE_LADDER_EXHAUSTED_DECLARED_INVISIBLE_FIELD" or "G3_SOLVED_TARGET_OUTSIDE" or
        "G3_STAR_FIELD_SPARSE_VALID_EXPOSURE" or "TARGET_NOT_FOUND" or "TARGET_AMBIGUOUS" or
        "BRIGHT_TARGET_SATURATED_CORE_NOT_FOUND" or "BRIGHT_TARGET_WINGS_UNUSABLE" or "BRIGHT_TARGET_AMBIGUOUS";
}
