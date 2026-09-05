namespace PdfViewer.Core.Licensing;

public enum FeatureId
{
    Viewer,
    Search,
    Bookmarks,
    Printing,
    Annotations,
    Forms,
    Signatures,
    Redaction,
    PageOperations,
    MergeSplit,
    Ocr,
    BatchProcessing,
    EnterpriseDeployment,
    Sdk
}

public enum LicenseTier
{
    Community,
    Pro,
    Enterprise,
    DeveloperSdk
}

/// <summary>
/// Thrown when an operation requires a feature the active licence tier does not include.
/// </summary>
public class FeatureNotLicensedException : Exception
{
    public FeatureId Feature { get; }
    public LicenseTier CurrentTier { get; }

    public FeatureNotLicensedException(FeatureId feature, LicenseTier currentTier)
        : base($"The '{feature}' feature is not available on the {currentTier} licence tier.")
    {
        Feature = feature;
        CurrentTier = currentTier;
    }
}

public interface IFeatureGate
{
    LicenseTier CurrentTier { get; }
    bool IsFeatureEnabled(FeatureId feature);
}

public static class FeatureGateExtensions
{
    /// <summary>
    /// Throws <see cref="FeatureNotLicensedException"/> unless the feature is licensed.
    /// Enforcement points call this rather than testing <see cref="IFeatureGate.IsFeatureEnabled"/>
    /// and deciding for themselves, so the failure is uniform and cannot be forgotten.
    /// </summary>
    public static void EnsureFeatureEnabled(this IFeatureGate gate, FeatureId feature)
    {
        ArgumentNullException.ThrowIfNull(gate);

        if (!gate.IsFeatureEnabled(feature))
        {
            throw new FeatureNotLicensedException(feature, gate.CurrentTier);
        }
    }
}

public sealed class DefaultFeatureGate : IFeatureGate
{
    /// <summary>
    /// The active tier. Deliberately get-only and set through the constructor: a public
    /// setter let any code path silently grant itself Enterprise, which defeats the point
    /// of having a gate at all. The composition root decides the tier once.
    /// </summary>
    public LicenseTier CurrentTier { get; }

    public DefaultFeatureGate(LicenseTier tier = LicenseTier.Community)
    {
        CurrentTier = tier;
    }

    public bool IsFeatureEnabled(FeatureId feature)
    {
        return CurrentTier switch
        {
            LicenseTier.DeveloperSdk => true,
            LicenseTier.Enterprise => true,
            LicenseTier.Pro => feature != FeatureId.EnterpriseDeployment && feature != FeatureId.Sdk,
            _ => feature == FeatureId.Viewer ||
                 feature == FeatureId.Search ||
                 feature == FeatureId.Bookmarks ||
                 feature == FeatureId.Printing ||
                 feature == FeatureId.Annotations
        };
    }
}
