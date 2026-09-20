using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DuckDeskPet.ClientSetup;
using DuckDeskPet.Core;

namespace DuckDeskPet;

public partial class PetWindow
{
    private readonly List<double> _uiSmokeRpsStarts = new();

    private async Task RunClubUiSmokeAsync(string output, CancellationToken token)
    {
        ValidateExpansionIsolation(output);
        await ExpansionIdleAsync(token);
        string before = ExpansionContentFingerprint();
        OpenCarePanel();
        var care = _carePanel!;
        var pages = (TabControl)care.FindName("CareTabs");
        ExpansionCheck(pages.Items.Count == 3, "Care hub must have three reachable pages.");
        for (int i = 0; i < pages.Items.Count; i++)
        {
            pages.SelectedIndex = i;
            await Task.Delay(240, token);
            care.UpdateLayout();
            ExpansionCheck(pages.SelectedContent is FrameworkElement { IsVisible: true }, "Selected care page is not visible.");
            RenderOwnVisual(care, Path.Combine(output, $"club-care-page-{i}.png"));
        }
        care.SelectSource("Isolated demo AI");
        ExpansionCheck(pages.SelectedIndex == 2, "Return-app configuration did not navigate to message settings.");
        care.Width = 400; care.Height = 550; pages.SelectedIndex = 0;
        await Task.Delay(240, token);
        RenderOwnVisual(care, Path.Combine(output, "club-care-small.png"));
        care.Close();

        OpenShopWindow();
        await Task.Delay(240, token);
        var shop = _shopWindow!;
        var cards = (ListBox)shop.FindName("CardsItems");
        ExpansionCheck(cards.Items.Count is > 1 and <= 8, "Shop must be a bounded multi-item grid, not a long list.");
        ExpansionCheck(shop.SelectItem(ContentCatalog.HoodieOutfitId), "Paged shop cannot reach the hoodie.");
        await Task.Delay(180, token);
        RenderOwnVisual(shop, Path.Combine(output, "club-shop-hoodie.png"));
        ((RadioButton)shop.FindName("DeskCategory")).IsChecked = true;
        await Task.Delay(180, token);
        RenderOwnVisual(shop, Path.Combine(output, "club-shop-desks.png"));
        ExpansionCheck(cards.Items.Count <= 4, "Desk category includes non-desk products.");
        shop.Close();

        OpenHonorWall();
        await Task.Delay(250, token);
        ExpansionCheck(HonorCatalog.Definitions.Count == 18, "Expected eighteen honors including all old IDs.");
        RenderOwnVisual(_honorWall!, Path.Combine(output, "club-honor-gallery.png"));
        _honorWall!.Close();
        OpenGitHubWindow();
        await Task.Delay(250, token);
        RenderOwnVisual(_githubWindow!, Path.Combine(output, "club-github-empty.png"));
        _githubWindow!.Close();
        OpenTaskInbox();
        await Task.Delay(250, token);
        RenderOwnVisual(_taskInboxWindow!, Path.Combine(output, "club-tasks-empty.png"));
        _taskInboxWindow!.Close();

        // Never inspect the real user's AI home even when only taking a preview.
        string fakeHome = Path.Combine(output, "club-client-home");
        Directory.CreateDirectory(fakeHome);
        var setup = new ClientSetupWindow(new ClientSetupService(fakeHome, AppContext.BaseDirectory)) { Owner = this };
        try
        {
            setup.Show(); await setup.PendingOperation.WaitAsync(token);
            await Task.Delay(240, token);
            RenderOwnVisual(setup, Path.Combine(output, "club-ai-setup.png"));
            ExpansionCheck(!Directory.EnumerateFiles(fakeHome, "*", SearchOption.AllDirectories).Any(), "AI setup preview wrote configuration files.");
        }
        finally { setup.Close(); }

        // Exercise production transition code on a detached surface: system-style
        // reduced motion is instantaneous and repeated transitions cannot queue.
        var transform = new ScaleTransform(.9, .9);
        var surface = new Border { RenderTransform = transform, Opacity = .8 };
        PetUiMotion.Reveal(surface, motionEnabled: false);
        ExpansionCheck(ReferenceEquals(surface.RenderTransform, transform) && Math.Abs(surface.Opacity - .8) < .001 && !surface.HasAnimatedProperties,
            "Reduced-motion mode changed or hid content.");
        PetUiMotion.Reveal(surface, motionEnabled: true);
        PetUiMotion.Reveal(surface, motionEnabled: true);
        PetUiMotion.Stop(surface);
        ExpansionCheck(ReferenceEquals(surface.RenderTransform, transform) && !surface.HasAnimatedProperties && Math.Abs(surface.Opacity - .8) < .001,
            "Repeated navigation leaked an animation or lost the existing transform.");
        PetUiMotion.Reveal(surface, motionEnabled: true);
        await Task.Delay(260, token);
        ExpansionCheck(ReferenceEquals(surface.RenderTransform, transform) && !surface.HasAnimatedProperties && Math.Abs(surface.Opacity - .8) < .001,
            "Completed transition did not restore its visual state.");
        ExpansionCheck(before == ExpansionContentFingerprint(), "Read-only UI navigation changed wallet, ownership or equipment.");
    }
}
