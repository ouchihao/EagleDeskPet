namespace DuckDeskPet;

internal enum BanterContext { Ordinary, Hungry, LowMood, Working, LateNight }

/// <summary>Low-priority local chatter. The caller supplies elapsed time and UI availability.</summary>
internal sealed class OfficeBanter
{
    internal const double IntervalSeconds = 60;
    private const double MaximumContinuousTickSeconds = 5;
    private readonly Random _random;
    private readonly int[] _bag = Enumerable.Range(0, Lines.Count).ToArray();
    private int _next;
    private int _lastLine = -1;
    private double _elapsed;
    private bool _enabled = true;
    private string? _lastContextLine;

    internal OfficeBanter(Random? random = null)
    {
        _random = random ?? Random.Shared;
        _next = _bag.Length;
    }

    internal bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value) return;
            _enabled = value;
            _elapsed = 0;
        }
    }

    /// <summary>
    /// Call approximately once a second with time since the previous tick.
    /// An occupied bubble skips this minute; sleep/stalls never create a backlog.
    /// </summary>
    internal string? Tick(double elapsedSeconds, bool blocked, BanterContext context = BanterContext.Ordinary)
    {
        if (!_enabled) return null;
        if (!double.IsFinite(elapsedSeconds) || elapsedSeconds < 0 || elapsedSeconds > MaximumContinuousTickSeconds)
        {
            _elapsed = 0;
            return null;
        }

        _elapsed += elapsedSeconds;
        if (_elapsed < IntervalSeconds) return null;
        _elapsed = 0;
        if (blocked) return null;

        if (context != BanterContext.Ordinary && ContextLines.TryGetValue(context, out var contextual))
        {
            var choices = contextual.Where(x => x != _lastContextLine).ToArray();
            return _lastContextLine = choices[_random.Next(choices.Length)];
        }

        if (_next == _bag.Length) ShuffleBag();
        _lastLine = _bag[_next++];
        return Lines[_lastLine];
    }

    private void ShuffleBag()
    {
        for (int i = _bag.Length - 1; i > 0; i--)
        {
            int j = _random.Next(i + 1);
            (_bag[i], _bag[j]) = (_bag[j], _bag[i]);
        }
        if (_bag[0] == _lastLine)
        {
            int j = _random.Next(1, _bag.Length);
            (_bag[0], _bag[j]) = (_bag[j], _bag[0]);
        }
        _next = 0;
    }

    private static readonly IReadOnlyDictionary<BanterContext, string[]> ContextLines = new Dictionary<BanterContext, string[]>
    {
        [BanterContext.Hungry] = ["空碗也是碗，怎么就没饭。", "肚子在开会，议题只有吃饭。", "画饼不管饱，真饭来一口。", "鹰已饿扁，申请投喂。"],
        [BanterContext.LowMood] = ["快乐库存告急，摸摸能补货吗。", "今天的班，把鹰都上蔫了。", "让我先丧两秒，第三秒再营业。", "我没生气，我在内心翻白眼。"],
        [BanterContext.Working] = ["键盘敲得响，鹰币慢慢涨。", "这不是上班，这是攒桌子基金。", "电脑在发热，本鹰在挣钱。", "认真打字，偷偷想饭。"],
        [BanterContext.LateNight] = ["这么晚了，保存一下再睡吧。", "月亮都打卡了，你还没下班。", "别熬鹰了，明天还能摸鱼。", "困意已送达，记得签收。"],
    };

    internal static IReadOnlyList<string> Lines { get; } = Array.AsReadOnly(new[]
    {
        "人在工位，魂在饭点。",
        "键盘敲响，饭碗有望。",
        "我没发呆，我在加载。",
        "今日进度：等一口饭。",
        "别卷我，我只有翅膀。",
        "需求很多，鹰脑很小。",
        "表情严肃，脑内开席。",
        "工资是饼，会议管饱。",
        "工位很小，胃口很大。",
        "饭点一到，效率爆表。",
        "你负责输出，我负责围观。",
        "打工可以，饿鹰不行。",
        "这班上的，羽毛都懂了。",
        "我不是困，是省电模式。",
        "稍等，我的脑子在排队。",
        "文件已开，灵感未到。",
        "会议结束，饭局开始？",
        "忙了一圈，水还没喝。",
        "工资到账，我才重新联网。",
        "老板画饼，我等外卖。",
        "我的专长是准点干饭。",
        "项目在跑，午饭在招手。",
        "先别催，鹰正在思考。",
        "脑袋很大，缓存很小。",
        "这段沉默，是加载动画。",
        "工作有回声，肚子有回音。",
        "你敲代码，我敲饭碗。",
        "当前状态：已读，挠头。",
        "任务已分配，灵魂未上线。",
        "咖啡续杯，快乐续命？",
        "上班是副业，等饭是主业。",
        "想法很丰满，饭碗很空。",
        "本鹰在线，偶尔掉智商。",
        "小问题，先歪个脑袋。",
        "这活儿，得配两口饭。",
        "需求又变？那我也变笨。",
        "需求会变，饭点不变。",
        "能量不足，申请投喂。",
        "我在陪工，没在监工。",
        "你歇一下，我装会儿忙。",
        "今日KPI：活着下班。",
        "代码能跑，我不想跑。",
        "我的工牌，比我更想上班。",
        "报告，快乐库存待补货。",
        "会议能压缩，午饭不能。",
        "方案可以改，饭不许缩水。",
        "看起来很忙，其实在酝酿。",
        "别急，翅膀都还没理顺。",
        "工作讲逻辑，干饭靠本能。",
        "这不是摸鱼，是鹰式待机。",
        "上班全靠一口仙气。",
        "消息叮咚，鹰头嗡嗡。",
        "需求一拍脑袋，我就秃了。",
        "今日技能：一本正经点头。",
        "你先保存，我先保住饭。",
        "表格有行列，快乐别排队。",
        "方案第几版？饭来第几碗？",
        "周报写满了，人也写空了。",
        "本鹰不画饼，只想吃饼。",
        "收工不积极，饭碗有问题。"
    });
}
