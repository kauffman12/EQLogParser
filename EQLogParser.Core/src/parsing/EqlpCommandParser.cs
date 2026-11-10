namespace EQLogParser;

internal enum EqlpCommand
{
  None,
  Stop,
  Clear
}

internal static class EqlpCommandParser
{
  internal static EqlpCommand Match(ChatType chat, string action, string currentPlayer)
  {
    if (chat?.Sender is null || action is null)
    {
      return EqlpCommand.None;
    }

    // Commands belong to the character this lane reads, not to the main window's selected player.
    if (!chat.IsFrom(currentPlayer))
    {
      return EqlpCommand.None;
    }

    // TextStart includes the 27-character timestamp; action does not.
    var offset = chat.TextStart - 27;
    if (offset <= 0 || offset >= action.Length)
    {
      return EqlpCommand.None;
    }

    var tail = action.AsSpan(offset);
    if (tail.StartsWith("{EQLP:STOP}", StringComparison.OrdinalIgnoreCase))
    {
      return EqlpCommand.Stop;
    }

    return tail.StartsWith("{EQLP:CLEAR}", StringComparison.OrdinalIgnoreCase)
      ? EqlpCommand.Clear : EqlpCommand.None;
  }
}
