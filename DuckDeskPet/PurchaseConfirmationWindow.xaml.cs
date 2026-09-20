using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DuckDeskPet.Core;

namespace DuckDeskPet;

/// <summary>A modal, read-only purchase question. Only its explicit true result authorizes the
/// caller to re-quote and attempt a durable transaction; displaying it never spends anything.</summary>
public partial class PurchaseConfirmationWindow : Window
{
    internal PurchaseConfirmationWindow(ContentDefinition item, decimal balance)
    {
        ArgumentNullException.ThrowIfNull(item);
        InitializeComponent();
        ProductImage.Source = ShopThumbnails.Get(item);
        ProductName.Text = item.Name;
        ProductPrice.Text = $"{item.Price:N2} 鹰币 · 永久收藏";
        CurrentBalance.Text = $"{balance:N2} 鹰币";
        RemainingBalance.Text = balance >= item.Price ? $"{balance - item.Price:N2} 鹰币" : "鹰币不足";
        PurchaseNote.Text = EquipmentPresentation.Describe(item.Bonuses) + "\n购买不自动装备；装备实际生效后才提供加成。";
        ConfirmButton.IsEnabled = balance >= item.Price && !item.IsDefault && ProductImage.Source is not null;
        if (!ConfirmButton.IsEnabled) PurchaseNote.Text = "余额或商品素材未就绪，这次不能购买，不会扣除鹰币。";
        Loaded += (_, _) => CancelButton.Focus();
    }

    private void Confirm_OnClick(object sender, RoutedEventArgs e)
    {
        if (ConfirmButton.IsEnabled) DialogResult = true;
    }
    private void Cancel_OnClick(object sender, RoutedEventArgs e) => DialogResult = false;
    private void Window_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { e.Handled = true; DialogResult = false; }
        else if (e.Key == Key.Enter)
        {
            e.Handled = true;
            if (CancelButton.IsKeyboardFocused || CloseButton.IsKeyboardFocused) DialogResult = false;
            else if (ConfirmButton.IsEnabled) DialogResult = true;
        }
    }
    private void Title_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || CloseButton.IsMouseOver) return;
        try { DragMove(); } catch (InvalidOperationException) { /* Pointer released between events. */ }
    }
}
