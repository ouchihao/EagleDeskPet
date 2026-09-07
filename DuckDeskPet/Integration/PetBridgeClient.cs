using System.IO;
using System.IO.Pipes;
using System.Text.Json;

namespace DuckDeskPet.Integration;

public sealed class PetBridgeClient
{
    private readonly SemaphoreSlim _connections = new(4, 4);

    public async Task<PetBridgeReply> SendAsync(PetBridgeRequest request, CancellationToken token = default)
    {
        if (!await _connections.WaitAsync(0, token).ConfigureAwait(false)) return new(false, "busy", "Too many simultaneous pet requests.");
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            using var pipe = new NamedPipeClientStream(".", PetBridgeProtocol.PipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(1200, deadline.Token).ConfigureAwait(false);
            await PetBridgeProtocol.WriteAsync(pipe, request, deadline.Token).ConfigureAwait(false);
            return await PetBridgeProtocol.ReadAsync<PetBridgeReply>(pipe, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return new(false, "unavailable", "The desktop pet did not respond in time. Open EagleDeskPet.exe and try again.");
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            return new(false, "unavailable", "The desktop pet is not available. Open EagleDeskPet.exe as the same Windows user.");
        }
        finally { _connections.Release(); }
    }
}
