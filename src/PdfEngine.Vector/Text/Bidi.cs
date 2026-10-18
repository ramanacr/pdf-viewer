using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace PdfEngine.Vector.Text;

/// <summary>
/// The little of the Unicode bidirectional algorithm the text page uses (PDFium's CFX_BidiString):
/// a line is cut into runs of one direction class (strong left, strong right, numbers, neutral),
/// and when right-to-left runs are at least as many as left-to-right ones the run order is
/// reversed. Classes come from the script blocks; that is enough to tell Hebrew and Arabic from
/// everything else.
/// </summary>
internal static class Bidi
{
    public enum Direction { Neutral, Left, LeftWeak, Right }

    public readonly record struct Segment(Direction Direction, int Start, int Count);

    /// <summary>True when <see cref="Classify"/> can find the character right-to-left (Hebrew, Arabic and their presentation forms).</summary>
    public static bool CanBeRight(char c) => c is >= '֐' and <= 'ࣿ' or >= 'יִ' and <= '﷿' or >= 'ﹰ' and <= '﻾';

    public static Direction Classify(char c)
    {
        if (c is >= '0' and <= '9') return Direction.LeftWeak;
        if (c is >= '٠' and <= '٩' or '٫' or '٬' or >= '۰' and <= '۹') return Direction.LeftWeak;
        if (c is '²' or '³' or '¹' or >= '⁰' and <= '⁹' or >= '₀' and <= '₉' or >= '０' and <= '９')
            return Direction.LeftWeak;
        if (c is >= '֐' and <= 'ࣿ' or >= 'יִ' and <= '﷿' or >= 'ﹰ' and <= '﻾')
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(c);
            return cat is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.Format
                or UnicodeCategory.OtherPunctuation or UnicodeCategory.DashPunctuation or UnicodeCategory.OpenPunctuation or UnicodeCategory.ClosePunctuation
                ? Direction.Neutral : Direction.Right;
        }
        if (c is >= '\uD800' and <= '\uDFFF' or >= '' and <= '') return Direction.Left;
        if (c is '￾' or '￿') return Direction.Neutral;
        switch (CharUnicodeInfo.GetUnicodeCategory(c))
        {
            case UnicodeCategory.UppercaseLetter:
            case UnicodeCategory.LowercaseLetter:
            case UnicodeCategory.TitlecaseLetter:
            case UnicodeCategory.ModifierLetter:
            case UnicodeCategory.OtherLetter:
            case UnicodeCategory.SpacingCombiningMark:
            case UnicodeCategory.LetterNumber:
            case UnicodeCategory.OtherNotAssigned:
                return Direction.Left;
            default:
                return Direction.Neutral;
        }
    }

    /// <summary>Cuts <paramref name="text"/> into runs and returns the overall direction (runs reversed when it is right-to-left).</summary>
    public static Direction Segments(string text, out List<Segment> segments)
    {
        segments = new List<Segment>();
        if (text.Length == 0) return Direction.Left;
        var dir = Classify(text[0]);
        int start = 0;
        for (int i = 1; i <= text.Length; i++)
        {
            var d = i < text.Length ? Classify(text[i]) : (Direction)(-1);
            if (i == text.Length || d != dir)
            {
                segments.Add(new Segment(dir, start, i - start));
                start = i;
                if (i < text.Length) dir = d;
            }
        }
        int right = 0, left = 0;
        foreach (var s in segments)
        {
            if (s.Direction == Direction.Right) right++;
            else if (s.Direction == Direction.Left) left++;
        }
        if (right > 0 && right >= left)
        {
            segments.Reverse();
            return Direction.Right;
        }
        return Direction.Left;
    }

    public static char Mirror(char c) => c switch
    {
        '(' => ')', ')' => '(', '<' => '>', '>' => '<', '[' => ']', ']' => '[', '{' => '}', '}' => '{',
        '«' => '»', '»' => '«', '‹' => '›', '›' => '‹',
        '⁅' => '⁆', '⁆' => '⁅', '⁽' => '⁾', '⁾' => '⁽', '₍' => '₎', '₎' => '₍',
        '≤' => '≥', '≥' => '≤', '〈' => '〉', '〉' => '〈', '《' => '》', '》' => '《',
        '（' => '）', '）' => '（', '［' => '］', '］' => '［', '｛' => '｝', '｝' => '｛',
        _ => c,
    };

    /// <summary>The compatibility form of a presentation-form character (Arabic and Hebrew forms, ligatures); empty when it has none.</summary>
    public static string Normalize(char c)
    {
        if (c is < 'ﬀ' or > '﻿') return string.Empty;
        string s = c.ToString();
        try
        {
            string n = s.Normalize(NormalizationForm.FormKC);
            return n == s ? string.Empty : n;
        }
        catch (ArgumentException)
        {
            return string.Empty;
        }
    }
}
