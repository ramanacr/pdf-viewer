namespace PdfEngine.Exceptions;

/// <summary>
/// Base class for all PDF engine exceptions.
/// </summary>
public class PdfException : Exception
{
    public PdfException(string message) : base(message) { }
    public PdfException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// Thrown when a PDF document cannot be opened or parsed.
/// </summary>
public class PdfOpenException : PdfException
{
    public string? FilePath { get; }
    public PdfOpenException(string message, string? filePath = null) : base(message)
    {
        FilePath = filePath;
    }
    public PdfOpenException(string message, Exception innerException, string? filePath = null) : base(message, innerException)
    {
        FilePath = filePath;
    }
}

/// <summary>
/// Thrown when a document requires a password or provided password was invalid.
/// </summary>
public class PdfPasswordRequiredException : PdfOpenException
{
    public PdfPasswordRequiredException(string message = "Password required to open encrypted PDF document.", string? filePath = null)
        : base(message, filePath) { }
}

/// <summary>
/// Thrown when a PDF file structure is corrupted or unreadable.
/// </summary>
public class PdfCorruptDocumentException : PdfOpenException
{
    public PdfCorruptDocumentException(string message = "PDF file header or body is corrupted.", string? filePath = null)
        : base(message, filePath) { }
    public PdfCorruptDocumentException(string message, Exception innerException, string? filePath = null)
        : base(message, innerException, filePath) { }
}

/// <summary>
/// Thrown when a document save, export, or write operation fails.
/// </summary>
public class PdfSaveException : PdfException
{
    public string? TargetPath { get; }
    public PdfSaveException(string message, string? targetPath = null) : base(message)
    {
        TargetPath = targetPath;
    }
    public PdfSaveException(string message, Exception innerException, string? targetPath = null) : base(message, innerException)
    {
        TargetPath = targetPath;
    }
}

/// <summary>
/// Thrown when an operation violates active PDF security policy (e.g. JavaScript, external actions, file limits).
/// </summary>
public class PdfSecurityPolicyException : PdfException
{
    public string PolicyName { get; }
    public PdfSecurityPolicyException(string policyName, string message) : base(message)
    {
        PolicyName = policyName;
    }
}
