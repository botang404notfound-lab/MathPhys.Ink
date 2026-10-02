using System.IO;
using MathPhys.Ink.Infrastructure;
using MathPhys.Ink.Ink;

namespace MathPhys.Ink.ViewModels;

/// <summary>
/// 当前工程的界面状态：路径、标题、PDF 绑定方式、脏标记、上次保存时刻。
/// </summary>
/// <remarks>
/// <para>
/// 只存<b>状态</b>，不碰磁盘也不碰控件 —— 于是整条状态机可以在 harness 里逐条断言。
/// 保存/装载的动作在 <see cref="MainWindow"/> 侧（它才拿得到画布里的笔迹与窗口对话框），
/// 与既有"保存批注"的分工保持一致。
/// </para>
/// <para>
/// <b>为什么脏标记不跟视口走。</b>视图状态（缩放/平移）虽然也存进工程，但它不算"改动"：
/// 把它算进去的话，老师只是翻了一页、关窗口就会被拦下来问"要保存吗"。
/// 视图状态改成<b>保存时顺手记下</b>，于是"未保存"这三个字永远只对应"老师真的画了东西"。
/// </para>
/// </remarks>
public sealed class ProjectViewModel : ObservableObject
{
    private string? _filePath;
    private string _title = string.Empty;
    private string _createdUtc = string.Empty;
    private string _embedding = TwbPdfEmbedding.None;
    private bool _hasPdf;
    private bool _isDirty;
    private DateTime? _savedAt;

    /// <summary>工程文件路径；<c>null</c> 表示当前没有打开工程（快速模式）。</summary>
    public string? FilePath
    {
        get => _filePath;
        private set
        {
            if (SetProperty(ref _filePath, value))
            {
                OnPropertyChanged(nameof(HasProject));
                OnPropertyChanged(nameof(FileName));
                OnPropertyChanged(nameof(DisplayText));
                OnPropertyChanged(nameof(SaveStatusText));
                Raise();
            }
        }
    }

    public bool HasProject => !string.IsNullOrEmpty(_filePath);

    /// <summary>工程文件名（含扩展名）；没有工程时为空串。</summary>
    public string FileName => string.IsNullOrEmpty(_filePath) ? string.Empty : Path.GetFileName(_filePath!);

    /// <summary>工程标题（默认取 PDF 文件名，另存时可改）。</summary>
    public string Title
    {
        get => _title;
        private set
        {
            if (SetProperty(ref _title, value)) { OnPropertyChanged(nameof(DisplayText)); Raise(); }
        }
    }

    /// <summary>工程的创建时间（UTC，ISO 8601），重新保存时沿用。</summary>
    public string CreatedUtc => _createdUtc;

    /// <summary>PDF 绑定方式，取值见 <see cref="TwbPdfEmbedding"/>。</summary>
    public string Embedding
    {
        get => _embedding;
        private set
        {
            if (SetProperty(ref _embedding, value))
            {
                OnPropertyChanged(nameof(EmbeddingText));
                OnPropertyChanged(nameof(DisplayText));
                OnPropertyChanged(nameof(SaveStatusText));
                Raise();
            }
        }
    }

    /// <summary>这份工程是否带 PDF（空白工程为 <c>false</c>）。</summary>
    public bool HasPdf
    {
        get => _hasPdf;
        private set => SetProperty(ref _hasPdf, value);
    }

    /// <summary>有未保存的改动（只随笔迹 / 图形的变化置位）。</summary>
    public bool IsDirty
    {
        get => _isDirty;
        private set
        {
            if (SetProperty(ref _isDirty, value))
            {
                OnPropertyChanged(nameof(DisplayText));
                OnPropertyChanged(nameof(SaveStatusText));
                Raise();
            }
        }
    }

    /// <summary>本窗口内最后一次成功保存的时刻（仅用于显示）。</summary>
    public DateTime? SavedAt
    {
        get => _savedAt;
        private set
        {
            if (SetProperty(ref _savedAt, value)) { OnPropertyChanged(nameof(SaveStatusText)); Raise(); }
        }
    }

    /// <summary>绑定方式的中文说法。</summary>
    public string EmbeddingText => _embedding switch
    {
        TwbPdfEmbedding.Embedded => "内嵌",
        TwbPdfEmbedding.Referenced => "外挂",
        _ => "空白",
    };

    /// <summary>
    /// 工具栏右侧显示的工程标识：「文件名 · 绑定方式 · ● 未保存」。
    /// </summary>
    /// <remarks>
    /// 把"内嵌还是外挂"摆在标题旁边，是因为它直接决定老师下一步动作：
    /// 内嵌的工程拷走一个文件就够，外挂的必须连试卷一起拷 —— 现场最容易漏的正是这件事。
    /// </remarks>
    public string DisplayText
    {
        get
        {
            if (!HasProject) return "（未打开工程）";
            return $"{FileName} · {EmbeddingText}" + (_isDirty ? " · ● 未保存" : string.Empty);
        }
    }

    /// <summary>状态栏文案；没有工程时为空串（由窗口决定显示什么）。</summary>
    public string SaveStatusText
    {
        get
        {
            if (!HasProject) return string.Empty;
            if (_isDirty) return "● 工程未保存";
            if (_savedAt is { } time) return $"工程已保存 {time:HH:mm:ss}";
            return $"工程已保存（{EmbeddingText}）";
        }
    }

    /// <summary>工程状态有任何变化（窗口据此刷新标题与状态栏）。</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// 装载一份工程之后填入状态。
    /// </summary>
    /// <param name="twbPath">工程文件路径。</param>
    /// <param name="manifest">装载出来的元信息。</param>
    /// <param name="hasPdf">这份工程是否带 PDF（外挂找不到时为 <c>false</c>，等定位成功再改）。</param>
    public void Apply(string twbPath, TwbManifest manifest, bool hasPdf)
    {
        _createdUtc = manifest?.CreatedUtc ?? string.Empty;

        FilePath = twbPath;
        Title = string.IsNullOrEmpty(manifest?.Title) ? ProjectComposer.DefaultTitle(twbPath) : manifest!.Title;
        Embedding = manifest?.Pdf?.Embedding ?? TwbPdfEmbedding.None;
        HasPdf = hasPdf;

        // 刚打开的工程是"干净"的 —— 装载过程本身会被宿主当成一次笔迹变化，
        // 那一刻置上的脏标记必须在这里抹掉，否则一打开就显示"未保存"。
        _isDirty = false;
        OnPropertyChanged(nameof(IsDirty));
        _savedAt = null;
        OnPropertyChanged(nameof(SavedAt));
    }

    /// <summary>另存为之后换路径（标题保持不变，脏标记由调用方决定）。</summary>
    public void ChangePath(string twbPath)
    {
        FilePath = twbPath;
    }

    /// <summary>外挂 PDF 定位成功后更新绑定信息。</summary>
    public void MarkPdfLocated()
    {
        HasPdf = true;
        if (string.Equals(_embedding, TwbPdfEmbedding.None, StringComparison.Ordinal))
        {
            Embedding = TwbPdfEmbedding.Referenced;
        }
    }

    /// <summary>关掉工程，回到快速模式。</summary>
    public void Clear()
    {
        _createdUtc = string.Empty;
        FilePath = null;
        Title = string.Empty;
        Embedding = TwbPdfEmbedding.None;
        HasPdf = false;
        IsDirty = false;
        SavedAt = null;
        Raise();
    }

    /// <summary>标记有未保存的改动（笔迹 / 图形变化时调用）。</summary>
    public void MarkDirty()
    {
        if (IsDirty) return;

        IsDirty = true;
        // 置脏即表示"屏幕上与磁盘上不一致"，上次保存时刻不该继续显示成当前状态
        SavedAt = null;
    }

    /// <summary>标记已保存。</summary>
    public void MarkClean(DateTime savedAt)
    {
        _isDirty = false;
        OnPropertyChanged(nameof(IsDirty));
        SavedAt = savedAt;
    }

    private void Raise() => Changed?.Invoke(this, EventArgs.Empty);
}
