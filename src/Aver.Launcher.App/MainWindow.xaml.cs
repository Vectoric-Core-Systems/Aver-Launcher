using System.Windows;
using System.Windows.Threading;
using Aver.Launcher.App.ViewModels;
using Aver.Launcher.Core;

namespace Aver.Launcher.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();
    private readonly DispatcherTimer _pollTimer = new();
    private readonly Random _jitter = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;

        Loaded += async (_, _) =>
        {
            await _vm.LoadAsync(CheckTrigger.Launch);
            StartPollTimer();
        };

        // Coming back to the window is a good moment to look, but focus is cheap to gain and lose,
        // so the policy rate-limits this to once per half hour. The handler stays dumb; the decision
        // lives in UpdateCheckPolicy where it can be tested against a fixed clock.
        Activated += async (_, _) => await _vm.LoadAsync(CheckTrigger.Activated);

        _pollTimer.Tick += async (_, _) =>
        {
            await _vm.LoadAsync(CheckTrigger.Timer);
            // Re-jitter every tick, so instances that started together do not converge on one second.
            _pollTimer.Interval = UpdateCheckPolicy.NextTimerDelay(_jitter);
        };

        Closed += (_, _) => _pollTimer.Stop();
    }

    private void StartPollTimer()
    {
        _pollTimer.Interval = UpdateCheckPolicy.NextTimerDelay(_jitter);
        _pollTimer.Start();
    }

    private void OnMinimise(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximise(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
