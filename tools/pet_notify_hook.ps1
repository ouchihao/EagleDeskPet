param(
    [Parameter(Mandatory = $true)]
    [string]$BridgeExe,
    [Parameter(Mandatory = $true)]
    [ValidateLength(1, 32)]
    [string]$Source,
    [Parameter(Mandatory = $true)]
    [ValidateLength(1, 128)]
    [string]$EventId,
    [ValidateSet("reply_ready", "needs_attention", "task_failed")]
    [string]$EventType = "reply_ready",
    [ValidateLength(0, 240)]
    [string]$Message = "",
    [ValidateLength(0, 128)]
    [string]$SessionId = ""
)

# Generic local hook adapter. Configure Source in the client's own hook, not from model text.
# Never pass a full transcript, secrets, executable paths, or URLs as the notification message.
$ErrorActionPreference = "Stop"
$resolvedBridge = (Get-Item -LiteralPath $BridgeExe -ErrorAction Stop).FullName
if ([System.IO.Path]::GetFileName($resolvedBridge) -ne "EagleDeskPet.Mcp.exe") {
    throw "BridgeExe must point to the locally installed EagleDeskPet.Mcp.exe."
}
$notifyArguments = @("--source", $Source, "--notify", "--event-id", $EventId, "--event-type", $EventType)
if (-not [string]::IsNullOrEmpty($Message)) { $notifyArguments += @("--message", $Message) }
if (-not [string]::IsNullOrEmpty($SessionId)) { $notifyArguments += @("--session-id", $SessionId) }
& $resolvedBridge @notifyArguments
exit $LASTEXITCODE
