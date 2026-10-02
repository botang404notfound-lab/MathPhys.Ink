namespace MathPhys.Ink.Infrastructure;

/// <summary>
/// 极简 INotifyPropertyChanged 基类。
/// 刻意不引 CommunityToolkit.Mvvm：这里只需要这一二十行，不值得多一个依赖。
/// </summary>
public abstract class ObservableObject : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));

    protected bool SetProperty<T>(ref T field, T value,
        [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}
