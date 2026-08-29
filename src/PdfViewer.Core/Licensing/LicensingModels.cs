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

public interface IFeatureGate
{
    LicenseTier CurrentTier { get; }
    bool IsFeatureEnabled(FeatureId feature);
}

public sealed class DefaultFeatureGate : IFeatureGate
{
    public LicenseTier CurrentTier { get; set; } = LicenseTier.Community;

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
