using System.Threading;
using System.Windows;

namespace DuckDeskPet;

public partial class App : Application
{
    private Mutex? _singleInstanceMutex;
    private bool _ownsMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstanceMutex = new Mutex(
            initiallyOwned: true,
            name: @"Local\EagleDeskPet-7C4F36B8-9D50-4D9A-B46E-92B174F0FD81" + Integration.PetBridgeProtocol.TestChannelSuffix,
            createdNew: out _ownsMutex);

        if (!_ownsMutex)
        {
            Shutdown();
            return;
        }

        MainWindow = new PetWindow();
        MainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_ownsMutex)
        {
            _singleInstanceMutex?.ReleaseMutex();
        }

        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }
}
