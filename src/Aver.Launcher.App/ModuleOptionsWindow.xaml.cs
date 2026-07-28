using System.Windows;
using Aver.Launcher.App.ViewModels;
using Aver.Launcher.Core;

namespace Aver.Launcher.App;

public partial class ModuleOptionsWindow : Window
{
    public ModuleOptionsWindow(ModuleOptionsViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        Model = vm;
    }

    public ModuleOptionsViewModel Model { get; }

    /// <summary>The edition the user settled on, or null when they cancelled.</summary>
    public EditionMatch? Chosen { get; private set; }

    private void OnProceed(object sender, RoutedEventArgs e)
    {
        Chosen = Model.Best;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
