namespace SharpView.Services;

/// <summary>
/// Scans a folder for supported image files and provides sorted navigation.
/// </summary>
sealed class ImageNavigator
{
    // Note: .webp is intentionally absent — the GDI+ decoder (System.Drawing)
    // cannot open WebP, so listing it only produced silently-failing entries.
    // Add it back once decoding moves to WIC/Windows Imaging Component.
    static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tiff", ".tif"
    };

    string[] _files = Array.Empty<string>();
    int _currentIndex;

    public string[] Files => _files;
    public int CurrentIndex => _currentIndex;
    public int Count => _files.Length;
    public string CurrentFile => _files.Length > 0 ? _files[_currentIndex] : string.Empty;
    public bool HasFiles => _files.Length > 0;

    /// <summary>
    /// Scan the folder containing the given file. Sorts A-Z (case-insensitive)
    /// and sets the current index to the given file.
    /// </summary>
    public void ScanFolder(string imagePath)
    {
        string? dir = Path.GetDirectoryName(imagePath);
        if (string.IsNullOrEmpty(dir))
        {
            _files = new[] { imagePath };
            _currentIndex = 0;
            return;
        }

        _files = Directory.EnumerateFiles(dir)
            .Where(f => SupportedExtensions.Contains(Path.GetExtension(f)))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        _currentIndex = Array.FindIndex(_files,
            f => string.Equals(f, imagePath, StringComparison.OrdinalIgnoreCase));
        if (_currentIndex < 0) _currentIndex = 0;
    }

    /// <summary>Navigate to the previous image. Returns true if the index changed.</summary>
    public bool MovePrevious()
    {
        if (_currentIndex > 0) { _currentIndex--; return true; }
        return false;
    }

    /// <summary>Navigate to the next image. Returns true if the index changed.</summary>
    public bool MoveNext()
    {
        if (_currentIndex < _files.Length - 1) { _currentIndex++; return true; }
        return false;
    }

    /// <summary>Jump to a specific index. Returns true if the index changed.</summary>
    public bool MoveTo(int index)
    {
        if (index < 0 || index >= _files.Length || index == _currentIndex) return false;
        _currentIndex = index;
        return true;
    }

    public bool MoveFirst()
    {
        if (_files.Length == 0 || _currentIndex == 0) return false;
        _currentIndex = 0;
        return true;
    }

    public bool MoveLast()
    {
        if (_files.Length == 0 || _currentIndex == _files.Length - 1) return false;
        _currentIndex = _files.Length - 1;
        return true;
    }
}
