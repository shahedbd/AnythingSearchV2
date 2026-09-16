using AnythingSearch.Services.Search.Memory;
using Xunit;

namespace AnythingSearch.Tests;

/// <summary>
/// The packed table stores every name folded to lowercase and reconstructs the original casing
/// from a bit per byte, so a round-trip failure here would show the user mangled file names.
/// </summary>
public class PackedStringTableTests
{
    private static PackedStringTable Build(params string[] values)
    {
        var builder = new PackedStringTable.Builder(values.Length, 64);
        foreach (var value in values) builder.Add(value);
        return builder.Build();
    }

    [Fact]
    public void Original_RestoresCasingExactly()
    {
        var names = new[]
        {
            "README.md", "MainForm.Designer.cs", "lowercase.txt", "UPPERCASE.TXT",
            "Mixed_Case-123.DLL", "", "a", "TECH_SPEC.md"
        };

        var table = Build(names);

        Assert.Equal(names.Length, table.Count);
        for (int i = 0; i < names.Length; i++)
            Assert.Equal(names[i], table.Original(i));
    }

    [Fact]
    public void Original_RestoresNonAsciiNamesVerbatim()
    {
        // Non-ASCII entries cannot be undone with a single case bit, so they are kept verbatim.
        var names = new[] { "Résumé.PDF", "文件.txt", "Ünicode-MIX.doc", "plain.txt" };

        var table = Build(names);

        for (int i = 0; i < names.Length; i++)
            Assert.Equal(names[i], table.Original(i));
    }

    [Fact]
    public void Folded_IsLowercaseAndSearchableWithFoldedTerm()
    {
        var table = Build("MainForm.Designer.cs");

        var folded = table.Folded(0).ToArray();
        Assert.Equal("mainform.designer.cs", System.Text.Encoding.UTF8.GetString(folded));

        // A term folded the same way matches regardless of how the user typed it.
        foreach (var typed in new[] { "DESIGNER", "designer", "DeSiGnEr" })
            Assert.True(table.Folded(0).IndexOf(PackedStringTable.Fold(typed)) >= 0);
    }

    [Fact]
    public void Entries_AreSeparatedSoAMatchCannotStraddleTwoOfThem()
    {
        var table = Build("abc", "def");

        // "cd" spans the end of entry 0 and the start of entry 1; the NUL between them must
        // stop the blob scan from reporting it as a hit.
        Assert.True(table.Blob.IndexOf(PackedStringTable.Fold("cd")) < 0);
        Assert.True(table.Blob.IndexOf(PackedStringTable.Fold("abc")) >= 0);
    }

    [Fact]
    public void IndexOfOffsetFrom_MapsEveryByteBackToItsEntry()
    {
        var names = new[] { "one", "twotwo", "three", "x", "averyverylongnamehere", "y" };
        var table = Build(names);

        for (int i = 0; i < names.Length; i++)
        {
            int start = table.StartOf(i);
            for (int b = start; b < start + table.LengthOf(i); b++)
            {
                Assert.Equal(i, table.IndexOfOffsetFrom(b, 0, table.Count));
                Assert.Equal(i, table.IndexOfOffsetFrom(b, i, table.Count));
                Assert.Equal(i, table.IndexOfOffset(b, 0, table.Count));
            }
        }
    }

    [Fact]
    public void IndexOfOffsetFrom_AgreesWithBinarySearchBeyondTheLinearProbeWindow()
    {
        // The forward-walking lookup probes a few entries then falls back to a bisect; both
        // branches must give the same answer.
        var names = Enumerable.Range(0, 500).Select(i => $"file{i}.txt").ToArray();
        var table = Build(names);

        for (int i = 0; i < names.Length; i++)
        {
            int offset = table.StartOf(i);
            Assert.Equal(i, table.IndexOfOffsetFrom(offset, 0, table.Count));
            Assert.Equal(i, table.IndexOfOffset(offset, 0, table.Count));
        }
    }
}
