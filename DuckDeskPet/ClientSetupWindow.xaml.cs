using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using DuckDeskPet.ClientSetup;

namespace DuckDeskPet;

public partial class ClientSetupWindow : Window
{
    private ClientSetupService? _service;
    private ClientSetupPlan? _installPlan;
    private ClientSetupPlan? _removePlan;
    private bool _ready, _busy, _writing, _closed;
    private Task _operation = Task.CompletedTask;
    internal Task PendingOperation => _operation;
    internal bool IsWriting => _writing;

    internal ClientSetupWindow(ClientSetupService? service = null)
    {
        InitializeComponent();
        MaxHeight = Math.Max(320, SystemParameters.WorkArea.Height - 24);
        MaxWidth = Math.Max(420, SystemParameters.WorkArea.Width - 24);
        Height = Math.Min(Height, MaxHeight);
        Width = Math.Min(Width, MaxWidth);
        Closing += OnClosing;
        Closed += (_, _) => _closed = true;
        try { _service = service ?? ClientSetupService.CreateDefault(AppContext.BaseDirectory); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            ResultBorder.Visibility = Visibility.Visible;
            ResultText.Text = "无法安全确定客户端配置目录。请检查用户目录、CODEX_HOME 和本机目录权限；未修改任何配置。";
        }
        _ready = true;
        ClientBox.SelectedIndex = 0;
    }

    private ClientKind SelectedClient => ClientBox.SelectedIndex switch
    {
        1 => ClientKind.ClaudeCode,
        2 => ClientKind.CodeBuddyCode,
        _ => ClientKind.Codex
    };

    private void Client_OnChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_ready && !_busy) _operation = RefreshPreviewAsync();
    }

    private void Refresh_OnClick(object sender, RoutedEventArgs e)
    {
        if (!_busy) _operation = RefreshPreviewAsync();
    }

    private async Task RefreshPreviewAsync()
    {
        ClientKind client = SelectedClient;
        ClientDescriptionText.Text = client switch
        {
            ClientKind.ClaudeCode => "Claude Code 本机客户端；不等同于 Claude 网页聊天或桌面聊天应用。",
            ClientKind.CodeBuddyCode => "CodeBuddy Code 本机命令行客户端；不冒充所有 CodeBuddy IDE 版本均已兼容。Windows Hook 还需客户端要求的 Git Bash。",
            _ => "Codex 本机配置层。已有手动小鹰 Hook 时会提示冲突，避免一条回复提醒两遍。"
        };
        TrustText.Text = client == ClientKind.Codex
            ? "配置完成后，在 Codex 中检查 MCP 连接，并通过 /hooks 或客户端 Hook 审核入口信任新 Hook，再开启新会话。不会替你绕过信任审核。"
            : "配置完成后，重启客户端或开启新会话，确认 MCP 和 Hooks 状态。客户端或组织策略禁止的 Hook 不会被本程序绕过。";
        _installPlan = _removePlan = null;
        if (_service is null) { SetBusy(false); return; }
        SetBusy(true);
        PlanStatusText.Text = "正在检查本机文件，仅预览，不写入…";
        try
        {
            var plans = await Task.Run(() => (_service.Preview(client), _service.Preview(client, SetupAction.Remove)));
            if (_closed) return;
            (_installPlan, _removePlan) = plans;
            DisplayPlan(_installPlan);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            if (!_closed) PlanStatusText.Text = "无法安全读取配置，未写入任何文件。请检查目录权限后重试。";
        }
        finally { if (!_closed) SetBusy(false); }
    }

    private void DisplayPlan(ClientSetupPlan plan)
    {
        PlanStatusText.Text = plan.Status;
        TargetsText.Text = string.Join(Environment.NewLine + Environment.NewLine,
            plan.Files.Select(file => $"{(file.WillChange ? "将修改" : "检查")} · {file.Description}{Environment.NewLine}{file.Path}"));
        NotesText.Text = string.Join(Environment.NewLine, plan.Notes);
    }

    private void Install_OnClick(object sender, RoutedEventArgs e)
    {
        if (!_busy && _installPlan is { CanApply: true, HasChanges: true })
            _operation = ApplyPlanAsync(_installPlan);
    }

    private void Remove_OnClick(object sender, RoutedEventArgs e)
    {
        if (_busy || _removePlan is not { CanApply: true, HasChanges: true }) return;
        DisplayPlan(_removePlan);
        if (MessageBox.Show(this, "只移除本程序管理的小鹰 MCP 和 Hook，保留你的其他配置和手动接入。修改前仍会备份。是否继续？",
            "移除小鹰配置", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
        {
            if (_installPlan is not null) DisplayPlan(_installPlan);
            return;
        }
        _operation = ApplyPlanAsync(_removePlan);
    }

    private async Task ApplyPlanAsync(ClientSetupPlan plan)
    {
        if (_service is null) return;
        SetBusy(true);
        _writing = true;
        ResultBorder.Visibility = Visibility.Visible;
        ResultText.Text = "正在备份并更新本机配置…";
        BackupsText.Visibility = Visibility.Collapsed;
        try
        {
            ClientSetupResult result = await Task.Run(() => _service.Apply(plan));
            if (_closed) return;
            ResultText.Text = result.Message;
            BackupsText.Text = string.Join(Environment.NewLine, result.BackupPaths);
            BackupsText.Visibility = result.BackupPaths.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            if (!_closed) ResultText.Text = "配置未能完成，请检查文件权限。不要把本次操作视为接入成功；重新检查后再试。";
        }
        finally
        {
            _writing = false;
            if (!_closed)
            {
                SetBusy(false);
                await RefreshPreviewAsync();
                if (!_closed) await Dispatcher.InvokeAsync(() => ResultBorder.BringIntoView(), DispatcherPriority.Loaded);
            }
        }
    }

    private void SetBusy(bool value)
    {
        _busy = value;
        ClientBox.IsEnabled = RefreshButton.IsEnabled = !value && _service is not null;
        InstallButton.IsEnabled = !value && _installPlan is { CanApply: true, HasChanges: true };
        RemoveButton.IsEnabled = !value && _removePlan is { CanApply: true, HasChanges: true };
        InstallButton.Content = value ? "处理中…" : _installPlan is { CanApply: true, HasChanges: false } ? "已配置" : "一键接入";
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_writing)
        {
            e.Cancel = true;
            ResultText.Text = "正在保存，请等待备份和配置写入完成后再关闭。";
        }
    }

    private void Window_OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Close(); e.Handled = true; }
    }
}
