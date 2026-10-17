using System;
using System.Collections.Generic;

namespace PdfEngine.Vector.Text;

/// <summary>
/// Text search over a <see cref="PdfTextPage"/> with PDFium's rules (CPDF_TextPageFind), so
/// that hits are the ones PDFium found: the query is cut into words at spaces (and characters
/// outside the Latin, Cyrillic, Arabic and general punctuation blocks, such as CJK, become words
/// of their own); words match with only spaces, line breaks or no-break spaces between them;
/// without "match case" both sides are lower-cased character by character.
/// </summary>
internal static class PdfTextFind
{
    private const char NoBreakSpace = ' ';

    public static IReadOnlyList<(int Start, int Count)> FindAll(PdfTextPage page, string query, bool matchCase, bool wholeWord, bool consecutive)
    {
        var results = new List<(int, int)>();
        if (string.IsNullOrEmpty(query)) return results;
        string text = Case(page.GetAllText(), matchCase);
        if (text.Length == 0) return results;
        var words = ExtractFindWhat(Case(query, matchCase));
        int? next = 0;
        int guard = 0;
        while (next.HasValue && guard++ < 1_000_000)
        {
            if (!FindNext(text, words, wholeWord, consecutive, ref next, out int resStart, out int resEnd)) break;
            int start = page.CharIndexFromTextIndex(resStart);
            int end = page.CharIndexFromTextIndex(resEnd);
            results.Add((start, end - start + 1));
        }
        return results;
    }

    private static string Case(string s, bool matchCase)
    {
        if (matchCase) return s;
        var chars = s.ToCharArray();
        for (int i = 0; i < chars.Length; i++) chars[i] = char.ToLowerInvariant(chars[i]);
        return new string(chars);
    }

    internal static bool IsIgnoreSpaceCharacter(char c) =>
        !(c < 255 || c is >= '؀' and <= 'ۿ' || c is >= 'ﹰ' and <= '﻿' || c is >= 'ﭐ' and <= '﷿' ||
          c is >= 'Ѐ' and <= 'ӿ' || c is >= 'Ԁ' and <= 'ԯ' || c is >= 'Ꙁ' and <= 'ꚟ' ||
          c is >= 'ⷠ' and <= 'ⷿ' || c == 'ℓ' || c is >= ' ' and <= '⁯');

    /// <summary>The <paramref name="index"/>-th space-separated token (runs of spaces after a separator are skipped); null past the end.</summary>
    private static string? ExtractSubString(string s, int index)
    {
        int p = 0;
        while (index-- > 0)
        {
            int sp = s.IndexOf(' ', p);
            if (sp < 0) return null;
            p = sp + 1;
            while (p < s.Length && s[p] == ' ') p++;
        }
        int end = s.IndexOf(' ', p);
        return end < 0 ? s[p..] : s[p..end];
    }

    internal static List<string> ExtractFindWhat(string findWhat)
    {
        var result = new List<string>();
        int i = 0;
        while (i < findWhat.Length && findWhat[i] == ' ') i++;
        if (i == findWhat.Length)
        {
            result.Add(findWhat);
            return result;
        }
        int index = 0;
        while (true)
        {
            string? word = ExtractSubString(findWhat, index);
            if (word == null) break;
            if (word.Length == 0)
            {
                result.Add(string.Empty);
                index++;
                continue;
            }
            int pos = 0;
            while (pos < word.Length)
            {
                char c = word[pos];
                if (IsIgnoreSpaceCharacter(c))
                {
                    if (pos > 0 && c == '’') { pos++; continue; }
                    if (pos > 0) result.Add(word[..pos]);
                    result.Add(c.ToString());
                    if (pos == word.Length - 1) { word = string.Empty; break; }
                    word = word[(pos + 1)..];
                    pos = 0;
                    continue;
                }
                pos++;
            }
            if (word.Length > 0) result.Add(word);
            index++;
        }
        return result;
    }

    private static bool IsSpace(char c) => c is '\n' or ' ' or '\r' or NoBreakSpace;

    private static bool FindNext(string text, List<string> words, bool wholeWord, bool consecutive, ref int? next, out int resStart, out int resEnd)
    {
        resStart = resEnd = 0;
        if (text.Length == 0 || !next.HasValue) return false;
        int len = text.Length;
        int startPos = next.Value;
        if (startPos >= len) return false;
        int count = words.Count;
        int resultPos = 0;
        bool spaceStart = false;
        int iterations = 0;
        for (int iWord = 0; iWord < count; iWord++)
        {
            if (++iterations > 10_000_000) return false;
            string word = words[iWord];
            if (word.Length == 0)
            {
                if (iWord == count - 1)
                {
                    if (startPos >= len) return false;
                    if (IsSpace(text[startPos]))
                    {
                        resultPos = startPos + 1;
                        break;
                    }
                    iWord = -1;
                }
                else if (iWord == 0)
                {
                    spaceStart = true;
                }
                continue;
            }
            if (startPos > len) return false;
            int found = text.IndexOf(word, startPos, StringComparison.Ordinal);
            if (found < 0) return false;
            resultPos = found;
            int endIndex = found + word.Length - 1;
            if (iWord == 0) resStart = found;
            bool match = true;
            if (iWord != 0 && !spaceStart)
            {
                int preEnd = startPos;
                char cur = word[0];
                string last = words[iWord - 1];
                char lastChar = last.Length > 0 ? last[^1] : '\0';
                if (startPos == found && !(IsIgnoreSpaceCharacter(lastChar) || IsIgnoreSpaceCharacter(cur)))
                    match = false;
                for (int d = preEnd; d < found; d++)
                {
                    if (!IsSpace(text[d]))
                    {
                        match = false;
                        break;
                    }
                }
            }
            else if (spaceStart)
            {
                if (found > 0)
                {
                    if (!IsSpace(text[found - 1]))
                    {
                        match = false;
                        resStart = found;
                    }
                    else
                    {
                        resStart = found - 1;
                    }
                }
            }
            if (wholeWord && match) match = IsMatchWholeWord(text, found, endIndex);
            if (match)
            {
                startPos = endIndex + 1;
            }
            else
            {
                iWord = -1;
                int idx = spaceStart ? 1 : 0;
                startPos = resStart + (idx < words.Count ? words[idx].Length : 0);
                if (startPos <= resStart) startPos = resStart + 1;
            }
        }
        resEnd = resultPos + words[^1].Length - 1;
        if (consecutive) next = resStart + 1;
        else next = resEnd + 1;
        return true;
    }

    private static bool IsDigit(char c) => c is >= '0' and <= '9';

    private static bool IsMatchWholeWord(string text, int startPos, int endPos)
    {
        if (startPos > endPos) return false;
        int charCount = endPos - startPos + 1;
        if (charCount == 1 && text[startPos] > 255) return true;
        char left = startPos >= 1 ? text[startPos - 1] : '\0';
        char right = startPos + charCount < text.Length ? text[startPos + charCount] : '\0';
        if ((left > 'A' && left < 'a') || (left > 'a' && left < 'z') || (left > 'ﬀ' && left < 'ﬆ') || IsDigit(left) ||
            (right > 'A' && right < 'a') || (right > 'a' && right < 'z') || (right > 'ﬀ' && right < 'ﬆ') || IsDigit(right))
            return false;
        if (!(('A' > left || left > 'Z') && ('a' > left || left > 'z') && ('A' > right || right > 'Z') && ('a' > right || right > 'z')))
            return false;
        if (IsDigit(left) && IsDigit(text[startPos])) return false;
        if (IsDigit(right) && IsDigit(text[endPos])) return false;
        return true;
    }
}
