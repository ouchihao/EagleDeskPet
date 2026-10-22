using DuckDeskPet.Core;

int passed = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException(name);
    Console.WriteLine("PASS " + name);
    passed++;
}

void Tick(QuickAccessPolicy policy, double seconds, bool nearPet, bool nearBar, bool expected, string name, bool blocked = false)
{
    bool visible = policy.Update(seconds, nearPet, nearBar, blocked);
    Check(visible == expected && policy.IsVisible == expected, name);
}

QuickAccessPolicy Reveal()
{
    var policy = new QuickAccessPolicy();
    policy.Update(0, nearPet: true, nearBar: false, blocked: false);
    policy.Update(.12, nearPet: true, nearBar: false, blocked: false);
    Check(policy.IsVisible, "A fresh pet dwell reveals the bar");
    return policy;
}

var initial = new QuickAccessPolicy();
var fading = Reveal();
fading.Update(1, false, false, false);
fading.Update(1.5, false, false, false);
Check(!fading.IsVisible, "Leave deadline starts dismissal");
Check(fading.Update(1.55, false, true, false, barStillVisible: true), "Entering a still-visible fading bar restores it immediately");
fading.Reset(dismissUntilLeave: true);
Check(!fading.Update(2, false, true, false, barStillVisible: true), "Explicit dismissal wins over the fading target");
Check(!initial.IsVisible, "The bar starts hidden");
Tick(initial, 0, false, false, false, "An idle pointer keeps the bar hidden");
Tick(initial, 10, false, true, false, "The hidden bar's former position cannot start a reveal");
Tick(initial, 20, false, true, false, "Lingering at the hidden bar's former position never reveals it");

var dwell = new QuickAccessPolicy();
Tick(dwell, 0, true, false, false, "Entering the pet does not reveal immediately");
Tick(dwell, .119, true, false, false, "119 milliseconds of pet dwell keeps the bar hidden");
Tick(dwell, .12, true, false, true, "Exactly 120 milliseconds of pet dwell reveals the bar");
Tick(dwell, 5, true, false, true, "Remaining over the pet keeps the bar visible");

var flyby = new QuickAccessPolicy();
Tick(flyby, 0, true, false, false, "A flyby starts the reveal delay");
Tick(flyby, .08, false, false, false, "Leaving after 80 milliseconds cancels the pending reveal");
Tick(flyby, .12, false, false, false, "A departed pointer cannot cause a delayed flash");
Tick(flyby, 1, false, false, false, "A cancelled reveal stays hidden indefinitely");
Tick(flyby, 2, true, false, false, "Reentering starts a fresh dwell");
Tick(flyby, 2.119, true, false, false, "The previous flyby does not shorten the new dwell");
Tick(flyby, 2.121, true, false, true, "A complete new dwell reveals normally");

var grace = Reveal();
Tick(grace, 1, false, false, true, "Leaving both regions starts a hide grace period");
Tick(grace, 1.419, false, false, true, "The bar stays visible 419 milliseconds after leaving");
Tick(grace, 1.421, false, false, false, "The bar hides after the 420 millisecond grace period");
Tick(grace, 2, false, true, false, "Returning to the now hidden bar's position cannot reopen it");
Tick(grace, 3, false, true, false, "The old bar position cannot accumulate reveal dwell");

var transfer = Reveal();
Tick(transfer, .2, false, true, true, "Moving directly from the pet to the bar keeps it visible");
Tick(transfer, 3, false, true, true, "The visible bar remains interactive while hovered");
Tick(transfer, 4, true, true, true, "Overlapping pet and bar regions keep the bar visible");
Tick(transfer, 5, false, false, true, "Leaving the bar starts a fresh hide delay");
Tick(transfer, 5.421, false, false, false, "Leaving the bar eventually hides it");

foreach (bool returnToPet in new[] { true, false })
{
    string region = returnToPet ? "pet" : "bar";
    var returning = Reveal();
    Tick(returning, 1, false, false, true, "A short gap begins hide grace before returning to the " + region);
    Tick(returning, 1.3, returnToPet, !returnToPet, true, "Returning to the " + region + " cancels pending hide");
    Tick(returning, 2, returnToPet, !returnToPet, true, "The cancelled hide deadline cannot close the hovered " + region);
    Tick(returning, 3, false, false, true, "A second departure starts its own grace period after the " + region);
    Tick(returning, 3.419, false, false, true, "The second departure gets the full grace period after the " + region);
    Tick(returning, 3.421, false, false, false, "The second departure hides after its own deadline after the " + region);
}

var blockedVisible = Reveal();
Tick(blockedVisible, .13, true, true, false, "Blocking immediately closes a visible bar", blocked: true);
Tick(blockedVisible, 5, true, true, false, "Blocking keeps the bar closed while hovered", blocked: true);
Tick(blockedVisible, 6, true, false, false, "Unblocking requires a new pet dwell");
Tick(blockedVisible, 6.119, true, false, false, "Unblocking does not reuse the earlier hover duration");
Tick(blockedVisible, 6.121, true, false, true, "A fresh dwell after unblocking can reveal");

var blockedPending = new QuickAccessPolicy();
Tick(blockedPending, 0, true, false, false, "The pending-reveal blocking scenario begins hidden");
Tick(blockedPending, .08, true, false, false, "Blocking cancels an in-progress reveal", blocked: true);
Tick(blockedPending, .2, false, false, false, "Unblocking elsewhere cannot release a stale reveal");
Tick(blockedPending, 5, false, true, false, "A cancelled reveal cannot reactivate at the old bar position");
Tick(blockedPending, 6, true, false, false, "The pet starts a new dwell after a blocked reveal");
Tick(blockedPending, 6.121, true, false, true, "The cancelled pending reveal does not prevent later use");

foreach (string dismissal in new[] { "action click", "Escape" })
{
    var dismissed = Reveal();
    dismissed.Reset(dismissUntilLeave: true);
    Check(!dismissed.IsVisible, dismissal + " immediately closes the bar");
    Tick(dismissed, 1, true, true, false, dismissal + " suppresses reveal while both regions are hovered");
    Tick(dismissed, 2, true, false, false, dismissal + " stays dismissed while the pointer remains at the pet");
    Tick(dismissed, 3, false, true, false, dismissal + " stays dismissed while the pointer remains at the bar");
    Tick(dismissed, 4, true, false, false, dismissal + " cannot reopen by moving between pet and bar");
    Tick(dismissed, 5, false, false, false, dismissal + " is rearmed only after exiting both regions");
    Tick(dismissed, 6, false, true, false, dismissal + " rearming cannot reveal from the hidden bar position");
    Tick(dismissed, 7, true, false, false, dismissal + " requires a fresh pet dwell after exit");
    Tick(dismissed, 7.119, true, false, false, dismissal + " retains the full reveal delay after exit");
    Tick(dismissed, 7.121, true, false, true, dismissal + " permits a new reveal after exit and dwell");
}

var repeatedDismissal = Reveal();
repeatedDismissal.Reset(dismissUntilLeave: true);
repeatedDismissal.Reset(dismissUntilLeave: true);
Tick(repeatedDismissal, 1, true, false, false, "Repeated dismissal remains suppressed at the pet");
Tick(repeatedDismissal, 2, false, false, false, "Exiting both regions rearms repeated dismissal");
Tick(repeatedDismissal, 3, true, false, false, "Rearmed repeated dismissal starts a new dwell");
repeatedDismissal.Reset(dismissUntilLeave: true);
Tick(repeatedDismissal, 3.2, true, false, false, "Dismissing during a new dwell cancels that pending reveal");
Tick(repeatedDismissal, 4, false, false, false, "A later exit rearms the newly dismissed dwell");
Tick(repeatedDismissal, 5, true, false, false, "Repeated dismissal does not prevent another fresh dwell");
Tick(repeatedDismissal, 5.121, true, false, true, "Repeated dismissal remains reusable after leaving");

var behavior = new PetBehaviorController();
behavior.SetPaused(true);
Check(behavior.IsPaused, "The paused scenario has paused pet animation");
var paused = new QuickAccessPolicy();
Tick(paused, 0, true, false, false, "Paused animation still allows a pet dwell when unblocked");
Tick(paused, .12, true, false, true, "Paused animation alone does not block quick access");
Tick(paused, 1, false, true, true, "Paused animation leaves the visible bar available");

Console.WriteLine($"Quick access policy: {passed}/{passed} assertions passed");
