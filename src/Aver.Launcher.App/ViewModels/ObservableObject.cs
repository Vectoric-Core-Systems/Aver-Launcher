using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace Aver.Launcher.App.ViewModels;

/// <summary>
/// Minimal INotifyPropertyChanged base.
/// </summary>
/// <remarks>
/// Hand-rolled rather than taking a dependency on CommunityToolkit.Mvvm, because the launcher ships
/// self-contained and single-file and currently has zero NuGet references outside the test project.
/// One base class and one command type is a smaller cost than the first package that has to be
/// audited, updated and explained in the third-party notices.
/// </remarks>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }
}

/// <summary>A command backed by a delegate.</summary>
public sealed class RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null) : ICommand
{
    private readonly Action<object?> _execute = execute ?? throw new ArgumentNullException(nameof(execute));

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => canExecute?.Invoke(parameter) ?? true;

    public void Execute(object? parameter) => _execute(parameter);
}
