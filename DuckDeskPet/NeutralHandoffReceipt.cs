using System.Text.Json;
using DuckDeskPet.Core;

namespace DuckDeskPet;

internal static class NeutralHandoffReceipt
{
    internal static bool Matches(JsonElement message, long generation, long request, long revision, ClipSample sample,
        string desk, string computer, string outfit)
    {
        var equipment = message.GetProperty("equipment");
        return message.GetProperty("protocol").GetInt32() == 1 &&
            message.GetProperty("type").GetString() == "neutralized" &&
            message.GetProperty("generation").GetInt64() == generation &&
            message.GetProperty("requestId").GetInt64() == request &&
            message.GetProperty("revision").GetInt64() == revision &&
            message.GetProperty("sequence").GetInt64() == sample.Sequence &&
            message.GetProperty("kind").GetString() == sample.Kind.ToString() &&
            equipment.GetProperty("desk").GetString() == desk &&
            equipment.GetProperty("computer").GetString() == computer &&
            equipment.GetProperty("outfit").GetString() == outfit;
    }
}
