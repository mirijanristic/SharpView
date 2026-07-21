using SharpView.Services;
using Xunit;

namespace SharpView.Tests;

public sealed class ImageNavigatorTests : IDisposable
{
    readonly string _dir = Path.Combine(
        Path.GetTempPath(), "SharpViewTests_" + Guid.NewGuid().ToString("N"));

    public ImageNavigatorTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    string Touch(string fileName)
    {
        string path = Path.Combine(_dir, fileName);
        File.WriteAllBytes(path, Array.Empty<byte>());
        return path;
    }

    [Fact]
    public void ScanFolder_FiltersUnsupportedExtensions_AndSortsCaseInsensitively()
    {
        Touch("b.png");
        Touch("A.jpg");
        Touch("notes.txt");
        Touch("c.bmp");

        var nav = new ImageNavigator();
        nav.ScanFolder(Path.Combine(_dir, "b.png"));

        Assert.Equal(3, nav.Count);
        Assert.Equal("A.jpg", Path.GetFileName(nav.Files[0]));
        Assert.Equal("b.png", Path.GetFileName(nav.Files[1]));
        Assert.Equal("c.bmp", Path.GetFileName(nav.Files[2]));
    }

    [Fact]
    public void ScanFolder_SetsCurrentIndexToOpenedFile()
    {
        Touch("a.png");
        string opened = Touch("m.png");
        Touch("z.png");

        var nav = new ImageNavigator();
        nav.ScanFolder(opened);

        Assert.Equal(1, nav.CurrentIndex);
        Assert.Equal(opened, nav.CurrentFile);
    }

    [Fact]
    public void MoveNext_StopsAtLastImage()
    {
        Touch("a.png");
        Touch("b.png");

        var nav = new ImageNavigator();
        nav.ScanFolder(Path.Combine(_dir, "a.png"));

        Assert.True(nav.MoveNext());
        Assert.False(nav.MoveNext()); // already at the end
        Assert.Equal(1, nav.CurrentIndex);
    }

    [Fact]
    public void MovePrevious_StopsAtFirstImage()
    {
        Touch("a.png");
        Touch("b.png");

        var nav = new ImageNavigator();
        nav.ScanFolder(Path.Combine(_dir, "a.png"));

        Assert.False(nav.MovePrevious());
        Assert.Equal(0, nav.CurrentIndex);
    }

    [Fact]
    public void MoveTo_RejectsOutOfRangeAndSameIndex()
    {
        Touch("a.png");
        Touch("b.png");

        var nav = new ImageNavigator();
        nav.ScanFolder(Path.Combine(_dir, "a.png"));

        Assert.False(nav.MoveTo(-1));
        Assert.False(nav.MoveTo(2));
        Assert.False(nav.MoveTo(0)); // same index
        Assert.True(nav.MoveTo(1));
        Assert.Equal(1, nav.CurrentIndex);
    }

    [Fact]
    public void MoveFirstAndMoveLast_ReportWhetherIndexChanged()
    {
        Touch("a.png");
        Touch("b.png");
        Touch("c.png");

        var nav = new ImageNavigator();
        nav.ScanFolder(Path.Combine(_dir, "b.png"));

        Assert.True(nav.MoveFirst());
        Assert.False(nav.MoveFirst());
        Assert.True(nav.MoveLast());
        Assert.False(nav.MoveLast());
        Assert.Equal(2, nav.CurrentIndex);
    }
}
