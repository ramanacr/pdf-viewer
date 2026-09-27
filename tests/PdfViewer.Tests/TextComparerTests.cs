using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using PdfViewer.Core.Comparison;
using Xunit;

namespace PdfViewer.Tests;

/// <summary>Word-level comparison: exact, minimal changes, indifferent to page breaks and typographic variants, and fast.</summary>
public class TextComparerTests
{
    private static List<CompareWord> Words(string text, int page = 1) =>
        text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select((w, i) => new CompareWord(w, page, i * 0.01, 0.1, 0.01, 0.01)).ToList();

    [Fact]
    public void IdenticalText_HasNoChanges() =>
        Assert.Empty(TextComparer.Compare(Words("the quick brown fox"), Words("the quick brown fox")));

    [Fact]
    public void Insertions_Deletions_AndReplacements_AreFoundExactly()
    {
        var changes = TextComparer.Compare(
            Words("The payment is due within 30 days of the invoice date"),
            Words("The full payment is due within 45 days of the date"));
        Assert.Equal(3, changes.Count);
        Assert.Equal((ChangeKind.Inserted, "", "full"), (changes[0].Kind, changes[0].OldText, changes[0].NewText));
        Assert.Equal((ChangeKind.Replaced, "30", "45"), (changes[1].Kind, changes[1].OldText, changes[1].NewText));
        Assert.Equal((ChangeKind.Deleted, "invoice", ""), (changes[2].Kind, changes[2].OldText, changes[2].NewText));
    }

    [Fact]
    public void TextMovedAcrossAPageBreak_StillMatches()
    {
        var old = Words("alpha beta gamma", 1).Concat(Words("delta epsilon", 2)).ToList();
        var @new = Words("alpha beta", 1).Concat(Words("gamma delta epsilon", 2)).ToList();
        Assert.Empty(TextComparer.Compare(old, @new));
    }

    [Fact]
    public void TypographicVariants_AreNotChanges()
    {
        Assert.Empty(TextComparer.Compare(Words("it’s a “quote” – done"), Words("it's a \"quote\" - done")));
        Assert.Single(TextComparer.Compare(Words("Hello World"), Words("hello world")));
        Assert.Empty(TextComparer.Compare(Words("Hello World"), Words("hello world"), ignoreCase: true));
    }

    [Fact]
    public void ChangesKeepTheirPositions()
    {
        var old = Words("keep this word", 3);
        var @new = Words("keep that word", 5);
        var change = Assert.Single(TextComparer.Compare(old, @new));
        Assert.Equal(3, change.OldPage);
        Assert.Equal(5, change.NewPage);
        Assert.Equal(0.01, change.Old[0].X, 6);
    }

    [Fact]
    public void ALargeDocument_WithScatteredEdits_IsFast()
    {
        var rng = new Random(11);
        var vocabulary = Enumerable.Range(0, 2000).Select(i => "w" + i).ToArray();
        var old = Enumerable.Range(0, 60_000).Select(i => new CompareWord(vocabulary[rng.Next(vocabulary.Length)], 1 + i / 500, 0, 0, 0, 0)).ToList();
        var @new = old.ToList();
        for (int k = 0; k < 40; k++) @new[rng.Next(@new.Count)] = new CompareWord("CHANGED" + k, 1, 0, 0, 0, 0);
        @new.Insert(30_000, new CompareWord("INSERTED", 60, 0, 0, 0, 0));
        var clock = Stopwatch.StartNew();
        var changes = TextComparer.Compare(old, @new);
        clock.Stop();
        Assert.InRange(changes.Count, 40, 41);
        Assert.True(clock.ElapsedMilliseconds < 2000, $"{clock.ElapsedMilliseconds} ms");
    }
}
