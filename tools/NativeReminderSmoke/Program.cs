using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DuckDeskPet;
using Microsoft.Toolkit.Uwp.Notifications;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // Scope is permanently derived from this dedicated test EXE, not temporary
        // environment variables that a cold Windows COM activation would lose
        string executable = Environment.ProcessPath ?? throw new InvalidOperationException();
        if (!Path.GetFileName(executable).Equals("EagleNativeReminderSmoke.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Native smoke must run as its dedicated test apphost");
        string scope = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(executable.ToUpperInvariant())))[..20];
        string data = Path.Combine(Path.GetDirectoryName(executable)!, "isolated-native-reminder-data");
        Directory.CreateDirectory(data);
        void Log(string name, object? value = null) => File.AppendAllText(Path.Combine(data, "events.jsonl"),
            JsonSerializer.Serialize(new { time = DateTimeOffset.UtcNow, pid = Environment.ProcessId, name, value }) + "\n");
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        using var mutex = new Mutex(true, "Local\\EagleNativeReminderSmoke." + scope, out bool primary);
        using var host = new NativeReminderNotificationHost(data, ".test." + scope, allowIsolatedRegistration: true);
        app.Startup += async (_, _) =>
        {
            Log("startup", new { primary, toast = NativeReminderNotificationHost.IsToastActivation });
            if (!primary)
            {
                await host.ForwardSecondaryActivationAsync();
                Log("secondary-exit"); app.Shutdown(); return;
            }
            host.StartPrimary(true);
            host.Attach(activation => app.Dispatcher.BeginInvoke(() =>
            {
                Log("activated", activation);
                if (app.MainWindow is { } window) { window.Show(); window.Activate(); }
            }));
            if (args.Contains("--cleanup"))
            {
                // Dedicated EXE-derived identity only, never production history
                ToastNotificationManagerCompat.Uninstall(); Log("isolated-registration-cleaned"); app.Shutdown(); return;
            }
            var text = new TextBlock { Text = "大头鹰原生通知隔离测试\n\n此专用 EXE 不读真实宠物存档。\n点击 Windows 通知将写入本目录事件日志。", Margin = new Thickness(20), TextWrapping = TextWrapping.Wrap };
            var button = new Button { Content = "发送真正的 Windows 测试通知", Margin = new Thickness(20), Padding = new Thickness(15) };
            button.Click += (_, _) => { var result = host.SendTest(); Log("send", result); text.Text = result.Detail; };
            var panel = new StackPanel(); panel.Children.Add(text); panel.Children.Add(button);
            app.MainWindow = new Window { Title = "Eagle native notification — isolated smoke", Width = 450, Height = 250, Content = panel };
            app.MainWindow.Closed += (_, _) => app.Shutdown();
            if (!args.Contains("--headless")) app.MainWindow.Show();
            if (args.Contains("--send")) Log("send", host.SendTest());
            var audit = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            audit.Tick += (_, _) => { audit.Stop(); try { Log("history", ToastNotificationManagerCompat.History.GetHistory().Select(t => new { t.Tag, t.Group, Xml = t.Content.GetXml() }).ToArray()); } catch (Exception ex) { Log("history-error", ex.GetType().Name); } };
            audit.Start();
            var exit = new DispatcherTimer { Interval = TimeSpan.FromMinutes(4) };
            exit.Tick += (_, _) => { exit.Stop(); app.Shutdown(); }; exit.Start();
        };
        app.Run();
        if (primary) mutex.ReleaseMutex();
    }
}
