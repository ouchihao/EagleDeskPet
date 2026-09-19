using DuckDeskPet;

int passed = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException(name);
    Console.WriteLine($"PASS {++passed}: {name}");
}
string? Minute(OfficeBanter banter, bool blocked = false, BanterContext context = BanterContext.Ordinary)
{
    for (int i = 0; i < 59; i++)
        if (banter.Tick(1, blocked, context) is not null) throw new InvalidOperationException("Early chatter");
    return banter.Tick(1, blocked, context);
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

var expectedContexts = new Dictionary<BanterContext, string[]>
{
    [BanterContext.Hungry] = ["空碗也是碗，怎么就没饭。", "肚子在开会，议题只有吃饭。", "画饼不管饱，真饭来一口。", "鹰已饿扁，申请投喂。"],
    [BanterContext.LowMood] = ["快乐库存告急，摸摸能补货吗。", "今天的班，把鹰都上蔫了。", "让我先丧两秒，第三秒再营业。", "我没生气，我在内心翻白眼。"],
    [BanterContext.Working] = ["键盘敲得响，鹰币慢慢涨。", "这不是上班，这是攒桌子基金。", "电脑在发热，本鹰在挣钱。", "认真打字，偷偷想饭。"],
    [BanterContext.LateNight] = ["这么晚了，保存一下再睡吧。", "月亮都打卡了，你还没下班。", "别熬鹰了，明天还能摸鱼。", "困意已送达，记得签收。"],
};
Check(expectedContexts.Values.SelectMany(x => x).Distinct(StringComparer.Ordinal).Count() == 16 &&
    expectedContexts.Values.SelectMany(x => x).All(line => line.Length <= 22 && !line.Any(char.IsControl) && !OfficeBanter.Lines.Contains(line)),
    "Sixteen contextual quips are distinct, compact and separate from sixty ordinary lines");

foreach (var (context, expected) in expectedContexts)
{
    var contextual = new OfficeBanter(new Random(100 + (int)context));
    var choices = Enumerable.Range(0, 64).Select(_ => Minute(contextual, context: context)!).ToArray();
    Check(choices.All(expected.Contains) && choices.Distinct(StringComparer.Ordinal).Count() == expected.Length,
        $"{context}: only its own four lines are selected and all are reachable");
    Check(choices.Zip(choices.Skip(1)).All(pair => pair.First != pair.Second),
        $"{context}: consecutive contextual lines never repeat");
    var repeat = new OfficeBanter(new Random(100 + (int)context));
    Check(choices.SequenceEqual(Enumerable.Range(0, 64).Select(_ => Minute(repeat, context: context))),
        $"{context}: same seed and ticks reproduce exactly the same selections");

    var blocked = new OfficeBanter(new Random(200 + (int)context));
    var control = new OfficeBanter(new Random(200 + (int)context));
    Check(Minute(blocked, true, context) is null && blocked.Tick(1, false, context) is null,
        $"{context}: blocked minute is skipped without immediate replay");
    for (int i = 0; i < 58; i++) blocked.Tick(1, false, context);
    Check(blocked.Tick(1, false, context) == Minute(control, context: context),
        $"{context}: skipped bubble consumes neither random selection nor history");
}

var changing = new OfficeBanter(new Random(301));
for (int i = 0; i < 59; i++) changing.Tick(1, false, BanterContext.Working);
Check(expectedContexts[BanterContext.Hungry].Contains(changing.Tick(1, false, BanterContext.Hungry)),
    "Context at the shared minute boundary selects the line; there is no per-context timer");
Check(changing.Tick(1, false, BanterContext.LateNight) is null && changing.Tick(1, false, BanterContext.LowMood) is null,
    "Changing contexts cannot create an extra bubble immediately after the minute");

var mixed = new OfficeBanter(new Random(302));
var ordinaryControl = new OfficeBanter(new Random(302));
Check(Minute(mixed) == Minute(ordinaryControl), "Mixed run starts with the same seeded ordinary bag");
foreach (BanterContext context in expectedContexts.Keys) Minute(mixed, context: context);
Check(Enumerable.Range(0, 59).Select(_ => Minute(mixed)).SequenceEqual(Enumerable.Range(0, 59).Select(_ => Minute(ordinaryControl))),
    "Contextual turns do not consume or reorder the remaining ordinary shuffle bag");

var contextualToggle = new OfficeBanter(new Random(303));
for (int i = 0; i < 59; i++) contextualToggle.Tick(1, false, BanterContext.Hungry);
contextualToggle.Enabled = false;
Check(Minute(contextualToggle, context: BanterContext.Working) is null, "Disabled context changes stay silent");
contextualToggle.Enabled = true;
Check(contextualToggle.Tick(1, false, BanterContext.LateNight) is null, "Re-enable does not replay a nearly-due contextual line");
for (int i = 0; i < 58; i++) contextualToggle.Tick(1, false, BanterContext.LateNight);
Check(expectedContexts[BanterContext.LateNight].Contains(contextualToggle.Tick(1, false, BanterContext.LateNight)),
    "Re-enabled context requires a fresh complete minute");

var contextualSleep = new OfficeBanter(new Random(304));
for (int i = 0; i < 59; i++) contextualSleep.Tick(1, false, BanterContext.LowMood);
Check(contextualSleep.Tick(3600, false, BanterContext.Working) is null &&
    expectedContexts[BanterContext.Working].Contains(Minute(contextualSleep, context: BanterContext.Working)),
    "Sleep discards the old context deadline instead of replaying missed working lines");

var unknown = new OfficeBanter(new Random(305));
Check(OfficeBanter.Lines.Contains(Minute(unknown, context: (BanterContext)999)!),
    "Unknown context safely falls back to ordinary quips");

Console.WriteLine($"All {passed} banter tests passed.");
