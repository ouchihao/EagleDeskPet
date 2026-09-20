using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
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
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(40) };
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
        RewardText.Text = "每 5 分钟最多心情 +2 · 无经验或鹰币奖励";
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
    private void Title_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed && e.OriginalSource is not Button)
        {
            try { DragMove(); } catch (InvalidOperationException) { /* The pointer may already have been released. */ }
        }
    }
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
        RoundProgress.Value = state.Phase switch
        {
            RpsPhase.Preparing => .15,
            RpsPhase.Throwing => .45,
            RpsPhase.Reacting => .8,
            RpsPhase.Completed => 1,
            _ => 0,
        };
        PlayerPlaceholder.Visibility = state.PlayerChoice.HasValue ? Visibility.Collapsed : Visibility.Visible;
        PlayerHand.Visibility = state.PlayerChoice.HasValue ? Visibility.Visible : Visibility.Collapsed;
        if (state.PlayerChoice is { } hand) PlayerHand.Data = (Geometry)FindResource(hand + "Hand");
        // Deliberately keep the pet's hand secret throughout preparation AND the
        // authored throw. A panel label must not spoil the desktop performance.
        PetHandLabel.Text = state.Phase is RpsPhase.Reacting or RpsPhase.Completed ? Label(state.PetChoice) : "已选好";
        (StageText.Text, DetailText.Text) = state.Phase switch
        {
            RpsPhase.AwaitingChoice => ("小鹰已选好，轮到你了！", "选一拳，它可不能偷看后改拳。"),
            RpsPhase.Preparing => ("石头、剪刀、布——", $"{Label(state.PlayerChoice)}已锁定，准备出招！"),
            RpsPhase.Throwing => ("出拳！", "看旁边的小鹰，结果马上揭晓。"),
            RpsPhase.Reacting or RpsPhase.Completed => (OutcomeTitle(state.Outcome),
                OutcomeLine(state.Outcome)),
            RpsPhase.Cancelled => ("这一局先收摊", state.Cancellation switch
            {
                RpsCancelReason.Paused => "先收好动作，本局不结算奖励。",
                RpsCancelReason.TimedOut => "刚才停留太久，本局不结算奖励。",
                RpsCancelReason.PresentationFailed => "动作没接上，本局不结算奖励。",
                _ => "本局已取消，没有发放奖励。",
            }),
            _ => ("小鹰正在偷偷选拳…", "不用下注，也不会扣饭。"),
        };
        if (_presentedPhase == state.Phase) return;
        _presentedPhase = state.Phase;
        AnimateStage();
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

    private void AnimateStage()
    {
        if (!SystemParameters.ClientAreaAnimation) return;
        // Only the panel's text/stage eases in; never fade, flip or retime the
        // actual eagle sprite. Every authored action frame still plays intact.
        StageSurface.BeginAnimation(OpacityProperty, new DoubleAnimation(.72, 1, TimeSpan.FromMilliseconds(140)));
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
                RewardText.Text = "奖励还在冷却，开心不用等冷却。";
            else
                RewardText.Text = _tryCommitReward(claim)
                    ? "心情 +2！下一份奖励 5 分钟后再来。"
                    : "奖励未保存，本次没有额外增加心情。";
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
        RockButton.IsEnabled = PaperButton.IsEnabled = ScissorsButton.IsEnabled = ReplayButton.IsEnabled = ExitButton.IsEnabled = TitleCloseButton.IsEnabled = false;
        StageText.Text = "正在收好这一局…";
        await CleanUpAfterAsync(_performance);
        _allowClose = true;
        // Even a synchronous teardown must let WPF finish the cancelled Closing event first.
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(Close));
    }

    private static string Label(RpsChoice? choice) => choice switch { RpsChoice.Rock => "石头", RpsChoice.Paper => "布", RpsChoice.Scissors => "剪刀", _ => "未出拳" };
    private static string OutcomeTitle(RpsOutcome? outcome) => outcome switch { RpsOutcome.PlayerWin => "你赢了！", RpsOutcome.PetWin => "小鹰赢了！", _ => "平局，默契拉满！" };
    private static string OutcomeLine(RpsOutcome? outcome) => outcome switch { RpsOutcome.PlayerWin => "这把不算，我翅膀打滑了。", RpsOutcome.PetWin => "不好意思，摸鱼也是有天赋的。", _ => "英雄所见略同，再来一把？" };
}
