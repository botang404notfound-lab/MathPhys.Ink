using System.Windows;
using MathPhys.Ink.Design.Dialogs;
using MathPhys.Ink.Infrastructure;
using MathPhys.Ink.Pdf;

namespace MathPhys.Ink.ViewModels;

/// <summary>主窗口视图模型：文档的打开/关闭与状态提示。</summary>
public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly IPdfDocumentService _pdf = new PdfiumDocumentService();
    private string _status = "就绪。点击「打开 PDF」载入试卷。";

    public MainViewModel()
    {
        OpenCommand = new RelayCommand(OpenDocument);
        CloseCommand = new RelayCommand(CloseDocument, () => Document.HasDocument);
    }

    public DocumentViewModel Document { get; } = new();

    public RelayCommand OpenCommand { get; }

    public RelayCommand CloseCommand { get; }

    /// <summary>供视图层把文档交给画布宿主。M1 用事件而非绑定，等画布接口稳定后再收敛。</summary>
    public IPdfDocumentService PdfService => _pdf;

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    /// <summary>
    /// 由外部（工具、画布宿主）往状态栏写一句话。
    /// </summary>
    /// <remarks>
    /// 工具要能给用户反馈（「直线 12.3 cm，倾角 30°」），但它拿到的只有
    /// <c>IToolContext.SetStatus</c>，不该知道界面是怎么搭的 —— 于是由这一处对接。
    /// </remarks>
    public void SetStatus(string text) => Status = text;

    /// <summary>文档状态发生变化（打开或关闭），视图层据此刷新画布。</summary>
    public event EventHandler? DocumentChanged;

    private void OpenDocument()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择试卷 PDF",
            Filter = "PDF 文件 (*.pdf)|*.pdf|所有文件 (*.*)|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog() != true) return;

        OpenDocument(dialog.FileName);
    }

    /// <summary>
    /// 打开指定路径的 PDF（<b>不弹对话框</b>）。
    /// </summary>
    /// <remarks>
    /// 供<b>工程模式</b>使用：工程里的 PDF 要么来自包内字节（已落到临时文件）、
    /// 要么来自 manifest 里记的路径 —— 两种都不是"让老师现场挑一份"。
    /// 失败时同样给出可读原因并清空文档，于是工程装载流程可以据此判成
    /// "PDF 打不开"，而不是带着一个错位文档继续往下走。
    /// </remarks>
    /// <returns>是否打开成功。</returns>
    public bool OpenDocument(string filePath)
    {
        bool ok = true;

        try
        {
            _pdf.Open(filePath);
            Document.Apply(filePath, _pdf.PageCount);
            Status = $"已打开 {Document.DisplayName}，共 {_pdf.PageCount} 页。";

            // 打开文档是低频动作，值得在日志里留一条：现场"怎么开的是这份卷子"全靠它。
            AppLog.Info($"已打开文档：{filePath}（共 {_pdf.PageCount} 页）");
        }
        catch (Exception ex)
        {
            // 打开失败要给出可读原因，而不是让异常冒到全局处理器
            ok = false;
            Document.Clear();
            Status = "打开失败：" + ex.Message;
            TDialog.Show($"无法打开这个 PDF：\n\n{ex.Message}", "数理墨",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        CloseCommand.RaiseCanExecuteChanged();
        DocumentChanged?.Invoke(this, EventArgs.Empty);
        return ok;
    }

    /// <summary>关闭当前 PDF（工程模式切到"空白工程"时用得到）。</summary>
    public void CloseDocument()
    {
        _pdf.Close();
        Document.Clear();
        Status = "已关闭文档。";
        CloseCommand.RaiseCanExecuteChanged();
        DocumentChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        // PdfDocument 持有原生句柄；虽然进程退出会兜底，但显式释放才是正确姿势
        _pdf.Dispose();
    }
}
