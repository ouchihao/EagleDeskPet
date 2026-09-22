using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Toolkit.Uwp.Notifications;
using Windows.UI.Notifications;
using DuckDeskPet.Core;

namespace DuckDeskPet;

/// <summary>
/// Native Windows toast transport for an unpackaged WPF EXE; Toolkit 7.1.3 owns
/// the per-user AUMID/COM identity (derived from the EXE path), with bounded
/// same-user activation forwarding here, not a scheduler or a cloud service
/// No registration occurs until Enable() or a genuine -ToastActivated launch
/// </summary>
internal sealed class NativeReminderNotificationHost : IReminderNativeChannel, IDisposable
{
    private readonly string _pipeName;
    private readonly string _logoPath;
    private readonly bool _registrationAllowed;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Queue<ReminderNativeActivation> _queued = new();
    private readonly TaskCompletionSource _secondaryActivation = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _gate = new();
    private Action<ReminderNativeActivation>? _activation;
    private readonly ReminderRegistrationGate _registration = new();
    private bool _primary;
    private bool _disposed;
    private string? _registrationError;

    internal NativeReminderNotificationHost(string dataDirectory, string instanceSuffix = "", bool allowIsolatedRegistration = false)
    {
        // Environment/test scopes are inputs, never silently fall back to the live pet
        if (instanceSuffix.Length > 80 || instanceSuffix.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-')))
            throw new ArgumentException("Invalid reminder notification instance scope.");
        using var identity = WindowsIdentity.GetCurrent();
        string scope = (identity.User?.Value ?? throw new InvalidOperationException("Windows SID unavailable.")) + instanceSuffix;
        _pipeName = "EagleDeskPet.ReminderActivation." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scope)))[..24];
        _logoPath = Path.Combine(Path.GetFullPath(dataDirectory), "reminder-notification-logo.png");
        _registrationAllowed = instanceSuffix.Length == 0 && Environment.GetEnvironmentVariable("EAGLE_PET_DATA_DIR") is null &&
            Environment.GetEnvironmentVariable("EAGLE_PET_TEST_CHANNEL") is null;
        if (allowIsolatedRegistration)
        {
            // COM cold activation does not inherit a test launch's environment
            // Only the dedicated smoke executable has a permanent isolated entry point
            if (System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name != "EagleNativeReminderSmoke")
                throw new InvalidOperationException("Isolated registration is restricted to the dedicated smoke executable");
            _registrationAllowed = true;
        }
    }

    internal static bool IsToastActivation => Environment.GetCommandLineArgs().Any(a => a == "-ToastActivated");

    /// <summary>Call after the existing app mutex decides ownership, before constructing PetWindow</summary>
    internal void StartPrimary(bool notificationsEnabled)
    {
        _primary = true;
        ReminderNotificationServices.Current = this;
        _ = ListenAsync(_shutdown.Token);
        if (notificationsEnabled || IsToastActivation) Enable();
    }

    /// <summary>A duplicate toast-launched process stays alive long enough to receive its COM callback,
    /// forwards it to the mutex owner and exits without constructing a second pet</summary>
    internal async Task ForwardSecondaryActivationAsync()
    {
        if (!IsToastActivation) return;
        Enable();
        try { await _secondaryActivation.Task.WaitAsync(TimeSpan.FromSeconds(12)); }
        catch (TimeoutException) { /* No callback: exit the temporary activation process safely */ }
    }

    internal void Attach(Action<ReminderNativeActivation> activation)
    {
        ReminderNativeActivation[] pending;
        lock (_gate) { _activation = activation; pending = _queued.ToArray(); _queued.Clear(); }
        foreach (var item in pending) activation(item);
    }

    internal string? Enable()
    {
        if (_registration.IsRegistered) return null;
        if (_disposed) return "通知服务已关闭。";
        if (!_registrationAllowed)
            return "自定义数据目录或测试通道下不注册 Windows 通知，防止系统点击冷启动后进入默认存档；请在正常启动的桌宠中使用。";
        try
        {
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763)) return "原生提醒需要 Windows 10 1809 或更新版本。";
            using var identity = WindowsIdentity.GetCurrent();
            if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
                return "请以普通用户运行桌宠；本功能不为管理员进程注册通知。";
            EnsureLogo();
            _registration.Ensure(() => ToastNotificationManagerCompat.OnActivated += OnActivated,
                () => _ = ToastNotificationManagerCompat.CreateToastNotifier(),
                () => ToastNotificationManagerCompat.OnActivated -= OnActivated);
            _registrationError = null;
            return null;
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException))
        {
            _registrationError = "Windows 通知注册失败（" + ex.GetType().Name + "）；提醒仍保留在本机。";
            return _registrationError;
        }
    }

    public ReminderNativeResult Send(IReadOnlyList<LocalReminder> batch)
    {
        if (batch.Count is < 1 or > ReminderScheduler.MaximumReminders) return new(false, "没有有效的到期提醒。");
        var first = batch[0];
        if (ReminderNativeActivation.Parse(new ReminderNativeActivation(first.Id, first.CycleId).Argument) is null)
            return new(false, "提醒标识无效，未发送。");
        string title = batch.Count == 1 ? "大头鹰 · 时间到啦" : $"大头鹰 · {batch.Count} 条提醒到点";
        string body = batch.Count == 1 ? first.Message : string.Join("；", batch.Take(2).Select(item => item.Message)) +
            (batch.Count > 2 ? "。其余请打开提醒列表查看。" : "。");
        string batchKey = string.Join("|", batch.Select(item => item.Id + item.CycleId).Order(StringComparer.Ordinal));
        return Publish(title, body, new(first.Id, first.CycleId),
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(batchKey)))[..16], batch);
    }

    public ReminderNativeResult SendTest() => Publish("大头鹰 · 测试通知", "这是 Windows 原生通知。点击回到定时提醒；不会创建、完成或暂停任何提醒。",
        new("", "", true), "test");

    public ReminderNativeResult Prepare()
    {
        string? error = Enable();
        if (error is not null) return new(false, error);
        try
        {
            return ToastNotificationManagerCompat.CreateToastNotifier().Setting == NotificationSetting.Enabled
                ? new(true, "Windows 通知接口可用；勿扰或锁屏仍可能隐藏横幅。")
                : new(false, "Windows 已禁用通知，暂未启用；请检查系统 → 通知后再试。");
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException))
        { return new(false, "通知能力检查失败（" + ex.GetType().Name + "），暂未启用。"); }
    }

    private ReminderNativeResult Publish(string title, string body, ReminderNativeActivation activation, string tag, IReadOnlyList<LocalReminder>? batch = null)
    {
        string? registration = Enable();
        if (registration is not null) return new(false, registration);
        try
        {
            var notifier = ToastNotificationManagerCompat.CreateToastNotifier();
            if (notifier.Setting != NotificationSetting.Enabled)
                return new(false, "Windows 已禁用此应用或系统通知；请检查 Windows 通知设置。本轮未提交，记录已保留。");
            var builder = new ToastContentBuilder().AddText(title).AddText(body)
                .AddAppLogoOverride(new Uri(_logoPath), ToastGenericAppLogoCrop.Circle)
                .AddButton(new ToastButton("查看提醒", activation.Argument));
            // The root launch argument and button use the same strict, ID-only protocol
            var content = builder.GetToastContent();
            content.Launch = activation.Argument;
            content.Audio = new ToastAudio { Silent = true };
            var toast = new ToastNotification(content.GetXml()) { Tag = tag, Group = "eagle-reminders", ExpirationTime = DateTimeOffset.UtcNow.AddDays(1) };
            if (batch is not null)
                toast.Failed += (_, args) => ReminderNotificationServices.ReportFailure(batch,
                    "Windows 随后报告通知失败（0x" + args.ErrorCode.HResult.ToString("X8") + "）；请检查系统通知设置，本轮不自动重发。");
            notifier.Show(toast);
            return new(true, "已提交 Windows；横幅受勿扰、锁屏及系统设置控制，不代表已显示或已读。");
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException))
        {
            return new(false, "Windows 通知提交失败或结果不确定（" + ex.GetType().Name + "）；本轮不自动重发，记录已保留。", Uncertain: true);
        }
    }

    private void EnsureLogo()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_logoPath)!);
        // A real bundled mascot, not a downloaded URL or generated notification mock-up
        string assemblyName = typeof(NativeReminderNotificationHost).Assembly.GetName().Name!;
        var resource = System.Windows.Application.GetResourceStream(new Uri("/" + assemblyName + ";component/Assets/mascot-animated-neutral.png", UriKind.Relative));
        if (resource is null) throw new IOException("Bundled notification logo unavailable.");
        using var input = resource.Stream;
        using var output = new FileStream(_logoPath, FileMode.Create, FileAccess.Write, FileShare.Read);
        input.CopyTo(output);
    }

    private void OnActivated(ToastNotificationActivatedEventArgsCompat args)
    {
        var activation = ReminderNativeActivation.Parse(args.Argument);
        if (activation is null) return;
        if (_primary) Receive(activation);
        else _ = ForwardAsync(activation);
    }

    private void Receive(ReminderNativeActivation activation)
    {
        Action<ReminderNativeActivation>? callback;
        lock (_gate)
        {
            if (_disposed) return;
            callback = _activation;
            if (callback is null) { if (_queued.Count < 8) _queued.Enqueue(activation); return; }
        }
        callback(activation);
    }

    private async Task ForwardAsync(ReminderNativeActivation activation)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await client.ConnectAsync(timeout.Token);
            byte[] bytes = Encoding.UTF8.GetBytes(activation.Argument + "\n");
            await client.WriteAsync(bytes, timeout.Token);
            await client.FlushAsync(timeout.Token);
            byte[] ack = new byte[1];
            await client.ReadAsync(ack, timeout.Token);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or UnauthorizedAccessException) { }
        finally { _secondaryActivation.TrySetResult(); }
    }

    private async Task ListenAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(token);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(2));
                var bytes = new List<byte>();
                byte[] one = new byte[1];
                while (bytes.Count <= 160 && await pipe.ReadAsync(one, timeout.Token) == 1 && one[0] != 10) bytes.Add(one[0]);
                var activation = ReminderNativeActivation.Parse(Encoding.UTF8.GetString(bytes.ToArray()));
                if (activation is not null) Receive(activation);
                await pipe.WriteAsync(new byte[] { 1 }, timeout.Token);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or UnauthorizedAccessException)
            { if (!token.IsCancellationRequested) await Task.Delay(100, token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing); }
        }
    }

    public void Dispose()
    {
        lock (_gate) { _disposed = true; _activation = null; _queued.Clear(); }
        _shutdown.Cancel();
        _registration.Detach(() => ToastNotificationManagerCompat.OnActivated -= OnActivated);
        // Do not Uninstall or clear notification history on normal exit: clicks remain useful
    }
}
