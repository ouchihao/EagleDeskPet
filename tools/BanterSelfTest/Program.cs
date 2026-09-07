using DuckDeskPet;

int passed = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException(name);
    Console.WriteLine($"PASS {++passed}: {name}");
}
string? Minute(OfficeBanter banter, bool blocked = false)
{
    for (int i = 0; i < 59; i++)
        if (banter.Tick(1, blocked) is not null) throw new InvalidOperationException("Early chatter");
    return banter.Tick(1, blocked);
}

Check(OfficeBanter.Lines.Count == 60 && OfficeBanter.Lines.Distinct(StringComparer.Ordinal).Count() == 60,
    "Exactly 60 unique quips");
Check(OfficeBanter.Lines.All(line => line.Length <= 22 && !line.Contains('\n')),
    "Every quip fits a compact bubble");

var clock = new OfficeBanter(new Random(1));
Check(Minute(clock) is not null, "One quip at 60 active seconds");
Check(Minute(clock, blocked: true) is null && clock.Tick(1, false) is null,
    "Blocked minute is discarded, not queued");

var toggled = new OfficeBanter(new Random(2));
for (int i = 0; i < 45; i++) toggled.Tick(1, false);
toggled.Enabled = false;
Check(Minute(toggled) is null, "Disabled chatter stays silent");
toggled.Enabled = true;
Check(Minute(toggled) is not null, "Re-enabling starts a fresh minute");

var sleeping = new OfficeBanter(new Random(3));
for (int i = 0; i < 45; i++) sleeping.Tick(1, false);
Check(sleeping.Tick(3600, false) is null && Minute(sleeping) is not null,
    "Sleep skips elapsed backlog and starts a new minute");

var shuffled = new OfficeBanter(new Random(4));
var firstBag = Enumerable.Range(0, 60).Select(_ => Minute(shuffled)).ToArray();
var secondBag = Enumerable.Range(0, 60).Select(_ => Minute(shuffled)).ToArray();
Check(firstBag.Distinct().Count() == 60 && secondBag.Distinct().Count() == 60 && firstBag[^1] != secondBag[0],
    "Shuffle bags exhaust all quips without boundary repetition");

var invalid = new OfficeBanter(new Random(5));
Check(invalid.Tick(double.NaN, false) is null && invalid.Tick(double.PositiveInfinity, false) is null &&
    invalid.Tick(-1, false) is null && Minute(invalid) is not null,
    "Invalid elapsed time is safely reset");

Console.WriteLine($"All {passed} banter tests passed.");
