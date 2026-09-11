namespace CairnMultiplayerMod.Internal.Game;

/// <summary>
/// Matches each suppressed pause request with its corresponding unpause request. The connection
/// state is captured when the menu opens so disconnecting or connecting while it is open cannot
/// unbalance Cairn's native request counters.
/// </summary>
internal sealed class PauseRequestSuppressionState
{
    private bool _suppressOpeningRequests;
    private bool _pauseRequestSuppressed;
    private bool _gameTimePauseRequestSuppressed;
    private bool _suppressClosingPauseRequest;
    private bool _suppressClosingGameTimePauseRequest;

    internal void BeginOpening(bool connected)
    {
        _suppressOpeningRequests = connected;
        _pauseRequestSuppressed = false;
        _gameTimePauseRequestSuppressed = false;
    }

    internal void EndOpening() => _suppressOpeningRequests = false;

    internal bool SuppressPauseRequest()
    {
        if (!_suppressOpeningRequests) return false;
        _pauseRequestSuppressed = true;
        return true;
    }

    internal bool SuppressGameTimePauseRequest()
    {
        if (!_suppressOpeningRequests) return false;
        _gameTimePauseRequestSuppressed = true;
        return true;
    }

    internal void BeginClosing()
    {
        _suppressClosingPauseRequest = _pauseRequestSuppressed;
        _suppressClosingGameTimePauseRequest = _gameTimePauseRequestSuppressed;
    }

    internal bool SuppressUnpauseRequest() => _suppressClosingPauseRequest;
    internal bool SuppressGameTimeUnpauseRequest() => _suppressClosingGameTimePauseRequest;

    internal void EndClosing() => Reset();

    internal void Reset()
    {
        _suppressOpeningRequests = false;
        _pauseRequestSuppressed = false;
        _gameTimePauseRequestSuppressed = false;
        _suppressClosingPauseRequest = false;
        _suppressClosingGameTimePauseRequest = false;
    }
}
