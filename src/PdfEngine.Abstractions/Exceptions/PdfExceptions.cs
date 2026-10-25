namespace PdfEngine.Exceptions;

/// <summary>
/// Base class for all PDF engine exceptions.
/// </summary>
public class PdfException : Exception
{
    /// <summary>Creates the exception with a message.</summary>
    public PdfException(string message) : base(message) { }
    /// <summary>Creates the exception with a message and the exception that caused it.</summary>
    public PdfException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// Thrown when a PDF document cannot be opened or parsed.
/// </summary>
public class PdfOpenException : PdfException
{
    /// <summary>The path of the document that failed to open, when known.</summary>
    public string? FilePath { get; }
    /// <summary>Creates the exception with a message and the document path.</summary>
    public PdfOpenException(string message, string? filePath = null) : base(message)
    {
        FilePath = filePath;
    }
    /// <summary>Creates the exception with a message, its cause and the document path.</summary>
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
    /// <summary>Creates the exception with a message and the document path.</summary>
    public PdfPasswordRequiredException(string message = "Password required to open encrypted PDF document.", string? filePath = null)
        : base(message, filePath) { }
}

/// <summary>
/// Thrown when a PDF file structure is corrupted or unreadable.
/// </summary>
public class PdfCorruptDocumentException : PdfOpenException
{
    /// <summary>Creates the exception with a message and the document path.</summary>
    public PdfCorruptDocumentException(string message = "PDF file header or body is corrupted.", string? filePath = null)
        : base(message, filePath) { }
    /// <summary>Creates the exception with a message, its cause and the document path.</summary>
    public PdfCorruptDocumentException(string message, Exception innerException, string? filePath = null)
        : base(message, innerException, filePath) { }
}

/// <summary>
/// Thrown when a document save, export, or write operation fails.
/// </summary>
public class PdfSaveException : PdfException
{
    /// <summary>The path that the save or export wrote to, when known.</summary>
    public string? TargetPath { get; }
    /// <summary>Creates the exception with a message and the target path.</summary>
    public PdfSaveException(string message, string? targetPath = null) : base(message)
    {
        TargetPath = targetPath;
    }
    /// <summary>Creates the exception with a message, its cause and the target path.</summary>
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
    /// <summary>The name of the policy that the operation violated.</summary>
    public string PolicyName { get; }
    /// <summary>Creates the exception with the policy name and a message.</summary>
    public PdfSecurityPolicyException(string policyName, string message) : base(message)
    {
        PolicyName = policyName;
    }
}
