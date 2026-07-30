namespace DeskBox.Services;

/// <summary>
/// Runtime source of truth for the physical host owned by a widget surface.
/// Persistent member ids remain lookup aliases; the surface id is the stable
/// key and does not change when the active member changes.
/// </summary>
internal sealed class WidgetSurfaceRegistry<THost>
    where THost : class
{
    private readonly object _gate = new();
    private readonly Dictionary<string, WidgetSurfaceSession<THost>> _sessions =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _surfaceIdByMemberId =
        new(StringComparer.Ordinal);

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _sessions.Count;
            }
        }
    }

    public IReadOnlyList<WidgetSurfaceSession<THost>> GetSessions()
    {
        lock (_gate)
        {
            return _sessions.Values.ToList();
        }
    }

    public WidgetSurfaceSession<THost> RegisterActive(
        WidgetSurfaceDefinition definition,
        THost host)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(host);
        definition.Validate();

        lock (_gate)
        {
            if (_sessions.TryGetValue(definition.SurfaceId, out var existing))
            {
                if (!ReferenceEquals(existing.Host, host))
                {
                    throw new InvalidOperationException(
                        $"Surface '{definition.SurfaceId}' already owns another active host.");
                }

                ReindexMembers(existing.MemberIds, definition);
                existing.UpdateDefinition(definition);
                return existing;
            }

            RemoveMemberClaims(definition.MemberIds, definition.SurfaceId);
            var session = new WidgetSurfaceSession<THost>(definition, host);
            _sessions.Add(definition.SurfaceId, session);
            IndexMembers(definition);
            return session;
        }
    }

    /// <summary>
    /// Reconciles the registry with an already-stable runtime host. This is
    /// used at restore and after group topology changes, never as the switch
    /// commit path.
    /// </summary>
    public WidgetSurfaceSession<THost> SynchronizeActive(
        WidgetSurfaceDefinition definition,
        THost host)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(host);
        definition.Validate();

        lock (_gate)
        {
            if (_sessions.TryGetValue(definition.SurfaceId, out var existing))
            {
                ReindexMembers(existing.MemberIds, definition);
                existing.CommitActive(definition, host);
                return existing;
            }

            RemoveMemberClaims(definition.MemberIds, definition.SurfaceId);
            var session = new WidgetSurfaceSession<THost>(definition, host);
            _sessions.Add(definition.SurfaceId, session);
            IndexMembers(definition);
            return session;
        }
    }

    public bool StageCandidate(
        string surfaceId,
        string targetMemberId,
        THost candidateHost)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(surfaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetMemberId);
        ArgumentNullException.ThrowIfNull(candidateHost);

        lock (_gate)
        {
            if (!_sessions.TryGetValue(surfaceId, out var session) ||
                !session.MemberIds.Contains(targetMemberId, StringComparer.Ordinal))
            {
                return false;
            }

            session.StageCandidate(targetMemberId, candidateHost);
            return true;
        }
    }

    public WidgetSurfaceSession<THost> CommitActive(
        WidgetSurfaceDefinition definition,
        THost host)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(host);
        definition.Validate();

        lock (_gate)
        {
            if (!_sessions.TryGetValue(definition.SurfaceId, out var session))
            {
                return RegisterActive(definition, host);
            }

            if (!ReferenceEquals(session.Host, host) &&
                (!ReferenceEquals(session.CandidateHost, host) ||
                 !string.Equals(
                     session.CandidateMemberId,
                     definition.ActiveMemberId,
                     StringComparison.Ordinal)))
            {
                throw new InvalidOperationException(
                    $"Host was not prepared for surface '{definition.SurfaceId}'.");
            }

            ReindexMembers(session.MemberIds, definition);
            session.CommitActive(definition, host);
            return session;
        }
    }

    public bool CancelCandidate(string surfaceId, THost candidateHost)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(surfaceId);
        ArgumentNullException.ThrowIfNull(candidateHost);

        lock (_gate)
        {
            return _sessions.TryGetValue(surfaceId, out var session) &&
                   session.CancelCandidate(candidateHost);
        }
    }

    public bool UpdateDefinition(WidgetSurfaceDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        definition.Validate();

        lock (_gate)
        {
            if (!_sessions.TryGetValue(definition.SurfaceId, out var session))
            {
                return false;
            }

            ReindexMembers(session.MemberIds, definition);
            session.UpdateDefinition(definition);
            return true;
        }
    }

    public bool TryGet(
        string surfaceId,
        out WidgetSurfaceSession<THost>? session)
    {
        if (string.IsNullOrWhiteSpace(surfaceId))
        {
            session = null;
            return false;
        }

        lock (_gate)
        {
            return _sessions.TryGetValue(surfaceId, out session);
        }
    }

    public bool TryGetByMember(
        string memberId,
        out WidgetSurfaceSession<THost>? session)
    {
        if (string.IsNullOrWhiteSpace(memberId))
        {
            session = null;
            return false;
        }

        lock (_gate)
        {
            if (!_surfaceIdByMemberId.TryGetValue(memberId, out string? surfaceId))
            {
                session = null;
                return false;
            }

            return _sessions.TryGetValue(surfaceId, out session);
        }
    }

    public bool RemoveSurface(string surfaceId)
    {
        if (string.IsNullOrWhiteSpace(surfaceId))
        {
            return false;
        }

        lock (_gate)
        {
            if (!_sessions.Remove(surfaceId, out var session))
            {
                return false;
            }

            RemoveIndexedMembers(session.MemberIds, surfaceId);
            session.Dispose();
            return true;
        }
    }

    public int UnregisterHost(THost host)
    {
        ArgumentNullException.ThrowIfNull(host);

        lock (_gate)
        {
            int removed = 0;
            foreach (WidgetSurfaceSession<THost> session in _sessions.Values.ToList())
            {
                if (ReferenceEquals(session.CandidateHost, host))
                {
                    session.CancelCandidate(host);
                }

                if (!ReferenceEquals(session.Host, host))
                {
                    continue;
                }

                _sessions.Remove(session.SurfaceId);
                RemoveIndexedMembers(session.MemberIds, session.SurfaceId);
                session.Dispose();
                removed++;
            }

            return removed;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            foreach (WidgetSurfaceSession<THost> session in _sessions.Values)
            {
                session.Dispose();
            }

            _sessions.Clear();
            _surfaceIdByMemberId.Clear();
        }
    }

    private void ReindexMembers(
        IReadOnlyList<string> previousMemberIds,
        WidgetSurfaceDefinition definition)
    {
        RemoveIndexedMembers(previousMemberIds, definition.SurfaceId);
        RemoveMemberClaims(definition.MemberIds, definition.SurfaceId);
        IndexMembers(definition);
    }

    private void IndexMembers(WidgetSurfaceDefinition definition)
    {
        foreach (string memberId in definition.MemberIds)
        {
            _surfaceIdByMemberId[memberId] = definition.SurfaceId;
        }
    }

    private void RemoveMemberClaims(
        IReadOnlyList<string> memberIds,
        string exceptSurfaceId)
    {
        foreach (string memberId in memberIds)
        {
            if (!_surfaceIdByMemberId.TryGetValue(memberId, out string? claimedSurfaceId) ||
                string.Equals(claimedSurfaceId, exceptSurfaceId, StringComparison.Ordinal))
            {
                continue;
            }

            if (_sessions.Remove(claimedSurfaceId, out var claimedSession))
            {
                RemoveIndexedMembers(
                    claimedSession.MemberIds,
                    claimedSession.SurfaceId);
                claimedSession.Dispose();
            }
            else
            {
                _surfaceIdByMemberId.Remove(memberId);
            }
        }
    }

    private void RemoveIndexedMembers(
        IReadOnlyList<string> memberIds,
        string surfaceId)
    {
        foreach (string memberId in memberIds)
        {
            if (_surfaceIdByMemberId.TryGetValue(memberId, out string? indexedSurfaceId) &&
                string.Equals(indexedSurfaceId, surfaceId, StringComparison.Ordinal))
            {
                _surfaceIdByMemberId.Remove(memberId);
            }
        }
    }
}

internal sealed record WidgetSurfaceDefinition(
    string SurfaceId,
    string? GroupId,
    IReadOnlyList<string> MemberIds,
    string ActiveMemberId)
{
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(SurfaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ActiveMemberId);
        ArgumentNullException.ThrowIfNull(MemberIds);
        if (MemberIds.Count == 0 ||
            MemberIds.Any(string.IsNullOrWhiteSpace) ||
            MemberIds.Distinct(StringComparer.Ordinal).Count() != MemberIds.Count ||
            !MemberIds.Contains(ActiveMemberId, StringComparer.Ordinal))
        {
            throw new ArgumentException(
                "A surface definition requires unique members and a valid active member.");
        }
    }
}

internal sealed class WidgetSurfaceSession<THost> : IDisposable
    where THost : class
{
    private bool _isDisposed;

    internal WidgetSurfaceSession(
        WidgetSurfaceDefinition definition,
        THost host)
    {
        SurfaceId = definition.SurfaceId;
        GroupId = definition.GroupId;
        MemberIds = definition.MemberIds.ToArray();
        ActiveMemberId = definition.ActiveMemberId;
        Host = host;
        SwitchGate = new SemaphoreSlim(1, 1);
    }

    public string SurfaceId { get; }

    public string? GroupId { get; private set; }

    public IReadOnlyList<string> MemberIds { get; private set; }

    public string ActiveMemberId { get; private set; }

    public THost Host { get; private set; }

    public string? CandidateMemberId { get; private set; }

    public THost? CandidateHost { get; private set; }

    public SemaphoreSlim SwitchGate { get; }

    internal void UpdateDefinition(WidgetSurfaceDefinition definition)
    {
        ThrowIfDisposed();
        GroupId = definition.GroupId;
        MemberIds = definition.MemberIds.ToArray();
        ActiveMemberId = definition.ActiveMemberId;
        if (CandidateMemberId is not null &&
            !MemberIds.Contains(CandidateMemberId, StringComparer.Ordinal))
        {
            CandidateMemberId = null;
            CandidateHost = null;
        }
    }

    internal void StageCandidate(string targetMemberId, THost candidateHost)
    {
        ThrowIfDisposed();
        CandidateMemberId = targetMemberId;
        CandidateHost = candidateHost;
    }

    internal bool CancelCandidate(THost candidateHost)
    {
        ThrowIfDisposed();
        if (!ReferenceEquals(CandidateHost, candidateHost))
        {
            return false;
        }

        CandidateMemberId = null;
        CandidateHost = null;
        return true;
    }

    internal void CommitActive(
        WidgetSurfaceDefinition definition,
        THost host)
    {
        ThrowIfDisposed();
        GroupId = definition.GroupId;
        MemberIds = definition.MemberIds.ToArray();
        ActiveMemberId = definition.ActiveMemberId;
        Host = host;
        CandidateMemberId = null;
        CandidateHost = null;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        CandidateMemberId = null;
        CandidateHost = null;
        // A topology change can retire the registry entry after cancellation
        // but before an in-flight switch leaves its finally block. Keeping the
        // gate undisposed allows that holder to release it safely.
    }
}
