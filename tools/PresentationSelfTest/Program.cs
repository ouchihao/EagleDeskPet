using DuckDeskPet;
using DuckDeskPet.Core;

int passed = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    passed++;
    Console.WriteLine("PASS " + message);
}
ClipSample Terminal(ClipKind kind, long sequence) => new(kind, ClipPlaybackPhase.Terminal, 1, sequence);
var gate = new PresentationAcknowledgementGate(11);
var eat = Terminal(ClipKind.Eat, 5);
Check(gate.CanLeave(ClipSample.Idle), "Idle does not require a terminal receipt");
Check(!gate.Acknowledge(11, 5, ClipKind.Eat), "Unsent terminal cannot authorize completion");
gate.Sent(new(ClipKind.Eat, ClipPlaybackPhase.Playing, 0.9, 5));
Check(!gate.Acknowledge(11, 5, ClipKind.Eat), "Predicted final pose does not precede the owner's terminal state");
gate.Sent(eat);
Check(!gate.CanLeave(eat), "The owner waits at its complete terminal pose");
Check(!gate.Acknowledge(10, 5, ClipKind.Eat), "Old browser generation cannot complete a meal");
Check(!gate.Acknowledge(11, 4, ClipKind.Eat), "Previous sequence cannot complete a later meal");
Check(!gate.Acknowledge(11, 5, ClipKind.Yawn), "Wrong action cannot complete a meal");
Check(gate.Acknowledge(11, 5, ClipKind.Eat) && gate.CanLeave(eat), "An actually presented terminal releases exactly its sequence");
Check(!gate.Acknowledge(11, 5, ClipKind.Eat), "Duplicate terminal receipt has no additional effect");
var work = Terminal(ClipKind.WorkLoop, 6);
gate.Sent(work);
Check(!gate.CanLeave(work), "Continuous work loops also wait for the real end pose");
Check(gate.Acknowledge(11, 6, ClipKind.WorkLoop) && gate.CanLeave(work), "Work loop can continue without an inserted idle");
var replacement = new PresentationAcknowledgementGate(12);
replacement.Sent(work);
Check(!replacement.Acknowledge(11, 6, ClipKind.WorkLoop) && !replacement.CanLeave(work), "Replacement host rejects the old host's in-flight callback");
var behavior = new PetBehaviorController();
behavior.SuspendAutomatic(true);
behavior.QueueReaction(PetBehaviorKind.Fed);
for (int i = 0; i < 600 && behavior.CurrentSample.Phase != ClipPlaybackPhase.Terminal; i++) behavior.Advance(1d / 60);
Check(behavior.CurrentSample.Kind == ClipKind.Eat && behavior.CurrentSample.Phase == ClipPlaybackPhase.Terminal,
    "Business timeline reaches the meal's terminal without prematurely returning idle");
var controlled = new PresentationAcknowledgementGate(20);
controlled.Sent(behavior.CurrentSample);
for (int i = 0; i < 120; i++) if (controlled.CanLeave(behavior.CurrentSample)) behavior.Advance(1d / 60);
Check(behavior.CurrentSample.Kind == ClipKind.Eat && behavior.CurrentSample.Phase == ClipPlaybackPhase.Terminal,
    "Two seconds without display acknowledgement cannot finish a consumed meal");
controlled.Acknowledge(20, behavior.CurrentSample.Sequence, ClipKind.Eat);
if (controlled.CanLeave(behavior.CurrentSample)) behavior.Advance(1d / 60);
Check(behavior.CurrentSample.Kind == ClipKind.Idle, "A confirmed full meal returns to the existing idle beat");
Console.WriteLine($"Presentation acknowledgement: {passed}/{passed}");
