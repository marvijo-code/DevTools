using DevTools.Services.FolderSizes;
using Xunit;

namespace DiskUsageAnalyzer.Tests.FolderSizes;

public sealed class FolderFileListingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadLargest_IncludesHiddenFilesButExcludesNestedFiles(bool relativePath)
    {
        var fixture = Directory.CreateTempSubdirectory("devtools-file-ranking-");
        var nested = Path.Combine(fixture.FullName, "nested");
        var hidden = Path.Combine(fixture.FullName, "hidden.bin");
        var visible = Path.Combine(fixture.FullName, "visible.bin");
        var small = Path.Combine(fixture.FullName, "small.bin");
        var deep = Path.Combine(nested, "deep.bin");

        try
        {
            Directory.CreateDirectory(nested);
            File.WriteAllBytes(hidden, new byte[10]);
            File.SetAttributes(hidden, FileAttributes.Hidden);
            File.WriteAllBytes(visible, new byte[5]);
            File.WriteAllBytes(small, []);
            File.WriteAllBytes(deep, new byte[50]);

            var folder = relativePath ? Path.GetRelativePath(Environment.CurrentDirectory, fixture.FullName) : fixture.FullName;
            var results = FolderFileListing.ReadLargest(folder, 2);

            Assert.Equal([hidden, visible], results.Select(item => item.Path));
            Assert.Equal([10L, 5L], results.Select(item => item.Bytes));
        }
        finally
        {
            File.Delete(hidden);
            File.Delete(visible);
            File.Delete(small);
            if (Directory.Exists(nested))
            {
                File.Delete(deep);
                Directory.Delete(nested);
            }

            fixture.Delete();
        }
    }
}
