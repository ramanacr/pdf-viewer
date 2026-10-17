using System;
using System.Collections.Generic;
using System.Text;
using PdfEngine.Geometry;
using PdfEngine.Vector.Objects;

namespace PdfEngine.Vector.Text;

/// <summary>What a character of the text page is.</summary>
public enum PdfTextCharType
{
    /// <summary>A glyph the content shows, with its Unicode text.</summary>
    Normal = 0,
    /// <summary>A space or line break inferred from the layout.</summary>
    Generated = 1,
    /// <summary>A glyph whose font gives no Unicode: the character code stands in for it.</summary>
    NotUnicode = 2,
    /// <summary>A line-end hyphen (its Unicode value is 0x02).</summary>
    Hyphen = 3,
    /// <summary>Part of a glyph: one character of a ligature or of /ActualText.</summary>
    Piece = 4,
}

/// <summary>
/// A page's text the way a reader's text layer has it: characters in reading order, with the
/// spaces and line breaks the layout implies, a box per character and the page text as one
/// string. It follows PDFium's text page (CPDF_TextPage) rule for rule, so that search, selection
/// and copy behave as they did with PDFium: content order within a line, lines ordered by
/// position, objects drawn twice for a bold effect dropped, spaces generated from gaps between
/// glyphs and objects, "\r\n" between lines, line-end hyphens marked, ligatures (U+FB00–FB06)
/// split, /ActualText in place of the glyphs it covers, right-to-left runs reversed. Boxes are in
/// default user space (the page's own coordinates, not offset by its crop box, unrotated).
/// </summary>
public sealed class PdfTextPage
{
    private const double SizeEpsilon = 0.01;
    private const int InvalidCode = TextFontInfo.InvalidCode;

    internal enum TextOrientation { Unknown, Horizontal, Vertical }
    private enum MarkedContentState { Pass, Done, Delay }
    private enum GenerateCharacter { None, Space, LineBreak, Hyphen }

    internal sealed class CharInfo
    {
        public PdfTextCharType Type;
        public int CharCode = InvalidCode;
        public int Cid = InvalidCode;
        public char Unicode;
        public PdfPoint Origin;
        public TextRect CharBox;
        public PdfMatrix Matrix = PdfMatrix.Identity;
        public TextObject? TextObject;
        public TextRect LooseBox;

        public CharInfo Clone() => (CharInfo)MemberwiseClone();
    }

    private readonly struct Transformed
    {
        public Transformed(TextObject obj, PdfMatrix form) { Obj = obj; Form = form; }
        public TextObject Obj { get; }
        public PdfMatrix Form { get; }
    }

    private readonly List<CharInfo> _chars = new();
    private readonly StringBuilder _text = new();
    private readonly List<CharInfo> _tempChars = new();
    private readonly StringBuilder _tempText = new();
    private readonly List<Transformed> _lineObjects = new();
    private readonly List<(int Index, int Count)> _charIndices = new();
    private readonly PdfMatrix _display;
    private TextOrientation _textlineDir = TextOrientation.Unknown;
    private TextObject? _prevObj;
    private PdfMatrix _prevMatrix = PdfMatrix.Identity;
    private TextRect _curlineRect;
    private readonly double _pageWidth, _pageHeight;

    /// <summary>1-based page number.</summary>
    public int PageNumber { get; }

    /// <summary>The page's box in user space: the crop box within the media box (where normalized coordinates start).</summary>
    public PdfRect PageBox { get; }

    /// <summary>The page's /Rotate in degrees (0, 90, 180, 270).</summary>
    public int Rotation { get; }

    internal PdfTextPage(int pageNumber, PdfRect pageBox, int rotation, TextHolder content)
    {
        PageNumber = pageNumber;
        PageBox = pageBox;
        Rotation = ((rotation % 360) + 360) % 360 / 90 * 90;
        bool swap = Rotation is 90 or 270;
        _pageWidth = swap ? pageBox.Height : pageBox.Width;
        _pageHeight = swap ? pageBox.Width : pageBox.Height;
        _display = DisplayMatrix(pageBox, Rotation, _pageWidth, _pageHeight);
        ProcessObject(content);
        BuildCharIndices();
    }

    /// <summary>PDFium's page display matrix for (0, 0, width, height) and no extra rotation: user space to top-down device space.</summary>
    private static PdfMatrix DisplayMatrix(PdfRect box, int rotation, double width, double height)
    {
        if (width <= 0 || height <= 0) return PdfMatrix.Identity;
        double l = box.X, b = box.Y, r = box.X + box.Width, t = box.Y + box.Height;
        var page = rotation switch
        {
            90 => new PdfMatrix(0, -1, 1, 0, -b, r),
            180 => new PdfMatrix(-1, 0, 0, -1, r, t),
            270 => new PdfMatrix(0, 1, -1, 0, t, -l),
            _ => new PdfMatrix(1, 0, 0, 1, -l, -b),
        };
        int w = (int)width, h = (int)height;
        var device = new PdfMatrix(w / width, 0, 0, -h / height, 0, h);
        return page * device;
    }

    // ================================================================== public API

    /// <summary>Number of characters, generated ones included.</summary>
    public int CharCount => _chars.Count;

    /// <summary>The page text: every character that reads as text, in order (line breaks as "\r\n").</summary>
    public string Text => _text.ToString();

    public char GetUnicode(int index) => _chars[index].Unicode;
    public PdfTextCharType GetCharType(int index) => _chars[index].Type;
    public bool IsGenerated(int index) => _chars[index].Type == PdfTextCharType.Generated;
    public bool IsHyphen(int index) => _chars[index].Type == PdfTextCharType.Hyphen;

    /// <summary>The character's glyph box (its ink, from the font's outline) in user space.</summary>
    public TextRect GetCharBox(int index) => _chars[index].CharBox;

    /// <summary>The character's box from the font box (ascent to descent, so a line's characters line up), in user space.</summary>
    public TextRect GetLooseCharBox(int index) => _chars[index].LooseBox;

    /// <summary>Where the glyph is drawn from, in user space.</summary>
    public PdfPoint GetOrigin(int index) => _chars[index].Origin;

    /// <summary>The font size of the text the character belongs to; 0 for a generated character.</summary>
    public double GetFontSize(int index) => _chars[index].TextObject?.FontSize ?? 0;

    /// <summary>The marked-content identifier (MCID) of the text the character belongs to, or -1 (none, or a generated character).</summary>
    public int GetMarkedContentId(int index) => _chars[index].TextObject?.Mcid ?? -1;

    /// <summary>True when the character belongs to a text object (it is not a generated space or line break).</summary>
    public bool HasTextObject(int index) => _chars[index].TextObject != null;

    /// <summary>The text of characters [start, start + count), as PDFium's FPDFText_GetText returns it.</summary>
    public string GetText(int start, int count)
    {
        int n = _chars.Count;
        if (start < 0 || start >= n) return string.Empty;
        if (count == -1 || count > n - start) count = n - start;
        if (count <= 0) return string.Empty;
        int textStart = TextIndexFromCharIndex(start);
        while (textStart < 0)
        {
            start++;
            if (start >= n) return string.Empty;
            textStart = TextIndexFromCharIndex(start);
        }
        count = Math.Min(count, n - start);
        int last = start + count - 1;
        int textLast = TextIndexFromCharIndex(last);
        while (textLast < 0)
        {
            last--;
            if (last < start) return string.Empty;
            textLast = TextIndexFromCharIndex(last);
        }
        if (textLast < textStart) return string.Empty;
        return _text.ToString(textStart, textLast - textStart + 1);
    }

    /// <summary>The whole page text (all characters).</summary>
    public string GetAllText() => GetText(0, _chars.Count);

    public int CharIndexFromTextIndex(int textIndex)
    {
        int count = 0;
        foreach (var (index, c) in _charIndices)
        {
            count += c;
            if (count > textIndex) return textIndex - count + c + index;
        }
        return -1;
    }

    public int TextIndexFromCharIndex(int charIndex)
    {
        int count = 0;
        foreach (var (index, c) in _charIndices)
        {
            int textIndex = charIndex - index;
            if (textIndex < c) return textIndex >= 0 ? textIndex + count : -1;
            count += c;
        }
        return -1;
    }

    /// <summary>
    /// Every match of <paramref name="query"/> on the page as (first character, character count),
    /// with PDFium's search rules (FPDFText_FindStart/FindNext): words of the query match across
    /// any run of spaces and line breaks, CJK characters match one by one, "match whole word"
    /// rejects letters and digits next to the match.
    /// </summary>
    public IReadOnlyList<(int Start, int Count)> Find(string query, bool matchCase = false, bool matchWholeWord = false, bool consecutive = false) =>
        PdfTextFind.FindAll(this, query, matchCase, matchWholeWord, consecutive);

    // ================================================================== construction

    private void ProcessObject(TextHolder page)
    {
        if (page.Objects.Count == 0) return;
        _textlineDir = FindTextlineFlowOrientation(page);
        for (int i = 0; i < page.Objects.Count; i++)
        {
            if (page.Objects[i] is TextObject t) ProcessTextObject(t, PdfMatrix.Identity, page, i);
            else if (page.Objects[i] is FormObject f) ProcessFormObject(f, PdfMatrix.Identity);
        }
        foreach (var obj in _lineObjects) ProcessTextObject(obj);
        _lineObjects.Clear();
        CloseTempLine();
    }

    private void ProcessFormObject(FormObject form, PdfMatrix formMatrix)
    {
        var current = form.FormMatrix * formMatrix;
        var holder = form.Holder;
        for (int i = 0; i < holder.Objects.Count; i++)
        {
            if (holder.Objects[i] is TextObject t) ProcessTextObject(t, current, holder, i);
            else if (holder.Objects[i] is FormObject f) ProcessFormObject(f, current);
        }
    }

    private TextOrientation FindTextlineFlowOrientation(TextHolder page)
    {
        int width = (int)_pageWidth, height = (int)_pageHeight;
        if (width <= 0 || height <= 0) return TextOrientation.Unknown;
        var hMask = new bool[width];
        var vMask = new bool[height];
        double lineHeight = 0;
        int startH = width, endH = 0, startV = height, endV = 0;
        foreach (var o in page.Objects)
        {
            if (o is not TextObject t) continue;
            var r = t.Rect;
            int minH = (int)Math.Clamp(r.Left, 0, width), maxH = (int)Math.Clamp(r.Right, 0, width);
            int minV = (int)Math.Clamp(r.Bottom, 0, height), maxV = (int)Math.Clamp(r.Top, 0, height);
            if (minH >= maxH || minV >= maxV) continue;
            for (int i = minH; i < maxH; i++) hMask[i] = true;
            for (int i = minV; i < maxV; i++) vMask[i] = true;
            startH = Math.Min(startH, minH); endH = Math.Max(endH, maxH);
            startV = Math.Min(startV, minV); endV = Math.Max(endV, maxV);
            if (lineHeight <= 0) lineHeight = r.Height;
        }
        int doubleLine = (int)(2 * lineHeight);
        if (endV - startV < doubleLine) return TextOrientation.Horizontal;
        if (endH - startH < doubleLine) return TextOrientation.Vertical;
        double sumH = Filled(hMask, startH, endH);
        if (sumH > 0.8) return TextOrientation.Horizontal;
        double sumV = Filled(vMask, startV, endV);
        if (sumH > sumV) return TextOrientation.Horizontal;
        if (sumH < sumV) return TextOrientation.Vertical;
        return TextOrientation.Unknown;
    }

    private static double Filled(bool[] mask, int start, int end)
    {
        if (start >= end) return 0;
        int n = 0;
        for (int i = start; i < end; i++) if (mask[i]) n++;
        return (double)n / (end - start);
    }

    // ------------------------------------------------------------------ line buffering

    private void ProcessTextObject(TextObject obj, PdfMatrix formMatrix, TextHolder holder, int index)
    {
        if (Math.Abs(obj.Rect.Width) < SizeEpsilon) return;
        var newObj = new Transformed(obj, formMatrix);
        if (_lineObjects.Count == 0)
        {
            _lineObjects.Add(newObj);
            return;
        }
        if (IsSameAsPreTextObject(obj, holder, index)) return;

        var prev = _lineObjects[^1];
        int nItem = prev.Obj.CountItems;
        if (nItem == 0) return;
        var prevItem = prev.Obj.GetItemInfo(nItem - 1);
        double prevWidth = CharWidth(prevItem, prev.Obj) * prev.Obj.FontSize / 1000;
        prevWidth = (prev.Obj.Matrix * prev.Form).TransformDistance(Math.Abs(prevWidth));
        var item = obj.GetItemInfo(0);
        double thisWidth = Math.Abs(CharWidth(item, obj) * obj.FontSize / 1000);
        thisWidth = (obj.Matrix * formMatrix).TransformDistance(thisWidth);
        double threshold = Math.Max(prevWidth, thisWidth) / 4;
        var prevPos = TextMatrixMath.TransformF(_display, TextMatrixMath.TransformF(prev.Form, prev.Obj.Pos));
        var thisPos = TextMatrixMath.TransformF(_display, TextMatrixMath.TransformF(formMatrix, obj.Pos));
        if (Math.Abs(thisPos.Y - prevPos.Y) > threshold * 2)
        {
            foreach (var o in _lineObjects) ProcessTextObject(o);
            _lineObjects.Clear();
            _lineObjects.Add(newObj);
            return;
        }
        for (int i = _lineObjects.Count; i > 0; i--)
        {
            var p = _lineObjects[i - 1];
            var pos = TextMatrixMath.TransformF(_display, TextMatrixMath.TransformF(p.Form, p.Obj.Pos));
            if (thisPos.X >= pos.X)
            {
                _lineObjects.Insert(i, newObj);
                return;
            }
        }
        _lineObjects.Insert(0, newObj);
    }

    private static double CharWidth(TextItem item, TextObject obj) => obj.Font.GetCharWidth(item.Code, item.Cid);

    private bool IsSameAsPreTextObject(TextObject obj, TextHolder holder, int index)
    {
        int compared = 0;
        for (int k = index - 1; compared < 5 && k >= 0; k--)
        {
            if (holder.Objects[k] is not TextObject other || ReferenceEquals(other, obj)) continue;
            if (IsSameTextObject(other, obj)) return true;
            compared++;
        }
        return false;
    }

    private bool IsSameTextObject(TextObject obj1, TextObject obj2)
    {
        var rcPre = obj2.Rect;
        var rcCur = obj1.Rect;
        if (rcPre.IsEmpty && rcCur.IsEmpty)
        {
            double dx = Math.Abs(rcPre.Left - rcCur.Left);
            int n = _chars.Count;
            if (n >= 2 && dx > _chars[n - 2].CharBox.Width) return false;
        }
        if (!rcPre.IsEmpty || !rcCur.IsEmpty)
        {
            rcPre.Intersect(rcCur);
            if (rcPre.IsEmpty) return false;
            if (Math.Abs(rcPre.Width - rcCur.Width) > rcCur.Width / 2) return false;
            if (obj2.FontSize != obj1.FontSize) return false;
        }
        int count = obj2.CountItems;
        if (count != obj1.CountItems) return false;
        if (count == 0) return true;
        TextItem itemPer = default;
        for (int i = 0; i < count; i++)
        {
            itemPer = obj2.GetItemInfo(i);
            var itemCur = obj1.GetItemInfo(i);
            if (itemCur.Code != itemPer.Code) return false;
        }
        double diffX = obj1.Pos.X - obj2.Pos.X, diffY = obj1.Pos.Y - obj2.Pos.Y;
        double fontSize = obj2.FontSize;
        double charSize = obj2.Font.GetCharWidth(itemPer.Code, itemPer.Cid);
        double maxPreSize = Math.Max(Math.Max(rcPre.Height, rcPre.Width), fontSize);
        return Math.Abs(diffX) <= 0.9 * charSize * fontSize / 1000 && Math.Abs(diffY) <= maxPreSize / 8;
    }

    // ------------------------------------------------------------------ one object

    private void ProcessTextObject(Transformed t)
    {
        var obj = t.Obj;
        if (Math.Abs(obj.Rect.Width) < SizeEpsilon) return;
        var form = t.Form;
        var matrix = obj.Matrix * form;

        var mkc = PreMarkedContent(obj);
        if (mkc == MarkedContentState.Done)
        {
            _prevObj = obj;
            _prevMatrix = form;
            return;
        }
        if (_prevObj != null)
        {
            var type = ProcessInsertObject(obj, form);
            if (type == GenerateCharacter.LineBreak) _curlineRect = obj.Rect;
            else _curlineRect.Union(obj.Rect);
            if (!ProcessGenerateCharacter(type, obj, form)) return;
        }
        else
        {
            _curlineRect = obj.Rect;
        }

        if (mkc == MarkedContentState.Delay)
        {
            ProcessMarkedContent(t);
            _prevObj = obj;
            _prevMatrix = form;
            return;
        }
        _prevObj = obj;
        _prevMatrix = form;

        bool rtl = IsRightToLeft(obj);
        bool mirrored = rtl && (matrix.A * matrix.D - matrix.B * matrix.C) < 0;
        int bufStart = _tempText.Length, listStart = _tempChars.Count;
        ProcessTextObjectItems(obj, form, matrix);
        if (mirrored) SwapTempTextBuf(listStart, bufStart);
    }

    private bool ProcessGenerateCharacter(GenerateCharacter type, TextObject obj, PdfMatrix form)
    {
        switch (type)
        {
            case GenerateCharacter.Space:
                if (GenerateCharInfo(' ') is { } space)
                {
                    if (!form.IsIdentity) space.Matrix = form;
                    _tempText.Append(' ');
                    _tempChars.Add(space);
                }
                return true;
            case GenerateCharacter.LineBreak:
                CloseTempLine();
                if (_text.Length > 0)
                {
                    AppendGeneratedCharacter('\r', form);
                    AppendGeneratedCharacter('\n', form);
                }
                return true;
            case GenerateCharacter.Hyphen:
            {
                if (obj.CountChars == 1)
                {
                    var item = obj.GetCharInfo(0);
                    string s = obj.Font.UnicodeFromCharCode(item.Code);
                    char cur = s.Length > 0 ? s[0] : (char)item.Code;
                    if (cur is '-' or '­') return false;
                }
                while (_tempText.Length > 0 && _tempText[^1] == ' ' && _tempChars.Count > 0)
                {
                    _tempText.Length--;
                    _tempChars.RemoveAt(_tempChars.Count - 1);
                }
                if (_tempChars.Count == 0 || _tempText.Length == 0) return true;
                var last = _tempChars[^1];
                _tempText.Length--;
                last.Unicode = '\u0002';
                last.Type = PdfTextCharType.Hyphen;
                _tempText.Append('￾');
                return true;
            }
            default:
                return true;
        }
    }

    private CharInfo? PrevCharInfo() => _tempChars.Count > 0 ? _tempChars[^1] : _chars.Count > 0 ? _chars[^1] : null;

    private CharInfo? GenerateCharInfo(char unicode)
    {
        var prev = PrevCharInfo();
        if (prev == null) return null;
        double preWidth = 0;
        if (prev.TextObject != null && prev.CharCode != InvalidCode)
            preWidth = prev.TextObject.Font.GetCharWidth(prev.CharCode, prev.Cid);
        double fontSize = prev.TextObject != null ? prev.TextObject.FontSize : prev.CharBox.Height;
        if (fontSize == 0) fontSize = 1;
        var origin = new PdfPoint(prev.Origin.X + preWidth * (fontSize / 1000), prev.Origin.Y);
        var box = TextRect.Point(origin.X, origin.Y);
        return new CharInfo { Type = PdfTextCharType.Generated, Unicode = unicode, Origin = origin, CharBox = box, LooseBox = box };
    }

    private void AppendGeneratedCharacter(char unicode, PdfMatrix form)
    {
        var info = GenerateCharInfo(unicode);
        if (info == null) return;
        _text.Append(unicode);
        if (!form.IsIdentity) info.Matrix = form;
        _chars.Add(info);
    }

    /// <summary>
    /// The character's box from the font's ascent and descent (PDFium's loose box): the advance
    /// wide, from the descent to the ascent, so every character of a line has the same band. Vertical CID
    /// text gets a font-size-wide column cell. Characters without a text object (or of size 0)
    /// keep their glyph box.
    /// </summary>
    private static TextRect LooseBounds(CharInfo info)
    {
        var obj = info.TextObject;
        if (obj == null || obj.FontSize == 0 || info.Type == PdfTextCharType.Piece && info.CharCode == InvalidCode) return info.CharBox;
        double fs = obj.FontSize;
        var font = obj.Font;
        if (font.IsVertical)
        {
            var (vx, vy) = font.VertOrigin(info.Cid);
            double offX = (vx - 500) * fs / 1000, offY = vy * fs / 1000;
            double height = font.VertWidth(info.Cid) * fs / 1000;
            double left = info.Origin.X + offX, bottom = info.Origin.Y + offY;
            return new TextRect(left, bottom, left + fs, bottom + height);
        }
        int ascent = font.TypeAscent, descent = font.TypeDescent;
        if (ascent == descent) return info.CharBox;
        // In text space: the advance wide, descent to ascent tall; then where the text matrix puts it.
        double width = obj.GetCharWidth(info.CharCode, info.Cid);
        var o = info.Matrix.Inverse().Transform(info.Origin);
        var loose = new TextRect(o.X, descent * fs / 1000, o.X + width, ascent * fs / 1000).Transform(info.Matrix);
        // Ink past the advance or the ascent widens it. A font that is not embedded is measured
        // on a stand-in face whose ink is not the reader's: only its width is taken, so a line's
        // characters keep one band.
        if (font.Font.EmbeddedFontData != null || font.IsType3)
        {
            loose.Union(info.CharBox);
        }
        else
        {
            var ink = info.CharBox;
            ink.Normalize();
            loose.Normalize();
            if (ink.Width > 0 && info.Matrix.B == 0 && info.Matrix.C == 0)
            {
                loose.Left = Math.Min(loose.Left, ink.Left);
                loose.Right = Math.Max(loose.Right, ink.Right);
            }
        }
        return loose;
    }

    private void ProcessTextObjectItems(TextObject obj, PdfMatrix form, PdfMatrix matrix)
    {
        var font = obj.Font;
        double fontSizeH = obj.FontSizeH;
        // Character spacing is taken out of the gaps only for objects of three items or more (as
        // PDFium does; measured against it, the adjustment is not applied to shorter objects).
        double baseSpace = obj.CharSpace == 0 || obj.CountItems < 3 ? 0 : CalculateBaseSpace(obj, matrix) + CalculateBaseSpaceAdjustment(obj, matrix);
        double spacing = 0;
        int nItems = obj.CountItems;
        for (int i = 0; i < nItems; i++)
        {
            var item = obj.GetItemInfo(i);
            if (item.IsKerning)
            {
                bool hasText = _tempText.Length > 0 ? _tempText[^1] != ' ' : _text.Length > 0 && _text[^1] != ' ';
                if (hasText) spacing = -fontSizeH * item.X / 1000;
                continue;
            }
            spacing -= baseSpace;
            if (spacing != 0 && i > 0)
            {
                double threshold = SpaceThreshold(font, fontSizeH, item);
                if (threshold != 0 && spacing >= threshold)
                {
                    var origin = matrix.Transform(item.X, item.Y);
                    var box = TextRect.Point(origin.X, origin.Y);
                    _tempText.Append(' ');
                    _tempChars.Add(new CharInfo
                    {
                        Type = PdfTextCharType.Generated, Unicode = ' ', Origin = origin, CharBox = box, LooseBox = box, Matrix = form, TextObject = obj,
                    });
                }
            }
            spacing = 0;

            string unicode = font.UnicodeFromCharCode(item.Code);
            var type = PdfTextCharType.Normal;
            if (unicode.Length == 0 && item.Code != 0)
            {
                unicode = ((char)item.Code).ToString();
                type = PdfTextCharType.NotUnicode;
            }
            var glyph = font.GetCharBBox(item.Code, item.Cid);
            double f = obj.FontSize / 1000;
            var cb = new TextRect(glyph.Left * f + item.X, glyph.Bottom * f + item.Y, glyph.Right * f + item.X, glyph.Top * f + item.Y);
            if (Math.Abs(cb.Top - cb.Bottom) < SizeEpsilon) cb.Top = cb.Bottom + f;
            if (Math.Abs(cb.Right - cb.Left) < SizeEpsilon) cb.Right = cb.Left + obj.GetCharWidth(item.Code, item.Cid);
            var info = new CharInfo
            {
                Type = type, CharCode = item.Code, Cid = item.Cid, Origin = matrix.Transform(item.X, item.Y),
                CharBox = cb.Transform(matrix), Matrix = matrix, TextObject = obj,
            };
            info.LooseBox = LooseBounds(info);

            if (unicode.Length == 0)
            {
                info.Unicode = '\0';
                _tempChars.Add(info);
                _tempText.Append('￾');
                continue;
            }

            bool duplicate = false;
            int count = Math.Min(_tempChars.Count, 7);
            double dupThreshold = matrix.TransformXDistance(0.07 * obj.FontSize);
            for (int n = _tempChars.Count; n > _tempChars.Count - count; n--)
            {
                var c1 = _tempChars[n - 1];
                double dx = c1.Origin.X - info.Origin.X, dy = c1.Origin.Y - info.Origin.Y;
                if (c1.CharCode == info.CharCode && c1.TextObject != null && ReferenceEquals(c1.TextObject.Font, font) &&
                    Math.Abs(dx) < dupThreshold && Math.Abs(dy) < dupThreshold)
                {
                    duplicate = true;
                    break;
                }
            }
            if (!duplicate)
            {
                foreach (char c in unicode)
                {
                    var piece = info.Clone();
                    piece.Unicode = c;
                    _tempText.Append(c != '\0' ? c : '￾');
                    _tempChars.Add(piece);
                }
            }
            else if (i == 0 && _tempText.Length > 0 && _tempText[^1] == ' ')
            {
                _tempText.Length--;
                _tempChars.RemoveAt(_tempChars.Count - 1);
            }
        }
    }

    private static double SpaceThreshold(TextFontInfo font, double fontSizeH, TextItem item)
    {
        int space = font.SpaceCode;
        double threshold = 0;
        if (space != InvalidCode)
            threshold = fontSizeH * font.GetCharWidthF(space, font.CidOf(space)) / 1000;
        if (threshold > fontSizeH / 3) threshold = 0;
        else threshold /= 2;
        if (threshold == 0)
        {
            threshold = font.GetCharWidth(item.Code, item.Cid);
            threshold = NormalizeThreshold(threshold, 300, 500, 700);
            threshold = fontSizeH * threshold / 1000;
        }
        return threshold;
    }

    private static double NormalizeThreshold(double threshold, int t1, int t2, int t3)
    {
        if (threshold < t1) return threshold / 2;
        if (threshold < t2) return threshold / 4;
        if (threshold < t3) return threshold / 5;
        return threshold / 6;
    }

    private static double CalculateBaseSpace(TextObject obj, PdfMatrix matrix)
    {
        int n = obj.CountItems;
        if (obj.CharSpace == 0 || n < 3) return 0;
        bool allChar = true;
        double spacing = matrix.TransformDistance(obj.CharSpace);
        double baseSpace = spacing;
        double fontSizeH = obj.FontSizeH;
        for (int i = 0; i < n; i++)
        {
            var item = obj.GetItemInfo(i);
            if (!item.IsKerning) continue;
            double kerning = -fontSizeH * item.X / 1000;
            baseSpace = Math.Min(baseSpace, kerning + spacing);
            allChar = false;
        }
        if (baseSpace < 0 || (n == 3 && !allChar)) return 0;
        return baseSpace;
    }

    private static double CalculateBaseSpaceAdjustment(TextObject obj, PdfMatrix matrix)
    {
        double cs = obj.CharSpace;
        if (cs > 0.001) return -matrix.TransformDistance(cs);
        if (cs < -0.001) return matrix.TransformDistance(Math.Abs(cs));
        return 0;
    }

    private void SwapTempTextBuf(int listStart, int bufStart)
    {
        if (listStart < _tempChars.Count) _tempChars.Reverse(listStart, _tempChars.Count - listStart);
        if (bufStart < _tempText.Length)
        {
            var tail = _tempText.ToString(bufStart, _tempText.Length - bufStart).ToCharArray();
            Array.Reverse(tail);
            _tempText.Length = bufStart;
            _tempText.Append(tail);
        }
    }

    // ------------------------------------------------------------------ between objects

    private TextOrientation GetTextObjectWritingMode(TextObject? obj)
    {
        if (obj == null) return _textlineDir;
        int n = obj.CountChars;
        if (n <= 1) return _textlineDir;
        var first = obj.GetCharInfo(0);
        var last = obj.GetCharInfo(n - 1);
        var p0 = obj.Matrix.Transform(first.X, first.Y);
        var p1 = obj.Matrix.Transform(last.X, last.Y);
        double dX = Math.Abs(p1.X - p0.X), dY = Math.Abs(p1.Y - p0.Y);
        if (dX <= 0.0001 && dY <= 0.0001) return TextOrientation.Unknown;
        double len = Math.Sqrt(dX * dX + dY * dY);
        double vx = dX / len, vy = dY / len;
        bool xUnder = vx <= 0.0872;
        if (vy <= 0.0872) return xUnder ? _textlineDir : TextOrientation.Horizontal;
        return xUnder ? TextOrientation.Vertical : _textlineDir;
    }

    private static bool EndHorizontalLine(TextRect thisRect, TextRect prevRect)
    {
        if (thisRect.Height <= 4.5 || prevRect.Height <= 4.5) return false;
        double top = Math.Min(thisRect.Top, prevRect.Top);
        double bottom = Math.Max(thisRect.Bottom, prevRect.Bottom);
        return bottom >= top;
    }

    private static bool EndVerticalLine(TextRect thisRect, TextRect prevRect, TextRect curline, double thisFontSize, double prevFontSize)
    {
        if (thisRect.Width <= thisFontSize * 0.1 || prevRect.Width <= prevFontSize * 0.1) return false;
        double left = Math.Max(thisRect.Left, curline.Left);
        double right = Math.Min(thisRect.Right, curline.Right);
        return right <= left;
    }

    private static bool IsHyphenCode(char c) => c is '-' or '­';

    private bool IsHyphen(char curChar)
    {
        var buf = _tempText.Length > 0 ? _tempText : _text;
        if (buf.Length == 0) return false;
        int i = buf.Length - 1;
        while (i > 0 && buf[i] == ' ') i--;
        if (!IsHyphenCode(buf[i])) return false;
        if (i > 0 && char.IsLetter(buf[i - 1]) && char.IsLetterOrDigit(curChar)) return true;
        var prev = PrevCharInfo();
        return prev != null && prev.Type == PdfTextCharType.Piece && IsHyphenCode(prev.Unicode);
    }

    private void FindPreviousTextObject()
    {
        var prev = PrevCharInfo();
        if (prev?.TextObject != null) _prevObj = prev.TextObject;
    }

    private GenerateCharacter ProcessInsertObject(TextObject obj, PdfMatrix form)
    {
        FindPreviousTextObject();
        var prevObj = _prevObj!;
        var mode = GetTextObjectWritingMode(obj);
        if (mode == TextOrientation.Unknown) mode = GetTextObjectWritingMode(prevObj);

        int nItem = prevObj.CountItems;
        if (nItem == 0) return GenerateCharacter.None;
        var prevItem = prevObj.GetItemInfo(nItem - 1);
        var item = obj.GetItemInfo(0);
        var thisRect = obj.Rect;
        var prevRect = prevObj.Rect;
        string w = obj.Font.UnicodeFromCharCode(item.Code);
        char curChar = w.Length > 0 ? w[0] : (char)item.Code;

        if (mode == TextOrientation.Horizontal)
        {
            if (EndHorizontalLine(thisRect, prevRect))
                return IsHyphen(curChar) ? GenerateCharacter.Hyphen : GenerateCharacter.LineBreak;
        }
        else if (mode == TextOrientation.Vertical)
        {
            if (EndVerticalLine(thisRect, prevRect, _curlineRect, obj.FontSize, prevObj.FontSize))
                return IsHyphen(curChar) ? GenerateCharacter.Hyphen : GenerateCharacter.LineBreak;
        }

        double lastPos = prevItem.X;
        double nLastWidth = prevObj.Font.GetCharWidth(prevItem.Code, prevItem.Cid);
        double lastWidth = Math.Abs(nLastWidth * prevObj.FontSize / 1000);
        double nThisWidth = obj.Font.GetCharWidth(item.Code, item.Cid);
        double thisWidth = Math.Abs(nThisWidth * obj.FontSize / 1000);
        double threshold = Math.Max(lastWidth, thisWidth) / 4;

        var prevMatrix = TextMatrixMath.MultiplyF(prevObj.Matrix, _prevMatrix);
        var prevReverse = TextMatrixMath.InverseF(prevMatrix);
        var pos = TextMatrixMath.TransformF(prevReverse, TextMatrixMath.TransformF(form, obj.Pos));
        if (lastWidth < thisWidth) threshold = prevReverse.TransformDistance(threshold);

        bool newline = false;
        if (mode == TextOrientation.Horizontal)
        {
            var rect = prevObj.Rect;
            double rectHeight = rect.Height;
            rect.Normalize();
            if ((rect.IsEmpty && rectHeight > 5) ||
                ((pos.Y > threshold * 2 || pos.Y < threshold * -3) && (Math.Abs(pos.Y) >= 1 || Math.Abs(pos.Y) > Math.Abs(pos.X))))
            {
                newline = true;
                if (nItem > 1)
                {
                    var tempItem = prevObj.GetItemInfo(0);
                    var m = prevObj.Matrix;
                    if (prevItem.X > tempItem.X && _display.A > 0.9 && _display.B < 0.1 && _display.C < 0.1 && _display.D < -0.9 && m.B < 0.1 && m.C < 0.1)
                    {
                        var re = new TextRect(0, prevObj.Rect.Bottom, 1000, prevObj.Rect.Top);
                        if (re.Contains(obj.Pos)) newline = false;
                        else if (new TextRect(0, obj.Rect.Bottom, 1000, obj.Rect.Top).Contains(prevObj.Pos)) newline = false;
                    }
                }
            }
        }
        if (newline)
            return IsHyphen(curChar) ? GenerateCharacter.Hyphen : GenerateCharacter.LineBreak;

        if (obj.CountChars == 1 && IsHyphenCode(curChar) && IsHyphen(curChar))
            return GenerateCharacter.Hyphen;
        if (curChar == ' ') return GenerateCharacter.None;

        string prevStr = prevObj.Font.UnicodeFromCharCode(prevItem.Code);
        char preChar = prevStr.Length > 0 ? prevStr[^1] : '\0';
        if (preChar == ' ') return GenerateCharacter.None;

        var matrix = obj.Matrix * form;
        double threshold2 = Math.Max(nLastWidth, nThisWidth);
        threshold2 = NormalizeThreshold(threshold2, 400, 700, 800);
        if (nLastWidth >= nThisWidth)
        {
            threshold2 *= Math.Abs(prevObj.FontSize);
        }
        else
        {
            threshold2 *= Math.Abs(obj.FontSize);
            threshold2 = matrix.TransformDistance(threshold2);
            threshold2 = prevReverse.TransformDistance(threshold2);
        }
        threshold2 /= 1000;
        if ((threshold2 < 1.4881 && threshold2 > 1.4879) || (threshold2 < 1.39001 && threshold2 > 1.38999))
            threshold2 *= 1.5;
        return GenerateSpace(pos, lastPos, thisWidth, lastWidth, threshold2) ? GenerateCharacter.Space : GenerateCharacter.None;
    }

    private static bool GenerateSpace(PdfPoint pos, double lastPos, double thisWidth, double lastWidth, double threshold)
    {
        if (Math.Abs(lastPos + lastWidth - pos.X) <= threshold) return false;
        double thresholdPos = threshold + lastWidth;
        double diff = pos.X - lastPos;
        if (Math.Abs(diff) > thresholdPos) return true;
        if (pos.X < 0 && -thresholdPos > diff) return true;
        return diff > thisWidth + lastWidth;
    }

    // ------------------------------------------------------------------ marked content (/ActualText)

    private MarkedContentState PreMarkedContent(TextObject obj)
    {
        var marks = obj.Marks;
        int n = marks.Length;
        if (n == 0) return MarkedContentState.Pass;
        string actual = string.Empty;
        bool exist = false;
        PdfDictionary? dict = null;
        for (int i = 0; i < n; i++)
        {
            dict = marks[i];
            if (dict == null) continue;
            if (dict["ActualText"] is PdfString s)
            {
                exist = true;
                actual = TextContentReader.TextString(s);
            }
        }
        if (!exist) return MarkedContentState.Pass;
        if (_prevObj != null)
        {
            var prevMarks = _prevObj.Marks;
            if (prevMarks.Length == n && ReferenceEquals(prevMarks[n - 1], dict)) return MarkedContentState.Done;
        }
        if (actual.Length == 0) return MarkedContentState.Pass;
        foreach (char c in actual)
            if ((c > 0x80 && c < 0xFFFD) || (c <= 0x80 && IsPrint(c))) return MarkedContentState.Delay;
        return MarkedContentState.Done;
    }

    private static bool IsPrint(char c) => c >= 0x20 && c < 0x7F;

    private void ProcessMarkedContent(Transformed t)
    {
        var obj = t.Obj;
        string actual = string.Empty;
        foreach (var m in obj.Marks)
            if (m != null) actual = m["ActualText"] is PdfString s ? TextContentReader.TextString(s) : string.Empty;
        if (actual.Length == 0) return;

        var matrix = obj.Matrix * t.Form;
        var rect = obj.Rect;
        bool rtl = IsRightToLeft(obj);
        double step = rect.Width / actual.Length;
        var box = rtl ? new TextRect(rect.Right - step, rect.Bottom, rect.Right, rect.Top) : new TextRect(rect.Left, rect.Bottom, rect.Left + step, rect.Top);
        if (rtl) step = -step;
        for (int k = 0; k < actual.Length; k++)
        {
            char c = actual[k];
            if (c <= 0x80 && !IsPrint(c)) c = ' ';
            if (c >= 0xFFFD) continue;
            var b = box;
            b.Translate(k * step, 0);
            var info = new CharInfo
            {
                Type = PdfTextCharType.Piece, Unicode = c, Origin = obj.Pos, CharBox = b, LooseBox = b, Matrix = matrix, TextObject = obj,
            };
            _tempText.Append(c);
            _tempChars.Add(info);
        }
    }

    // ------------------------------------------------------------------ lines and directions

    private static bool IsRightToLeft(TextObject obj)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < obj.CountItems; i++)
        {
            int code = obj.Codes[i];
            if (code == InvalidCode) continue;
            string u = obj.Font.UnicodeFromCharCode(code);
            char c = u.Length > 0 ? u[0] : (char)code;
            if (c != 0) sb.Append(c);
        }
        return Bidi.Segments(sb.ToString(), out _) == Bidi.Direction.Right;
    }

    private void CloseTempLine()
    {
        if (_tempChars.Count == 0) return;
        bool prevSpace = false;
        for (int i = 0; i < _tempText.Length; i++)
        {
            if (_tempText[i] != ' ') { prevSpace = false; continue; }
            if (prevSpace && i < _tempChars.Count)
            {
                _tempText.Remove(i, 1);
                _tempChars.RemoveAt(i);
                i--;
            }
            prevSpace = true;
        }
        string str = _tempText.ToString();
        var overall = Bidi.Segments(str, out var segments);
        var current = overall;
        int limit = Math.Min(str.Length, _tempChars.Count);
        foreach (var seg in segments)
        {
            if (seg.Direction == Bidi.Direction.Right || (seg.Direction == Bidi.Direction.Neutral && current == Bidi.Direction.Right))
            {
                current = Bidi.Direction.Right;
                for (int m = seg.Start + seg.Count; m > seg.Start; m--)
                    if (m - 1 < limit) AddCharInfoByRLDirection(str[m - 1], _tempChars[m - 1]);
            }
            else
            {
                if (seg.Direction != Bidi.Direction.LeftWeak) current = Bidi.Direction.Left;
                for (int m = seg.Start; m < seg.Start + seg.Count; m++)
                    if (m < limit) AddCharInfoByLRDirection(str[m], _tempChars[m]);
            }
        }
        _tempChars.Clear();
        _tempText.Clear();
    }

    private static bool IsControlChar(CharInfo info) =>
        info.Unicode is '\u0002' or '\u0003' or '\u0093' or '\u0094' or '\u0096' or '\u0097' or '\u0098' or '￾' &&
        info.Type != PdfTextCharType.Hyphen;

    private static bool IsNormalCharacter(CharInfo info) =>
        (info.Unicode != 0 && !IsControlChar(info)) || (info.Unicode == 0 && info.CharCode != 0);

    private static readonly Dictionary<char, string> Ligatures = new()
    {
        ['ﬀ'] = "ff", ['ﬁ'] = "fi", ['ﬂ'] = "fl", ['ﬃ'] = "ffi", ['ﬄ'] = "ffl", ['ﬅ'] = "ſt", ['ﬆ'] = "st",
    };

    private void AddCharInfoByLRDirection(char c, CharInfo info)
    {
        if (!IsNormalCharacter(info))
        {
            _chars.Add(info);
            return;
        }
        if (Ligatures.TryGetValue(c, out var parts))
        {
            foreach (char p in parts)
            {
                var piece = info.Clone();
                piece.Unicode = p;
                piece.Type = PdfTextCharType.Piece;
                _text.Append(p);
                _chars.Add(piece);
            }
            return;
        }
        _text.Append(c);
        _chars.Add(info);
    }

    private void AddCharInfoByRLDirection(char c, CharInfo info)
    {
        if (!IsNormalCharacter(info))
        {
            _chars.Add(info);
            return;
        }
        c = Bidi.Mirror(c);
        string normalized = Bidi.Normalize(c);
        if (normalized.Length == 0)
        {
            var copy = info.Clone();
            copy.Unicode = c;
            _text.Append(c);
            _chars.Add(copy);
            return;
        }
        foreach (char p in normalized)
        {
            var piece = info.Clone();
            piece.Unicode = p;
            piece.Type = PdfTextCharType.Piece;
            _text.Append(p);
            _chars.Add(piece);
        }
    }

    private void BuildCharIndices()
    {
        int n = _chars.Count;
        if (n > 0) _charIndices.Add((0, 0));
        bool skipped = false;
        for (int i = 0; i < n; i++)
        {
            var info = _chars[i];
            if (info.Type == PdfTextCharType.Generated || IsNormalCharacter(info))
            {
                var (idx, c) = _charIndices[^1];
                _charIndices[^1] = (idx, c + 1);
                skipped = true;
            }
            else if (skipped)
            {
                _charIndices.Add((i + 1, 0));
                skipped = false;
            }
            else
            {
                var (_, c) = _charIndices[^1];
                _charIndices[^1] = (i + 1, c);
            }
        }
    }
}
