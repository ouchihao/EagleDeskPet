using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using DuckDeskPet.Core;

namespace DuckDeskPet;

/// <summary>Standalone UI; the owner rejects work/paused entry and exclusively owns desktop performance.</summary>
public partial class RpsWindow : Window
{
    private readonly RockPaperScissorsGame _game;
    private readonly Func<RpsPerformance, CancellationToken, Task> _performAsync;
    private readonly Func<DateTimeOffset?> _getLastRewardUtc;
    private readonly Func<RpsRewardClaim, bool> _tryCommitReward;
    private readonly DispatcherTimer _timer;
    private CancellationTokenSource _roundCancellation = new();
    private Task _performance = Task.CompletedTask;
    private RpsPhase _presentedPhase = RpsPhase.Idle;
    private bool _loaded, _closing, _allowClose, _cleaning, _settled, _paused, _cleaned = true;

    internal RpsWindow(Func<RpsPerformance, CancellationToken, Task> performAsync,
        Func<DateTimeOffset?> getLastRewardUtc, Func<RpsRewardClaim, bool> tryCommitReward,
        RockPaperScissorsGame? game = null)
    {
        _performAsync = performAsync ?? throw new ArgumentNullException(nameof(performAsync));
        _getLastRewardUtc = getLastRewardUtc ?? throw new ArgumentNullException(nameof(getLastRewardUtc));
        _tryCommitReward = tryCommitReward ?? throw new ArgumentNullException(nameof(tryCommitReward));
        _game = game ?? new RockPaperScissorsGame(lastGameRewardUtc: getLastRewardUtc());
        InitializeComponent();
        MaxHeight = Math.Max(MinHeight, SystemParameters.WorkArea.Height - 24);
        Height = Math.Min(Height, MaxHeight);
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
        _timer.Tick += Timer_OnTick;
        Closing += Window_OnClosing;
        Closed += (_, _) => { _timer.Stop(); _timer.Tick -= Timer_OnTick; _roundCancellation.Dispose(); };
    }

    internal Task PendingOperation => _performance;
    internal bool IsRoundActive => _game.IsActive || _cleaning;

    private void Window_OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;
        StartRound();
        _timer.Start();
    }

    private void StartRound()
    {
        if (_closing || _paused || _cleaning || !_cleaned || !_performance.IsCompleted || !_game.StartRound()) return;
        _roundCancellation.Dispose();
        _roundCancellation = new();
        _presentedPhase = RpsPhase.Idle;
        _settled = false;
        _cleaned = true;
        RewardText.Text = "每 5 分钟最多心情 +2；不获得经验或鹰币。";
        Refresh();
    }

    private void Choice_OnClick(object sender, RoutedEventArgs e)
    {
        if (_closing || _cleaning || sender is not Button { Tag: string name } ||
            !Enum.TryParse<RpsChoice>(name, out var choice)) return;
        if (_game.Choose(choice)) { _cleaned = false; Refresh(); }
    }
    private void Replay_OnClick(object sender, RoutedEventArgs e) => StartRound();
    private void Exit_OnClick(object sender, RoutedEventArgs e) => Close();
    private void Window_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { e.Handled = true; Close(); }
    }
    private void Timer_OnTick(object? sender, EventArgs e) => Refresh();

    internal void Refresh()
    {
        if (_closing) return;
        var state = _game.Advance();
        bool canChoose = state.Phase == RpsPhase.AwaitingChoice && !_paused && !_cleaning;
        RockButton.IsEnabled = PaperButton.IsEnabled = ScissorsButton.IsEnabled = canChoose;
        ReplayButton.IsEnabled = state.Phase is RpsPhase.Completed or RpsPhase.Cancelled && !_paused && !_cleaning && _cleaned && _performance.IsCompleted;
        RoundProgress.Value = Math.Min(10, state.RoundElapsedSeconds);
        (StageText.Text, DetailText.Text) = state.Phase switch
        {
            RpsPhase.AwaitingChoice => ("小鹰已选好，轮到你了！", "它不能偷看后改拳。选一个，看看谁更会摸鱼。"),
            RpsPhase.Preparing => ("石头、剪刀、布——", $"你选了{Label(state.PlayerChoice)}。拳已锁定，小鹰正在蓄势。"),
            RpsPhase.Throwing => ("出拳！", "先看小鹰完整出招，结果马上揭晓。"),
            RpsPhase.Reacting or RpsPhase.Completed => (OutcomeTitle(state.Outcome),
                $"你：{Label(state.PlayerChoice)}　小鹰：{Label(state.PetChoice)}\n{OutcomeLine(state.Outcome)}"),
            RpsPhase.Cancelled => ("这一局先收摊", state.Cancellation switch
            {
                RpsCancelReason.Paused => "已取消本局，不结算奖励；等收好动作再继续。",
                RpsCancelReason.TimedOut => "刚才停留太久，本局作废，没有发放奖励。",
                RpsCancelReason.PresentationFailed => "动作暂时没接上，本局作废，没有发放奖励。",
                _ => "本局已取消，没有发放奖励。",
            }),
            _ => ("小鹰正在偷偷选拳…", "不用下注，也不会扣饭。"),
        };
        if (_presentedPhase == state.Phase) return;
        _presentedPhase = state.Phase;
        if (_game.CurrentPerformance is { } performance)
            _performance = PlayAsync(performance, _roundCancellation.Token);
        else if (state.Phase == RpsPhase.Completed)
        {
            SettleReward();
            _performance = CleanUpAsync();
        }
        else if (state.Phase == RpsPhase.Cancelled && !_cleaning)
        {
            _roundCancellation.Cancel();
            _performance = CleanUpAfterAsync(_performance);
        }
    }

    private async Task PlayAsync(RpsPerformance performance, CancellationToken cancellationToken)
    {
        try
        {
            await _performAsync(performance, cancellationToken);
            if (!cancellationToken.IsCancellationRequested && !_closing)
                _game.CompletePresentation(performance.RoundId, performance.Cue);
        }
        catch (OperationCanceledException) { if (!_closing) _game.Cancel(RpsCancelReason.Paused); }
        catch (Exception) { if (!_closing) _game.Cancel(RpsCancelReason.PresentationFailed); }
        // Only the next UI timer advances the game; synchronous delegates cannot recursively skip phases.
    }

    private void SettleReward()
    {
        if (_settled || _closing) return;
        _settled = true;
        try
        {
            if (!_game.TryTakeReward(_getLastRewardUtc(), out var claim) || claim is null)
                RewardText.Text = "本局完成。心情奖励还在冷却，开心不用等冷却。";
            else
                RewardText.Text = _tryCommitReward(claim)
                    ? "本局完成 · 心情 +2。下次奖励至少间隔 5 分钟。"
                    : "本局完成；奖励未保存，本次没有额外增加心情。";
        }
        catch (Exception) { RewardText.Text = "本局完成；奖励未保存，没有重复发放。"; }
    }

    /// <summary>The owner calls this before pausing the pet; it forfeits any unfinished round.</summary>
    internal Task CancelForPauseAsync()
    {
        if (_closing) return _performance;
        _paused = true;
        if (_game.IsActive) _game.Cancel(RpsCancelReason.Paused);
        _roundCancellation.Cancel();
        Refresh();
        return _performance;
    }

    internal void ResumeAfterPause() { _paused = false; Refresh(); }

    private async Task CleanUpAfterAsync(Task previous)
    {
        _cleaning = true;
        try { await previous; } catch (Exception) { /* An aborted performance still needs its safe exit. */ }
        await CleanUpAsync();
    }

    private async Task CleanUpAsync()
    {
        if (_cleaned) { _cleaning = false; return; }
        _cleaning = true;
        ReplayButton.IsEnabled = false;
        try
        {
            // Cancellation stops the round, not its teardown. The adapter waits for a safe animation boundary.
            await _performAsync(new(_game.RoundId, RpsCue.ReturnToIdle), CancellationToken.None);
            _cleaned = true;
        }
        catch (Exception)
        {
            DetailText.Text = "收尾动作暂时没有完成，请先退出这一局。";
        }
        finally { _cleaning = false; }
    }

    private async void Window_OnClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        if (_closing) return;
        _closing = true;
        _timer.Stop();
        _game.Cancel(RpsCancelReason.WindowClosed);
        _roundCancellation.Cancel();
        RockButton.IsEnabled = PaperButton.IsEnabled = ScissorsButton.IsEnabled = ReplayButton.IsEnabled = ExitButton.IsEnabled = false;
        StageText.Text = "正在收好这一局…";
        await CleanUpAfterAsync(_performance);
        _allowClose = true;
        // Even a synchronous teardown must let WPF finish the cancelled Closing event first.
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(Close));
    }

    private static string Label(RpsChoice? choice) => choice switch { RpsChoice.Rock => "石头", RpsChoice.Paper => "布", RpsChoice.Scissors => "剪刀", _ => "未出拳" };
    private static string OutcomeTitle(RpsOutcome? outcome) => outcome switch { RpsOutcome.PlayerWin => "你赢了！", RpsOutcome.PetWin => "小鹰赢了！", _ => "平局，默契拉满！" };
    private static string OutcomeLine(RpsOutcome? outcome) => outcome switch { RpsOutcome.PlayerWin => "小鹰：刚才那局不算，我翅膀打滑了。", RpsOutcome.PetWin => "小鹰：不好意思，摸鱼也是有天赋的。", _ => "小鹰：这把不叫平局，叫英雄所见略同。" };
}
