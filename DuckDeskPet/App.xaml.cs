using System.Threading;
using System.Windows;

namespace DuckDeskPet;

public partial class App : Application
{
    private Mutex? _singleInstanceMutex;
    private bool _ownsMutex;
    private NativeReminderNotificationHost? _reminderNotifications;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstanceMutex = new Mutex(
            initiallyOwned: true,
            name: @"Local\EagleDeskPet-7C4F36B8-9D50-4D9A-B46E-92B174F0FD81" + Integration.PetBridgeProtocol.TestChannelSuffix,
            createdNew: out _ownsMutex);

        _reminderNotifications = new NativeReminderNotificationHost(AppPaths.DataDirectory, Integration.PetBridgeProtocol.TestChannelSuffix);
        if (!_ownsMutex)
        {
            if (NativeReminderNotificationHost.IsToastActivation)
                await _reminderNotifications.ForwardSecondaryActivationAsync();
            Shutdown();
            return;
        }

        PetUiMotion.RegisterForApplication(typeof(App).Assembly);
        _reminderNotifications.StartPrimary(new ReminderStore(AppPaths.DataDirectory).Load().NativeNotificationsEnabled);
        var pet = new PetWindow();
        MainWindow = pet;
        MainWindow.Show();
        _reminderNotifications.Attach(pet.HandleReminderActivation);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _reminderNotifications?.Dispose();
        if (_ownsMutex)
        {
            _singleInstanceMutex?.ReleaseMutex();
        }

        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }
}
