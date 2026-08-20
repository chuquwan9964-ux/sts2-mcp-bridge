using System.Security.Cryptography;

namespace Sts2McpBridge.Core;

public sealed class BridgeStore
{
    private readonly object _gate = new();
    private readonly string _token;
    private readonly List<HistoryEntry> _history = [];
    private BridgeState? _state;
    private PendingAction? _pending;
    private bool _paused;

    public BridgeStore(string token) => _token = token;

    public bool Authenticate(string? token)
    {
        if (token is null) return false;
        byte[] expected = System.Text.Encoding.UTF8.GetBytes(_token);
        byte[] actual = System.Text.Encoding.UTF8.GetBytes(token);
        return expected.Length == actual.Length && CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    public void Register(BridgeState state)
    {
        lock (_gate)
        {
            if (_state is not null && state.StateVersion < _state.StateVersion) return;
            _state = state with { Paused = _paused, UpdatedAt = DateTimeOffset.UtcNow };
        }
    }

    public BridgeState? GetState()
    {
        lock (_gate) return _state is null ? null : _state with { Paused = _paused };
    }

    public ActionResponse Queue(ActionRequest request)
    {
        lock (_gate)
        {
            if (_paused) return new(false, "Bridge is paused.");
            if (_state is null) return new(false, "No game state is registered.");
            if (request.StateVersion != _state.StateVersion) return new(false, $"Stale state_version; current value is {_state.StateVersion}.");
            if (_state.LegalActions.Count(action => action.ActionId == request.ActionId) != 1) return new(false, "action_id is not legal in this state.");
            if (_pending is not null) return new(false, "Another action is pending.");
            _pending = new(request.StateVersion, request.ActionId);
            _history.Add(new(DateTimeOffset.UtcNow, request.StateVersion, request.ActionId, "queued"));
            TrimHistory();
            return new(true, "Action queued for main-thread execution.");
        }
    }

    public PendingAction? TakePending(long stateVersion)
    {
        lock (_gate)
        {
            if (_paused || _pending is null || _pending.StateVersion != stateVersion) return null;
            PendingAction result = _pending;
            _pending = null;
            return result;
        }
    }

    public void RecordResult(PendingAction action, string status)
    {
        lock (_gate)
        {
            _history.Add(new(DateTimeOffset.UtcNow, action.StateVersion, action.ActionId, status));
            TrimHistory();
        }
    }

    public void SetPaused(bool paused)
    {
        lock (_gate)
        {
            _paused = paused;
            if (paused && _pending is not null)
            {
                _history.Add(new(DateTimeOffset.UtcNow, _pending.StateVersion, _pending.ActionId, "cancelled_by_pause"));
                _pending = null;
            }
            TrimHistory();
        }
    }

    public IReadOnlyList<HistoryEntry> GetHistory(int limit)
    {
        lock (_gate) return _history.TakeLast(Math.Clamp(limit, 1, 200)).ToArray();
    }

    private void TrimHistory()
    {
        if (_history.Count > 500) _history.RemoveRange(0, _history.Count - 500);
    }
}
