namespace PdfEngine.Vector.Parsing;

public enum PdfTokenType
{
    None = 0,
    Integer,
    Real,
    Name,
    String,
    HexString,
    Keyword,
    BeginArray,       // [
    EndArray,         // ]
    BeginDictionary,  // <<
    EndDictionary,    // >>
    EndOfFile
}

public readonly record struct PdfToken(
    PdfTokenType Type,
    string TextValue,
    long IntValue = 0,
    double RealValue = 0.0,
    ReadOnlyMemory<byte> BinaryValue = default,
    long FileOffset = 0)
{
    public static readonly PdfToken Eof = new(PdfTokenType.EndOfFile, string.Empty);

    public override string ToString() =>
        Type switch
        {
            PdfTokenType.Integer => IntValue.ToString(),
            PdfTokenType.Real => RealValue.ToString("G"),
            PdfTokenType.Name => "/" + TextValue,
            PdfTokenType.String => $"({TextValue})",
            PdfTokenType.HexString => $"<{TextValue}>",
            PdfTokenType.Keyword => TextValue,
            PdfTokenType.BeginArray => "[",
            PdfTokenType.EndArray => "]",
            PdfTokenType.BeginDictionary => "<<",
            PdfTokenType.EndDictionary => ">>",
            PdfTokenType.EndOfFile => "EOF",
            _ => TextValue
        };
}
