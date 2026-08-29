using System;
using System.IO;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace PdfViewer.Tests;

public static class FixtureGenerator
{
    public static string GetEncryptedPdfPath()
    {
        var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
        while (dir != null)
        {
            string candidate = Path.Combine(dir.FullName, "tests", "PdfViewer.Tests", "Fixtures", "encrypted.pdf");
            if (File.Exists(candidate)) return candidate;
            candidate = Path.Combine(dir.FullName, "Fixtures", "encrypted.pdf");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Fixtures", "encrypted.pdf");
    }
}
