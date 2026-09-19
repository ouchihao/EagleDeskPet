using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DuckDeskPet.Core;

namespace DuckDeskPet;

internal readonly record struct ShopActionResult(bool Success, bool Changed, string Message);

/// <summary>UI transaction coordinator: clone -> validate -> durable save -> publish, never the reverse.</summary>
internal sealed class ShopTransactions(PetStore store, Func<PetState> state,
    Func<ContentDefinition, bool> isAvailable, Func<ContentOwnershipState, bool>? isCompatible = null)
{
    internal PetState State => state();
    internal bool CanSave => store.CanSave;
    internal bool IsAvailable(ContentDefinition item)
    {
        try { return isAvailable(item); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException or NotSupportedException) { return false; }
    }
    internal ContentOperationResult Quote(string id) => ContentOwnershipService.QuotePurchase(State.Content, id, State, IsAvailable);

    internal ShopActionResult Purchase(string id)
    {
        var quote = Quote(id);
        if (!quote.CanPurchase || quote.Definition is null) return new(false, false, quote.Message);
        var live = State;
        var result = store.TryTransaction(live, ContentCatalog.PurchaseTransactionId(id), quote.Definition.Price,
            candidate => ContentOwnershipService.GrantPurchase(candidate.Content, id, candidate, IsAvailable).Changed);
        return new(result.Success, result.Changed, result.Changed ? "已放进收藏。装备或播放请再点一次，不会自动改掉当前外观。" : result.Message);
    }

    internal ShopActionResult ClaimReward(string id)
    {
        if (!ContentCatalog.TryGet(id, out var item)) return new(false, false, "没有找到这份奖励。");
        var live = State;
        if (ContentOwnershipService.Owns(live.Content, id)) return new(true, false, "这份奖励已经拥有，不会重复发放或退还鹰币。");
        if (!ContentOwnershipService.IsRewardEligible(item!, live)) return new(false, false, "荣誉条件还未达成。");
        var candidate = live.CreateSnapshot();
        var grants = ContentOwnershipService.GrantEligibleRewards(candidate.Content, candidate);
        if (grants.Count == 0) return new(true, false, "奖励已经在收藏中。");
        if (!store.Save(candidate)) return new(false, false, store.Warning ?? "奖励未保存，请稍后再试。");
        live.ApplySnapshot(candidate);
        return new(true, true, IsAvailable(item!) ? "已领取达成的荣誉奖励，去收藏看看吧。" : "奖励权益已保存；素材暂未就绪，恢复后可直接使用。");
    }

    internal ShopActionResult Equip(string id, bool defer)
    {
        var live = State; var candidate = live.CreateSnapshot();
        var result = ContentOwnershipService.RequestEquip(candidate.Content, id, defer, IsAvailable);
        if (!result.Changed) return new(result.Status == ContentOperationStatus.NoChange, false, result.Message);
        if (isCompatible is not null && !isCompatible(candidate.Content)) return new(false, false, "这组桌子与电脑的插槽不兼容，尚未更改装备。");
        if (!store.Save(candidate)) return new(false, false, store.Warning ?? "装备选择未保存，仍使用原来的选择。");
        live.ApplySnapshot(candidate);
        return new(true, true, result.Status == ContentOperationStatus.Pending ? "已保存选择，当前工作或动作完整结束后生效。" : result.Message);
    }

    internal ShopActionResult ApplyPending()
    {
        var live = State; var candidate = live.CreateSnapshot();
        var results = ContentOwnershipService.ApplyPendingAtSafeBoundary(candidate.Content, IsAvailable);
        if (!results.Any(x => x.Changed)) return new(true, false, results.FirstOrDefault().Message ?? "没有待生效的更换。");
        if (isCompatible is not null && !isCompatible(candidate.Content)) return new(false, false, "待生效的工位组合不兼容，原装备和选择均已保留。");
        if (!store.Save(candidate)) return new(false, false, store.Warning ?? "换装未保存，仍使用原来的选择。");
        live.ApplySnapshot(candidate);
        return new(true, true, "收藏中的新装备已生效。");
    }
}

public partial class ShopWindow : Window
{
    private readonly ShopTransactions _transactions;
    private readonly Func<string, Task<ShopActionResult>> _equipAsync;
    private readonly Func<string, Task> _previewAsync, _playAsync;
    private readonly Func<bool> _interactionBusy;
    private readonly Func<ContentDefinition, bool> _confirmPurchase;
    private bool _ready, _busy, _closed;
    private string? _signature;
    private Task _operation = Task.CompletedTask;
    internal Task PendingOperation => _operation;

    public static readonly DependencyProperty CardWidthProperty = DependencyProperty.Register(
        nameof(CardWidth), typeof(double), typeof(ShopWindow), new PropertyMetadata(340.0));
    public double CardWidth { get => (double)GetValue(CardWidthProperty); private set => SetValue(CardWidthProperty, value); }

    internal ShopWindow(ShopTransactions transactions, Func<string, Task<ShopActionResult>> equipAsync,
        Func<string, Task> previewAsync, Func<string, Task> playAsync, Func<bool> interactionBusy,
        bool ownedOnly = false, Func<ContentDefinition, bool>? confirmPurchase = null)
    {
        _transactions = transactions; _equipAsync = equipAsync; _previewAsync = previewAsync;
        _playAsync = playAsync; _interactionBusy = interactionBusy;
        _confirmPurchase = confirmPurchase ?? (item => MessageBox.Show(this,
            $"用 {item.Price} 鹰币收藏「{item.Name}」？\n购买后永久拥有，不会自动装备，也不支持退款。",
            "确认收藏", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK);
        InitializeComponent();
        Width = Math.Min(Width, Math.Max(MinWidth, SystemParameters.WorkArea.Width - 32));
        Height = Math.Min(Height, Math.Max(MinHeight, SystemParameters.WorkArea.Height - 32));
        _ready = true; SelectOwned(ownedOnly);
        Closed += (_, _) => _closed = true;
        Loaded += (_, _) => UpdateCardWidth();
    }

    internal void SelectOwned(bool ownedOnly)
    {
        OwnedFilter.IsChecked = ownedOnly; ShopFilter.IsChecked = !ownedOnly; Refresh();
    }
    internal void Refresh()
    {
        if (!_ready || _closed) return;
        var state = _transactions.State;
        var cards = ContentCatalog.Definitions.OrderBy(item => OwnedFilter.IsChecked != true && item.IsDefault ? 1 : 0)
            .Select(item => new ShopCard(item, state, _transactions, _interactionBusy(), _busy)).ToArray();
        string signature = $"{state.Coins}|{OwnedFilter.IsChecked}|{_busy}|" + string.Join("|", cards.Select(x => x.Signature));
        if (signature == _signature) return;
        _signature = signature;
        BalanceText.Text = state.Coins.ToString("N0");
        CardsItems.ItemsSource = cards.Where(x => OwnedFilter.IsChecked != true || x.Owned).ToArray();
        EmptyText.Visibility = CardsItems.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private void Filter_OnChecked(object sender, RoutedEventArgs e) { if (_ready) Refresh(); }
    private void Window_OnSizeChanged(object sender, SizeChangedEventArgs e) { if (_ready) UpdateCardWidth(); }
    private void UpdateCardWidth()
    {
        double available = Math.Max(300, CardScroll.ActualWidth - 38);
        CardWidth = Math.Floor(available / (available >= 620 ? 2 : 1));
    }
    private void Close_OnClick(object sender, RoutedEventArgs e) => Close();
    private void Window_OnPreviewKeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Escape) { e.Handled = true; Close(); } }
    private void Preview_OnClick(object sender, RoutedEventArgs e)
    {
        if (_busy || sender is not Button { Tag: string id } || !ContentCatalog.TryGet(id, out var item) || !_transactions.IsAvailable(item!)) return;
        _operation = RunAsync(async () => { await _previewAsync(id); return "预览仅供欣赏，没有扣款、领奖或更换装备。"; });
    }
    private void Action_OnClick(object sender, RoutedEventArgs e)
    {
        if (_busy || sender is not Button { Tag: string id }) return;
        _operation = RunAsync(async () =>
        {
            var item = ContentCatalog.Get(id); var state = _transactions.State;
            if (ContentOwnershipService.Owns(state.Content, id))
            {
                if (item.Type == ContentType.Action)
                {
                    if (_interactionBusy()) return "先等当前工作或互动完整结束吧。";
                    var playable = ContentOwnershipService.CanPlayAction(state.Content, id, _transactions.IsAvailable);
                    if (!playable.CanPlay) return playable.Message;
                    await _playAsync(id); return "已播放收藏动作，不会加入自动待机或重复发奖。";
                }
                return (await _equipAsync(id)).Message;
            }
            var quote = _transactions.Quote(id);
            if (quote.Status == ContentOperationStatus.RewardAvailable) return _transactions.ClaimReward(id).Message;
            if (!quote.CanPurchase) return quote.Message;
            if (!_confirmPurchase(item)) return "已取消，没有扣除鹰币。";
            // Confirmation can pump WPF messages; re-quote and revalidate inside the transaction.
            return _transactions.Purchase(id).Message;
        });
    }
    private async Task RunAsync(Func<Task<string>> operation)
    {
        _busy = true; Refresh();
        try { string message = await operation(); if (!_closed) StatusText.Text = message; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { if (!_closed) StatusText.Text = "这次操作没有完成；请检查素材或存档状态后再试。"; }
        finally { _busy = false; Refresh(); }
    }

    private sealed class ShopCard
    {
        internal ShopCard(ContentDefinition item, PetState state, ShopTransactions transactions, bool interactionBusy, bool busy)
        {
            Id = item.Id; Name = item.Name; Description = item.Description;
            Owned = ContentOwnershipService.Owns(state.Content, item.Id);
            bool available = transactions.IsAvailable(item);
            bool earned = ContentOwnershipService.IsRewardEligible(item, state);
            bool equipped = item.Slot is { } slot && state.Content.Equipped.GetValueOrDefault(slot, ContentCatalog.DefaultForSlot(slot)) == item.Id;
            bool pending = item.Slot is { } pendingSlot && state.Content.PendingEquipment.GetValueOrDefault(pendingSlot) == item.Id;
            bool otherPending = item.Slot is { } currentSlot && state.Content.PendingEquipment.ContainsKey(currentSlot) && !pending;
            Category = item.Type switch { ContentType.Desk => "工位 · 桌子", ContentType.Computer => "工位 · 电脑", ContentType.Outfit => "形象 · 全套装扮", _ => "互动 · 手动动作" };
            PriceLabel = item.IsDefault ? "默认免费" : Owned ? "已拥有" : $"{item.Price} 鹰币";
            Acquisition = item.IsDefault ? "每只小鹰都有，不用购买。" : item.Reward is not null
                ? $"{item.Price} 鹰币购买，或{item.Reward.Description}。" : $"工作积累鹰币，{item.Price} 鹰币永久收藏。";
            Status = !available ? Owned ? "已拥有，资源暂未就绪；权益和选择仍会保留。" : "资源尚未就绪，暂不售卖；不会扣款。"
                : pending ? "待生效 · 当前工作或动作结束后更换。"
                : equipped ? otherPending ? "当前正在用；可以取消排队，保留这一款。" : "当前正在用。"
                : Owned ? item.Type == ContentType.Action ? "已解锁 · 点击主动播放，不加入自动待机。" : "已收藏 · 点击装备后才会更换。"
                : earned ? "荣誉条件已达成，可免费领取，不用购买。"
                : state.Coins < item.Price ? $"还差 {item.Price - state.Coins} 鹰币。" + (item.Reward is null ? "" : "也可以达成上方荣誉免费解锁。")
                : "尚未拥有 · 购买前可以独立预览。";
            ActionLabel = Owned ? item.Type == ContentType.Action ? "来一段" : pending ? "等待生效" : equipped ? otherPending ? "保留当前" : "正在使用" : "装备"
                : earned ? "免费领取" : available ? $"{item.Price} 币收藏" : "素材准备中";
            CanPreview = available && !busy;
            CanAct = !busy && transactions.CanSave && (Owned
                ? available && (item.Type == ContentType.Action ? !interactionBusy : !pending && (!equipped || otherPending))
                : earned || (available && state.Coins >= item.Price));
            AccessibleLabel = $"{Name}，{PriceLabel}。{Acquisition} {Status}";
            Signature = $"{Id}:{Owned}:{Status}:{ActionLabel}:{CanPreview}:{CanAct}";
        }
        public string Id { get; }
        public string Name { get; }
        public string Description { get; }
        public string Category { get; }
        public string PriceLabel { get; }
        public string Acquisition { get; }
        public string Status { get; }
        public string ActionLabel { get; }
        public bool Owned { get; }
        public bool CanPreview { get; }
        public bool CanAct { get; }
        public string AccessibleLabel { get; }
        internal string Signature { get; }
    }
}
