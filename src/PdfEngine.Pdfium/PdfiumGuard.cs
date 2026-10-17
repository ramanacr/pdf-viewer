namespace PdfEngine.Pdfium;

/// <summary>
/// The one gate every use of PDFium passes: initialising the library, constructing a
/// <see cref="PdfiumEngine"/>, and loading or creating a document (no other PDFium call can be
/// made without a document handle). It exists to prove the PDFium-free release gate: with PDFium
/// disabled, any attempt throws <see cref="PdfiumDisabledException"/> and is counted, so a test can
/// show that opening, viewing, annotating, form filling, signing and saving never reach PDFium.
/// </summary>
/// <remarks>
/// <see cref="Disabled"/> switches PDFium off for the whole process (also set by the environment
/// variable <c>PDF_PDFIUM_DISABLED=1</c>). <see cref="DisableInThisFlow"/> switches it off only for
/// the calling asynchronous flow, so tests that run in parallel with PDFium-oracle tests are not
/// affected by each other.
/// </remarks>
public static class PdfiumGuard
{
    private static volatile bool _disabled =
        string.Equals(Environment.GetEnvironmentVariable("PDF_PDFIUM_DISABLED"), "1", StringComparison.Ordinal);

    private static readonly AsyncLocal<Scope?> CurrentScope = new();

    /// <summary>PDFium is refused everywhere in the process.</summary>
    public static bool Disabled
    {
        get => _disabled;
        set => _disabled = value;
    }

    /// <summary>PDFium is refused for the caller: process-wide, or in a <see cref="DisableInThisFlow"/> scope.</summary>
    public static bool IsDisabled => _disabled || CurrentScope.Value is { IsActive: true };

    /// <summary>
    /// Refuses PDFium in this asynchronous flow (and the tasks it starts) until the scope is
    /// disposed. The scope records every attempt, including ones a caller caught.
    /// </summary>
    public static Scope DisableInThisFlow()
    {
        var scope = new Scope(CurrentScope.Value);
        CurrentScope.Value = scope;
        return scope;
    }

    /// <summary>Called before any PDFium use. Throws when PDFium is disabled for the caller.</summary>
    public static void Enter(string operation)
    {
        if (!IsDisabled) return;
        CurrentScope.Value?.Record(operation);
        throw new PdfiumDisabledException(operation);
    }

    public sealed class Scope : IDisposable
    {
        private readonly Scope? _outer;
        private readonly List<string> _attempts = new();

        internal Scope(Scope? outer) => _outer = outer;

        internal bool IsActive { get; private set; } = true;

        /// <summary>The PDFium operations attempted while the scope was active (none, if the gate holds).</summary>
        public IReadOnlyList<string> Attempts
        {
            get { lock (_attempts) return _attempts.ToList(); }
        }

        internal void Record(string operation)
        {
            lock (_attempts) _attempts.Add(operation);
            _outer?.Record(operation);
        }

        public void Dispose()
        {
            IsActive = false;
            if (ReferenceEquals(CurrentScope.Value, this)) CurrentScope.Value = _outer;
        }
    }
}

/// <summary>A PDFium call was attempted while PDFium is disabled (<see cref="PdfiumGuard"/>).</summary>
public sealed class PdfiumDisabledException : InvalidOperationException
{
    public PdfiumDisabledException(string operation)
        : base($"PDFium is disabled in this configuration; '{operation}' was refused.")
    {
        Operation = operation;
    }

    public string Operation { get; }
}
