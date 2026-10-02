namespace MathPhys.Ink.ViewModels;

/// <summary>当前打开的文档状态（只读展示用）。</summary>
public sealed class DocumentViewModel : Infrastructure.ObservableObject
{
    private string? _filePath;
    private int _pageCount;

    public string? FilePath
    {
        get => _filePath;
        private set
        {
            if (SetProperty(ref _filePath, value)) OnPropertyChanged(nameof(DisplayName));
        }
    }

    public int PageCount
    {
        get => _pageCount;
        private set
        {
            if (SetProperty(ref _pageCount, value))
            {
                OnPropertyChanged(nameof(HasDocument));
                OnPropertyChanged(nameof(PageCountText));
            }
        }
    }

    public bool HasDocument => _pageCount > 0;

    /// <summary>状态栏用的页数文案。刻意不在 XAML 里用 StringFormat——那东西带 {} 很容易踩解析坑。</summary>
    public string PageCountText => _pageCount > 0 ? $"共 {_pageCount} 页" : string.Empty;

    public string DisplayName =>
        string.IsNullOrEmpty(_filePath) ? "（未打开文档）" : System.IO.Path.GetFileName(_filePath);

    public void Apply(string filePath, int pageCount)
    {
        FilePath = filePath;
        PageCount = pageCount;
    }

    public void Clear()
    {
        FilePath = null;
        PageCount = 0;
    }
}
