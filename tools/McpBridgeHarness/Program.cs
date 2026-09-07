using DuckDeskPet.Integration;

foreach (var badLength in new[] { 0, -1, PetBridgeProtocol.MaxPacketBytes + 1 })
{
    try
    {
        await PetBridgeProtocol.ReadAsync<PetBridgeRequest>(new MemoryStream(BitConverter.GetBytes(badLength)), CancellationToken.None);
        throw new Exception("Invalid packet length was accepted.");
    }
    catch (InvalidDataException) { }
}
using (var memory = new MemoryStream())
{
    var request = new PetBridgeRequest("notify", new("Harness", "event", null, "你好", "reply_ready"));
    await PetBridgeProtocol.WriteAsync(memory, request, CancellationToken.None);
    memory.Position = 0;
    if (await PetBridgeProtocol.ReadAsync<PetBridgeRequest>(memory, CancellationToken.None) != request)
        throw new Exception("Bridge packet round-trip failed.");
}

var events = new List<PetNotification>();
using var server = new PetBridgeServer((notification, token) =>
{
    token.ThrowIfCancellationRequested();
    lock (events) events.Add(notification);
    return Task.FromResult(true);
}, token =>
{
    token.ThrowIfCancellationRequested();
    lock (events) return Task.FromResult<object>(new { testHarness = true, count = events.Count, events = events.ToArray() });
});
server.Start();
Console.WriteLine("READY");
Console.ReadLine();
