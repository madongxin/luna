using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using GameMesh.Auth;
using GameMesh.Network;
using GameMesh.Protocol;

namespace GameMesh.Friends
{
    public enum FriendPanelTab
    {
        Friends = 0,
        Requests = 1,
        Blocked = 2
    }

    public sealed class FriendClient
    {
        readonly GameSession _session;
        readonly Func<GameRequest, CancellationToken, Task<GameResponse>> _request;
        readonly Dictionary<string, string> _ops = new Dictionary<string, string>();
        int _sessionGeneration;
        int _friendListEpoch;
        int _requestListEpoch;
        int _blockListEpoch;
        bool _requestsOpen = true;
        float _cooldownUntil;

        public readonly List<FriendBrief> Friends = new List<FriendBrief>();
        public readonly List<FriendRequestInfo> Requests = new List<FriendRequestInfo>();
        public readonly List<FriendBrief> Blocked = new List<FriendBrief>();
        public FriendBrief SearchHit;
        public FriendRelationState SearchRelation;
        public string LastError = "";
        public string LastNotice = "";
        public uint FriendCount;
        public uint FriendCap;
        public int RequestBadge;
        public bool PanelOpen;
        public FriendPanelTab Tab;
        public float PollIntervalSeconds = 15f;
        // Panel closed: do not pull the full friend list. Refresh only the request
        // list on this interval so the badge stays correct if a push was missed.
        // Login and reconnect still call RefreshAfterLoginAsync for both lists.
        // Presence is best-effort and is restored by the open-panel friend refresh.
        public float ClosedPanelRequestPollSeconds = 60f;
        int _inFlight;
        int _friendLoads;
        int _requestLoads;
        int _blockLoads;
        float _nextSearchAt;

        public bool Busy => _inFlight > 0;
        public bool FriendsLoading => _friendLoads > 0;
        public bool RequestsLoading => _requestLoads > 0;
        public bool BlockedLoading => _blockLoads > 0;
        public bool ShowRetryHint { get; private set; }
        public bool PresenceHold { get; private set; }
        public string LastErrorCode { get; private set; } = "";
        public string LastOperationId { get; private set; } = "";
        public string FriendNextCursor { get; private set; } = "";
        public string RequestNextCursor { get; private set; } = "";
        public string BlockNextCursor { get; private set; } = "";
        public string FriendListError { get; private set; } = "";
        public string RequestListError { get; private set; } = "";
        public string BlockListError { get; private set; } = "";
        public bool FriendHasMore => FriendNextCursor.Length > 0;
        public bool RequestHasMore => RequestNextCursor.Length > 0;
        public bool BlockHasMore => BlockNextCursor.Length > 0;
        public const int MaxSearchLength = 32;
        public const int MaxNameChars = 16;
        public const int MaxOperationIdLength = 96;
        const uint FriendPageSize = 50;
        const uint RequestPageSize = 20;
        const int MaxListPages = 20;
        readonly HashSet<string> _busyKeys = new HashSet<string>();

        public void BeginPresenceHold()
        {
            PresenceHold = true;
        }

        public bool CanRequest =>
            _requestsOpen && _session != null && _session.HasIdentity && !_session.SessionReplaced;

        public void SetRequestsOpen(bool open)
        {
            _requestsOpen = open;
        }

        public FriendClient(GameSession session, Func<GameRequest, CancellationToken, Task<GameResponse>> request)
        {
            _session = session;
            _request = request;
        }

        public void Clear()
        {
            Interlocked.Increment(ref _sessionGeneration);
            _requestsOpen = false;
            Friends.Clear();
            Requests.Clear();
            Blocked.Clear();
            SearchHit = null;
            SearchRelation = FriendRelationState.FriendRelationNone;
            LastError = "";
            LastNotice = "";
            FriendCount = 0;
            FriendCap = 0;
            RequestBadge = 0;
            _ops.Clear();
            _busyKeys.Clear();
            _inFlight = 0;
            _cooldownUntil = 0f;
            _nextSearchAt = 0f;
            ShowRetryHint = false;
            PresenceHold = false;
            LastErrorCode = "";
            LastOperationId = "";
            FriendNextCursor = "";
            RequestNextCursor = "";
            BlockNextCursor = "";
            FriendListError = "";
            RequestListError = "";
            BlockListError = "";
        }

        public bool ShouldPoll(float now, float lastPoll, bool panelOpen)
        {
            if (!CanRequest)
                return false;
            var interval = panelOpen ? PollIntervalSeconds : ClosedPanelRequestPollSeconds;
            return now - lastPoll >= interval;
        }

        public async Task RefreshAfterLoginAsync(CancellationToken ct)
        {
            if (_session != null && _session.HasIdentity && !_session.SessionReplaced)
                _requestsOpen = true;
            if (!CanRequest)
                return;
            await RefreshFriendsAsync(ct).ConfigureAwait(false);
            await RefreshRequestsAsync(ct).ConfigureAwait(false);
            await RefreshBlockedAsync(ct).ConfigureAwait(false);
        }

        public Task RefreshFriendsAsync(CancellationToken ct)
        {
            return ReadFriendsAsync(ct, true, true);
        }

        public Task OpenFriendPageAsync(CancellationToken ct)
        {
            return ReadFriendsAsync(ct, true, false);
        }

        public Task LoadMoreFriendsAsync(CancellationToken ct)
        {
            return ReadFriendsAsync(ct, false, false);
        }

        async Task ReadFriendsAsync(CancellationToken ct, bool reset, bool drain)
        {
            if (!CanRequest)
                return;
            if (!reset && FriendNextCursor.Length == 0)
                return;
            Interlocked.Increment(ref _friendLoads);
            try
            {
                var sessionGen = _sessionGeneration;
                var epoch = Interlocked.Increment(ref _friendListEpoch);
                var cursor = reset ? "" : FriendNextCursor;
                var acc = new List<FriendBrief>();
                if (!reset)
                    acc.AddRange(Friends);
                var seen = new HashSet<string> { cursor ?? "" };
                var pages = 0;
                var friendN = FriendCount;
                var friendCap = FriendCap;
                while (true)
                {
                    pages++;
                    if (pages > MaxListPages)
                    {
                        FriendListError = GameErrorCatalog.FormatUi(GameMeshErrorCode.ServerError, "好友列表页数异常");
                        LastError = FriendListError;
                        return;
                    }

                    var rsp = await _request(new GameRequest
                    {
                        FriendList = new FriendListReq
                        {
                            PlayerId = _session.PlayerId,
                            Cursor = cursor ?? "",
                            PageSize = FriendPageSize
                        }
                    }, ct).ConfigureAwait(false);
                    if (sessionGen != _sessionGeneration || epoch != _friendListEpoch)
                        return;
                    var body = rsp.FriendList;
                    if (!Ok(rsp, body?.Ok ?? false, body?.ErrorCode, rsp.Message))
                    {
                        FriendListError = LastError;
                        return;
                    }

                    if (body != null)
                    {
                        for (var i = 0; i < body.Friends.Count; i++)
                            AddUniqueFriend(acc, body.Friends[i]);
                        if (body.FriendN != 0)
                            friendN = body.FriendN;
                        if (body.FriendCap != 0)
                            friendCap = body.FriendCap;
                        cursor = body.NextCursor ?? "";
                    }
                    else
                        cursor = "";

                    if (!drain || cursor.Length == 0)
                        break;
                    if (!seen.Add(cursor))
                    {
                        FriendListError = GameErrorCatalog.FormatUi(GameMeshErrorCode.ServerError, "好友列表游标重复");
                        LastError = FriendListError;
                        return;
                    }
                }

                if (sessionGen != _sessionGeneration || epoch != _friendListEpoch)
                    return;
                Friends.Clear();
                Friends.AddRange(acc);
                FriendCount = friendN != 0 ? friendN : (uint)acc.Count;
                FriendCap = friendCap;
                FriendNextCursor = cursor ?? "";
                FriendListError = "";
                SortFriends();
                LastError = "";
                ShowRetryHint = false;
                PresenceHold = false;
            }
            finally
            {
                Interlocked.Decrement(ref _friendLoads);
            }
        }

        public Task RefreshRequestsAsync(CancellationToken ct)
        {
            return ReadRequestsAsync(ct, true, true);
        }

        public Task LoadMoreRequestsAsync(CancellationToken ct)
        {
            return ReadRequestsAsync(ct, false, false);
        }

        async Task ReadRequestsAsync(CancellationToken ct, bool reset, bool drain)
        {
            if (!CanRequest)
                return;
            if (!reset && RequestNextCursor.Length == 0)
                return;
            Interlocked.Increment(ref _requestLoads);
            try
            {
                var sessionGen = _sessionGeneration;
                var epoch = Interlocked.Increment(ref _requestListEpoch);
                var cursor = reset ? "" : RequestNextCursor;
                var acc = new List<FriendRequestInfo>();
                if (!reset)
                    acc.AddRange(Requests);
                var seen = new HashSet<string> { cursor ?? "" };
                var pages = 0;
                while (true)
                {
                    pages++;
                    if (pages > MaxListPages)
                    {
                        RequestListError = GameErrorCatalog.FormatUi(GameMeshErrorCode.ServerError, "申请列表页数异常");
                        LastError = RequestListError;
                        return;
                    }

                    var rsp = await _request(new GameRequest
                    {
                        FriendRequestList = new FriendRequestListReq
                        {
                            PlayerId = _session.PlayerId,
                            Cursor = cursor ?? "",
                            PageSize = RequestPageSize
                        }
                    }, ct).ConfigureAwait(false);
                    if (sessionGen != _sessionGeneration || epoch != _requestListEpoch)
                        return;
                    var body = rsp.FriendRequestList;
                    if (!Ok(rsp, body?.Ok ?? false, body?.ErrorCode, rsp.Message))
                    {
                        RequestListError = LastError;
                        return;
                    }

                    if (body != null)
                    {
                        for (var i = 0; i < body.Requests.Count; i++)
                            AddUniqueRequest(acc, body.Requests[i]);
                        cursor = body.NextCursor ?? "";
                    }
                    else
                        cursor = "";

                    if (!drain || cursor.Length == 0)
                        break;
                    if (!seen.Add(cursor))
                    {
                        RequestListError = GameErrorCatalog.FormatUi(GameMeshErrorCode.ServerError, "申请列表游标重复");
                        LastError = RequestListError;
                        return;
                    }
                }

                if (sessionGen != _sessionGeneration || epoch != _requestListEpoch)
                    return;
                Requests.Clear();
                Requests.AddRange(acc);
                RequestNextCursor = cursor ?? "";
                RequestListError = "";
                if (RequestNextCursor.Length == 0)
                    RequestBadge = Requests.Count;
                LastError = "";
                ShowRetryHint = false;
            }
            finally
            {
                Interlocked.Decrement(ref _requestLoads);
            }
        }

        public Task RefreshBlockedAsync(CancellationToken ct)
        {
            return ReadBlockedAsync(ct, true, true);
        }

        public Task LoadMoreBlockedAsync(CancellationToken ct)
        {
            return ReadBlockedAsync(ct, false, false);
        }

        async Task ReadBlockedAsync(CancellationToken ct, bool reset, bool drain)
        {
            if (!CanRequest)
                return;
            if (!reset && BlockNextCursor.Length == 0)
                return;
            Interlocked.Increment(ref _blockLoads);
            try
            {
                var sessionGen = _sessionGeneration;
                var epoch = Interlocked.Increment(ref _blockListEpoch);
                var cursor = reset ? "" : BlockNextCursor;
                var acc = new List<FriendBrief>();
                if (!reset)
                    acc.AddRange(Blocked);
                var seen = new HashSet<string> { cursor ?? "" };
                var pages = 0;
                while (true)
                {
                    pages++;
                    if (pages > MaxListPages)
                    {
                        BlockListError = GameErrorCatalog.FormatUi(GameMeshErrorCode.ServerError, "黑名单页数异常");
                        LastError = BlockListError;
                        return;
                    }

                    var rsp = await _request(new GameRequest
                    {
                        FriendBlockList = new FriendBlockListReq
                        {
                            PlayerId = _session.PlayerId,
                            Cursor = cursor ?? "",
                            PageSize = FriendPageSize
                        }
                    }, ct).ConfigureAwait(false);
                    if (sessionGen != _sessionGeneration || epoch != _blockListEpoch)
                        return;
                    var body = rsp.FriendBlockList;
                    if (!Ok(rsp, body?.Ok ?? false, body?.ErrorCode, rsp.Message))
                    {
                        BlockListError = LastError;
                        return;
                    }

                    if (body != null)
                    {
                        for (var i = 0; i < body.Blocked.Count; i++)
                            AddUniqueFriend(acc, body.Blocked[i]);
                        cursor = body.NextCursor ?? "";
                    }
                    else
                        cursor = "";

                    if (!drain || cursor.Length == 0)
                        break;
                    if (!seen.Add(cursor))
                    {
                        BlockListError = GameErrorCatalog.FormatUi(GameMeshErrorCode.ServerError, "黑名单游标重复");
                        LastError = BlockListError;
                        return;
                    }
                }

                if (sessionGen != _sessionGeneration || epoch != _blockListEpoch)
                    return;
                Blocked.Clear();
                Blocked.AddRange(acc);
                BlockNextCursor = cursor ?? "";
                BlockListError = "";
                LastError = "";
                ShowRetryHint = false;
            }
            finally
            {
                Interlocked.Decrement(ref _blockLoads);
            }
        }

        public async Task SearchAsync(string query, CancellationToken ct)
        {
            if (RejectIfClosed())
                return;
            query = (query ?? "").Trim();
            if (query.Length > MaxSearchLength)
                query = query.Substring(0, MaxSearchLength);
            if (query.Length == 0)
            {
                LastErrorCode = "ERR_INVALID_ARGUMENT";
                LastError = GameErrorCatalog.FormatUi("ERR_INVALID_ARGUMENT", "请输入角色名或 PlayerID");
                return;
            }

            if (UnityEngine.Time.unscaledTime < _nextSearchAt)
                return;
            _nextSearchAt = UnityEngine.Time.unscaledTime + 0.4f;

            var req = new FriendSearchReq { PlayerId = _session.PlayerId };
            if (ulong.TryParse(query, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) && id != 0)
                req.TargetPlayerId = id;
            else
                req.ExactName = query;

            var rsp = await _request(new GameRequest { FriendSearch = req }, ct).ConfigureAwait(false);
            var body = rsp.FriendSearch;
            if (!Ok(rsp, body?.Ok ?? false, body?.ErrorCode, rsp.Message))
            {
                SearchHit = null;
                SearchRelation = FriendRelationState.FriendRelationNone;
                return;
            }

            SearchHit = body.Player;
            SearchRelation = body.Relation;
            LastError = "";
            LastNotice = SearchRelationLabel(SearchRelation);
            if (SearchRelation == FriendRelationState.FriendRelationReceivedPending)
                Tab = FriendPanelTab.Requests;
        }

        public async Task ApplyAsync(ulong targetPlayerId, string exactName, CancellationToken ct)
        {
            if (Cooling() || RejectIfClosed() || !BeginTarget("apply", targetPlayerId))
                return;
            var sessionGen = _sessionGeneration;
            try
            {
                await ApplyCoreAsync(targetPlayerId, exactName, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsTransportFailure(ex))
            {
                if (sessionGen == _sessionGeneration)
                    NoteTransportFailure(ex);
            }
            finally
            {
                EndTarget("apply", targetPlayerId);
            }
        }

        async Task ApplyCoreAsync(ulong targetPlayerId, string exactName, CancellationToken ct)
        {
            if (SearchRelation == FriendRelationState.FriendRelationReceivedPending)
            {
                Tab = FriendPanelTab.Requests;
                LastNotice = "对方已向你发出申请，请到申请列表同意。";
                return;
            }

            var sessionGen = _sessionGeneration;
            var rsp = await SendWriteAsync(new GameRequest
            {
                FriendApply = new FriendApplyReq
                {
                    PlayerId = _session.PlayerId,
                    TargetPlayerId = targetPlayerId,
                    ExactName = exactName ?? "",
                    OperationId = StableOp("apply", targetPlayerId)
                }
            }, sessionGen, ct).ConfigureAwait(false);
            if (sessionGen != _sessionGeneration)
                return;
            var body = rsp.FriendApply;
            var code = ProtocolMapper.ExtractErrorCode(rsp);
            if (code == "ERR_INCOMING_REQUEST_EXISTS")
            {
                Tab = FriendPanelTab.Requests;
                LastErrorCode = code;
                LastError = GameErrorCatalog.FormatUi(code);
                await RefreshKeepingNoticeAsync(ct, true, false).ConfigureAwait(false);
                return;
            }

            if (code == "ERR_OPERATION_TOO_FREQUENT")
            {
                Ok(rsp, false, code, rsp.Message, true);
                return;
            }

            if (!await CommitAsync(rsp, body?.Ok ?? false, body?.ErrorCode, rsp.Message, ct).ConfigureAwait(false))
                return;
            ForgetOp("apply", targetPlayerId);
            SearchRelation = FriendRelationState.FriendRelationSentPending;
            NoteSuccess(body != null && body.RequestId == 0 ? "已提交" : "已发送好友申请");
        }

        public async Task AcceptAsync(ulong requestId, CancellationToken ct)
        {
            if (Cooling() || RejectIfClosed() || LocalRequestExpired(requestId) || !BeginTarget("accept", requestId))
                return;
            var sessionGen = _sessionGeneration;
            try
            {
                await AcceptCoreAsync(requestId, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsTransportFailure(ex))
            {
                if (sessionGen == _sessionGeneration)
                    NoteTransportFailure(ex);
            }
            finally
            {
                EndTarget("accept", requestId);
            }
        }

        async Task AcceptCoreAsync(ulong requestId, CancellationToken ct)
        {
            var sessionGen = _sessionGeneration;
            var rsp = await SendWriteAsync(new GameRequest
            {
                FriendAccept = new FriendAcceptReq
                {
                    PlayerId = _session.PlayerId,
                    RequestId = requestId,
                    OperationId = StableOp("accept", requestId)
                }
            }, sessionGen, ct).ConfigureAwait(false);
            if (sessionGen != _sessionGeneration)
                return;
            var body = rsp.FriendAccept;
            var code = ProtocolMapper.ExtractErrorCode(rsp);
            if (code == "ERR_OPERATION_TOO_FREQUENT")
            {
                Ok(rsp, false, code, rsp.Message, true);
                return;
            }

            if (!await CommitAsync(rsp, body?.Ok ?? false, body?.ErrorCode, rsp.Message, ct).ConfigureAwait(false))
                return;
            ForgetOp("accept", requestId);
            RemoveRequest(requestId);
            if (body?.Peer != null)
                UpsertFriend(body.Peer);
            NoteSuccess("已成为好友");
        }

        public async Task RejectAsync(ulong requestId, CancellationToken ct)
        {
            if (RejectIfClosed() || LocalRequestExpired(requestId) || !BeginTarget("reject", requestId))
                return;
            var sessionGen = _sessionGeneration;
            try
            {
                await RejectCoreAsync(requestId, sessionGen, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsTransportFailure(ex))
            {
                if (sessionGen == _sessionGeneration)
                    NoteTransportFailure(ex);
            }
            finally
            {
                EndTarget("reject", requestId);
            }
        }

        async Task RejectCoreAsync(ulong requestId, int sessionGen, CancellationToken ct)
        {
            var rsp = await SendWriteAsync(new GameRequest
            {
                FriendReject = new FriendRejectReq
                {
                    PlayerId = _session.PlayerId,
                    RequestId = requestId,
                    OperationId = StableOp("reject", requestId)
                }
            }, sessionGen, ct).ConfigureAwait(false);
            if (sessionGen != _sessionGeneration)
                return;
            var body = rsp.FriendReject;
            if (!await CommitAsync(rsp, body?.Ok ?? false, body?.ErrorCode, rsp.Message, ct).ConfigureAwait(false))
                return;
            ForgetOp("reject", requestId);
            RemoveRequest(requestId);
            NoteSuccess("已拒绝申请");
        }

        public async Task DeleteAsync(ulong friendPlayerId, CancellationToken ct)
        {
            if (RejectIfClosed() || !BeginTarget("delete", friendPlayerId))
                return;
            var sessionGen = _sessionGeneration;
            try
            {
                await DeleteCoreAsync(friendPlayerId, sessionGen, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsTransportFailure(ex))
            {
                if (sessionGen == _sessionGeneration)
                    NoteTransportFailure(ex);
            }
            finally
            {
                EndTarget("delete", friendPlayerId);
            }
        }

        async Task DeleteCoreAsync(ulong friendPlayerId, int sessionGen, CancellationToken ct)
        {
            var rsp = await SendWriteAsync(new GameRequest
            {
                FriendDelete = new FriendDeleteReq
                {
                    PlayerId = _session.PlayerId,
                    FriendPlayerId = friendPlayerId,
                    OperationId = StableOp("delete", friendPlayerId)
                }
            }, sessionGen, ct).ConfigureAwait(false);
            if (sessionGen != _sessionGeneration)
                return;
            var body = rsp.FriendDelete;
            if (!await CommitAsync(rsp, body?.Ok ?? false, body?.ErrorCode, rsp.Message, ct).ConfigureAwait(false))
                return;
            ForgetOp("delete", friendPlayerId);
            RemoveFriend(friendPlayerId);
            NoteSuccess("已删除好友");
        }

        public async Task BlockAsync(ulong targetPlayerId, CancellationToken ct)
        {
            if (RejectIfClosed() || !BeginTarget("block", targetPlayerId))
                return;
            var sessionGen = _sessionGeneration;
            try
            {
                await BlockCoreAsync(targetPlayerId, sessionGen, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsTransportFailure(ex))
            {
                if (sessionGen == _sessionGeneration)
                    NoteTransportFailure(ex);
            }
            finally
            {
                EndTarget("block", targetPlayerId);
            }
        }

        async Task BlockCoreAsync(ulong targetPlayerId, int sessionGen, CancellationToken ct)
        {
            var rsp = await SendWriteAsync(new GameRequest
            {
                FriendBlock = new FriendBlockReq
                {
                    PlayerId = _session.PlayerId,
                    TargetPlayerId = targetPlayerId,
                    OperationId = StableOp("block", targetPlayerId)
                }
            }, sessionGen, ct).ConfigureAwait(false);
            if (sessionGen != _sessionGeneration)
                return;
            var body = rsp.FriendBlock;
            if (!await CommitAsync(rsp, body?.Ok ?? false, body?.ErrorCode, rsp.Message, ct).ConfigureAwait(false))
                return;
            ForgetOp("block", targetPlayerId);
            RemoveFriend(targetPlayerId);
            RemoveRequestsFrom(targetPlayerId);
            if (FindBlocked(targetPlayerId) == null)
            {
                var brief = SearchHit != null && SearchHit.PlayerId == targetPlayerId
                    ? SearchHit.Clone()
                    : new FriendBrief { PlayerId = targetPlayerId };
                Blocked.Add(brief);
                Interlocked.Increment(ref _blockListEpoch);
            }

            SearchRelation = FriendRelationState.FriendRelationBlockedBySelf;
            NoteSuccess("已拉黑");
            await RefreshBlockedAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(LastNotice))
                NoteSuccess("已拉黑");
        }

        public async Task UnblockAsync(ulong targetPlayerId, CancellationToken ct)
        {
            if (RejectIfClosed() || !BeginTarget("unblock", targetPlayerId))
                return;
            var sessionGen = _sessionGeneration;
            try
            {
                await UnblockCoreAsync(targetPlayerId, sessionGen, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsTransportFailure(ex))
            {
                if (sessionGen == _sessionGeneration)
                    NoteTransportFailure(ex);
            }
            finally
            {
                EndTarget("unblock", targetPlayerId);
            }
        }

        async Task UnblockCoreAsync(ulong targetPlayerId, int sessionGen, CancellationToken ct)
        {
            var rsp = await SendWriteAsync(new GameRequest
            {
                FriendUnblock = new FriendUnblockReq
                {
                    PlayerId = _session.PlayerId,
                    TargetPlayerId = targetPlayerId,
                    OperationId = StableOp("unblock", targetPlayerId)
                }
            }, sessionGen, ct).ConfigureAwait(false);
            if (sessionGen != _sessionGeneration)
                return;
            var body = rsp.FriendUnblock;
            if (!await CommitAsync(rsp, body?.Ok ?? false, body?.ErrorCode, rsp.Message, ct).ConfigureAwait(false))
                return;
            ForgetOp("unblock", targetPlayerId);
            var removedBlock = false;
            for (var i = Blocked.Count - 1; i >= 0; i--)
            {
                if (Blocked[i].PlayerId == targetPlayerId)
                {
                    Blocked.RemoveAt(i);
                    removedBlock = true;
                }
            }

            if (removedBlock)
                Interlocked.Increment(ref _blockListEpoch);
            if (SearchHit != null && SearchHit.PlayerId == targetPlayerId)
                SearchRelation = FriendRelationState.FriendRelationNone;
            NoteSuccess("已解除拉黑");
        }

        public bool ApplyPush(GameResponse inner)
        {
            if (inner == null)
                return false;
            if (inner.FriendRequestPush != null)
            {
                UpsertRequest(inner.FriendRequestPush);
                RequestBadge = Requests.Count;
                LastNotice = "收到好友申请";
                if (MissingName(inner.FriendRequestPush.Applicant))
                    _ = RefreshRequestsAsync(CancellationToken.None);
                return true;
            }

            if (inner.FriendAddedPush != null && inner.FriendAddedPush.Peer != null)
            {
                UpsertFriend(inner.FriendAddedPush.Peer);
                RemoveRequestsFrom(inner.FriendAddedPush.Peer.PlayerId);
                if (MissingName(inner.FriendAddedPush.Peer))
                    _ = RefreshFriendsAsync(CancellationToken.None);
                return true;
            }

            if (inner.FriendRemovedPush != null)
            {
                RemoveFriend(inner.FriendRemovedPush.FriendPlayerId);
                return true;
            }

            if (inner.FriendPresencePush != null)
            {
                if (!PresenceHold)
                    ApplyPresence(inner.FriendPresencePush);
                return true;
            }

            return false;
        }

        public static string FormatLastOnline(ulong unixSeconds, DateTimeOffset? now = null)
        {
            if (unixSeconds == 0 || unixSeconds > (ulong)long.MaxValue)
                return "未知";
            DateTimeOffset seen;
            try
            {
                seen = DateTimeOffset.FromUnixTimeSeconds((long)unixSeconds);
            }
            catch
            {
                return "未知";
            }

            var clock = now ?? DateTimeOffset.UtcNow;
            var delta = clock - seen;
            if (delta.TotalSeconds < -300 || delta.TotalDays >= 365)
                return "未知";
            if (delta.TotalMinutes < 1)
                return "刚刚";
            if (delta.TotalHours < 1)
                return ((int)delta.TotalMinutes).ToString(CultureInfo.InvariantCulture) + " 分钟前";
            if (delta.TotalDays < 1)
                return ((int)delta.TotalHours).ToString(CultureInfo.InvariantCulture) + " 小时前";
            return ((int)delta.TotalDays).ToString(CultureInfo.InvariantCulture) + " 天前";
        }

        public static string SearchRelationLabel(FriendRelationState relation)
        {
            switch (relation)
            {
                case FriendRelationState.FriendRelationFriend: return "已是好友";
                case FriendRelationState.FriendRelationSentPending: return "已发出申请";
                case FriendRelationState.FriendRelationReceivedPending: return "对方已向你申请";
                case FriendRelationState.FriendRelationBlockedBySelf: return "已拉黑";
                default: return "可申请";
            }
        }

        public static int CompareFriends(FriendBrief a, FriendBrief b)
        {
            if (a == null && b == null)
                return 0;
            if (a == null)
                return 1;
            if (b == null)
                return -1;
            if (a.Online != b.Online)
                return a.Online ? -1 : 1;
            return b.LastOnlineTime.CompareTo(a.LastOnlineTime);
        }

        void SortFriends()
        {
            Friends.Sort(CompareFriends);
        }

        static void AddUniqueFriend(List<FriendBrief> acc, FriendBrief row)
        {
            if (acc == null || row == null || row.PlayerId == 0)
                return;
            for (var i = 0; i < acc.Count; i++)
            {
                if (acc[i] != null && acc[i].PlayerId == row.PlayerId)
                {
                    acc[i] = row;
                    return;
                }
            }

            acc.Add(row);
        }

        static void AddUniqueRequest(List<FriendRequestInfo> acc, FriendRequestInfo row)
        {
            if (acc == null || row == null || row.RequestId == 0)
                return;
            for (var i = 0; i < acc.Count; i++)
            {
                if (acc[i] != null && acc[i].RequestId == row.RequestId)
                {
                    acc[i] = row;
                    return;
                }
            }

            acc.Add(row);
        }

        bool LocalRequestExpired(ulong requestId)
        {
            for (var i = 0; i < Requests.Count; i++)
            {
                if (Requests[i] == null || Requests[i].RequestId != requestId)
                    continue;
                if (!IsRequestExpired(Requests[i]))
                    return false;
                LastErrorCode = "ERR_REQUEST_EXPIRED";
                LastError = GameErrorCatalog.FormatUi("ERR_REQUEST_EXPIRED");
                ShowRetryHint = false;
                return true;
            }

            return false;
        }

        public static bool IsRequestExpired(FriendRequestInfo row, DateTimeOffset? now = null)
        {
            if (row == null || row.ExpireAt == 0 || row.ExpireAt > (ulong)long.MaxValue)
                return false;
            try
            {
                var exp = DateTimeOffset.FromUnixTimeSeconds((long)row.ExpireAt);
                return exp <= (now ?? DateTimeOffset.UtcNow);
            }
            catch
            {
                return true;
            }
        }

        void UpsertFriend(FriendBrief peer)
        {
            if (peer == null || peer.PlayerId == 0)
                return;
            for (var i = 0; i < Friends.Count; i++)
            {
                if (Friends[i].PlayerId == peer.PlayerId)
                {
                    Friends[i] = peer;
                    SortFriends();
                    Interlocked.Increment(ref _friendListEpoch);
                    return;
                }
            }

            Friends.Add(peer);
            SortFriends();
            FriendCount = (uint)Friends.Count;
            Interlocked.Increment(ref _friendListEpoch);
        }

        void RemoveFriend(ulong playerId)
        {
            for (var i = Friends.Count - 1; i >= 0; i--)
            {
                if (Friends[i].PlayerId == playerId)
                    Friends.RemoveAt(i);
            }

            FriendCount = (uint)Friends.Count;
            Interlocked.Increment(ref _friendListEpoch);
        }

        void UpsertRequest(FriendRequestPush push)
        {
            if (push == null || push.RequestId == 0)
                return;
            for (var i = 0; i < Requests.Count; i++)
            {
                if (Requests[i].RequestId == push.RequestId)
                {
                    Requests[i] = new FriendRequestInfo
                    {
                        RequestId = push.RequestId,
                        Applicant = push.Applicant,
                        CreatedAt = push.CreatedAt,
                        ExpireAt = push.ExpireAt
                    };
                    Interlocked.Increment(ref _requestListEpoch);
                    return;
                }
            }

            Requests.Insert(0, new FriendRequestInfo
            {
                RequestId = push.RequestId,
                Applicant = push.Applicant,
                CreatedAt = push.CreatedAt,
                ExpireAt = push.ExpireAt
            });
            Interlocked.Increment(ref _requestListEpoch);
        }

        void RemoveRequest(ulong requestId)
        {
            for (var i = Requests.Count - 1; i >= 0; i--)
            {
                if (Requests[i].RequestId == requestId)
                    Requests.RemoveAt(i);
            }

            RequestBadge = Requests.Count;
            Interlocked.Increment(ref _requestListEpoch);
        }

        void RemoveRequestsFrom(ulong playerId)
        {
            for (var i = Requests.Count - 1; i >= 0; i--)
            {
                if (Requests[i].Applicant != null && Requests[i].Applicant.PlayerId == playerId)
                    Requests.RemoveAt(i);
            }

            RequestBadge = Requests.Count;
            Interlocked.Increment(ref _requestListEpoch);
        }

        void ApplyPresence(FriendPresencePush push)
        {
            if (push == null)
                return;
            for (var i = 0; i < Friends.Count; i++)
            {
                if (Friends[i].PlayerId != push.FriendPlayerId)
                    continue;
                var copy = Friends[i].Clone();
                copy.Online = push.Online;
                copy.LastOnlineTime = push.LastOnlineTime;
                Friends[i] = copy;
                SortFriends();
                return;
            }
        }

        FriendBrief FindBlocked(ulong playerId)
        {
            for (var i = 0; i < Blocked.Count; i++)
            {
                if (Blocked[i].PlayerId == playerId)
                    return Blocked[i];
            }

            return null;
        }

        static string TargetKey(string kind, ulong id)
        {
            return kind + ":" + id.ToString(CultureInfo.InvariantCulture);
        }

        bool BeginTarget(string kind, ulong id)
        {
            var key = TargetKey(kind, id);
            if (_busyKeys.Contains(key))
            {
                LastError = "";
                LastErrorCode = "";
                ShowRetryHint = false;
                LastNotice = "正在处理，请稍候";
                return false;
            }

            _busyKeys.Add(key);
            Interlocked.Increment(ref _inFlight);
            return true;
        }

        void EndTarget(string kind, ulong id)
        {
            if (!_busyKeys.Remove(TargetKey(kind, id)))
                return;
            if (_inFlight > 0)
                Interlocked.Decrement(ref _inFlight);
        }

        void NoteSuccess(string notice)
        {
            LastError = "";
            LastErrorCode = "";
            ShowRetryHint = false;
            if (string.IsNullOrEmpty(notice))
                return;
            if (string.IsNullOrEmpty(LastNotice) || LastNotice == "正在处理，请稍候" || LastNotice == notice)
                LastNotice = notice;
            else if (LastNotice.IndexOf(notice, StringComparison.Ordinal) < 0)
                LastNotice = LastNotice + "\n" + notice;
        }

        void NoteTransportFailure(Exception ex)
        {
            var mesh = ex as GameMeshException;
            var code = mesh != null && !string.IsNullOrEmpty(mesh.ErrorCode)
                ? mesh.ErrorCode
                : GameMeshErrorCode.ClientTimeout;
            ShowRetryHint = true;
            LastErrorCode = code;
            LastError = GameErrorCatalog.FormatUi(code) + "\n稍后重试";
            GameMeshLog.Warn("friend code=" + code);
        }

        string StableOp(string kind, ulong id)
        {
            var key = kind + ":" + id.ToString(CultureInfo.InvariantCulture);
            if (_ops.TryGetValue(key, out var existing) && !string.IsNullOrEmpty(existing))
            {
                LastOperationId = existing;
                return existing;
            }
            var op = kind + ":" + _session.PlayerId.ToString(CultureInfo.InvariantCulture) + ":" +
                     id.ToString(CultureInfo.InvariantCulture) + ":" + Guid.NewGuid().ToString("N");
            if (op.Length > MaxOperationIdLength)
                op = op.Substring(0, MaxOperationIdLength);
            _ops[key] = op;
            LastOperationId = op;
            return op;
        }

        void ForgetOp(string kind, ulong id)
        {
            _ops.Remove(kind + ":" + id.ToString(CultureInfo.InvariantCulture));
        }

        bool Cooling()
        {
            if (UnityEngine.Time.unscaledTime < _cooldownUntil)
            {
                ShowRetryHint = true;
                LastErrorCode = "ERR_OPERATION_TOO_FREQUENT";
                LastError = GameErrorCatalog.FormatUi("ERR_OPERATION_TOO_FREQUENT") + "\n稍后重试";
                return true;
            }

            return false;
        }

        void MarkCooldown()
        {
            _cooldownUntil = UnityEngine.Time.unscaledTime + 2.5f;
        }

        async Task<bool> CommitAsync(GameResponse rsp, bool bodyOk, string bodyCode, string message,
            CancellationToken ct)
        {
            var code = ProtocolMapper.ExtractErrorCode(rsp);
            if (string.IsNullOrEmpty(code))
                code = bodyCode ?? "";
            var ok = Ok(rsp, bodyOk, bodyCode, message, true);
            if (!ok && (NeedsRequestRefresh(code) || NeedsFriendRefresh(code)))
                await RefreshKeepingNoticeAsync(ct, NeedsRequestRefresh(code), NeedsFriendRefresh(code))
                    .ConfigureAwait(false);
            return ok;
        }

        static bool NeedsRequestRefresh(string code)
        {
            return code == "ERR_REQUEST_EXPIRED" || code == "ERR_REQUEST_NOT_FOUND" ||
                   code == "ERR_RELATION_CONFLICT";
        }

        static bool NeedsFriendRefresh(string code)
        {
            return code == "ERR_RELATION_CONFLICT" || code == "ERR_NOT_FRIEND" || code == "ERR_ALREADY_FRIEND";
        }

        async Task RefreshKeepingNoticeAsync(CancellationToken ct, bool requests, bool friends)
        {
            var error = LastError;
            var errorCode = LastErrorCode;
            var notice = LastNotice;
            var retry = ShowRetryHint;
            if (requests)
                await RefreshRequestsAsync(ct).ConfigureAwait(false);
            if (friends)
                await RefreshFriendsAsync(ct).ConfigureAwait(false);
            LastError = error;
            LastErrorCode = errorCode;
            ShowRetryHint = retry;
            if (!string.IsNullOrEmpty(notice))
                LastNotice = notice;
        }

        bool RejectIfClosed()
        {
            if (CanRequest)
                return false;
            LastErrorCode = GameMeshErrorCode.ClientIllegalState;
            LastError = GameErrorCatalog.FormatUi(GameMeshErrorCode.ClientIllegalState, "当前未登录或连接已断开");
            return true;
        }

        static bool MissingName(FriendBrief brief)
        {
            return brief == null || string.IsNullOrEmpty(brief.Name);
        }

        public static string ClipText(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= MaxNameChars)
                return value ?? "";
            return value.Substring(0, MaxNameChars) + "…";
        }

        public static string DisplayName(FriendBrief brief, bool blockedPlaceholder)
        {
            if (brief != null && !string.IsNullOrEmpty(brief.Name))
                return ClipText(brief.Name);
            if (blockedPlaceholder)
                return "加载中";
            if (brief != null && brief.PlayerId != 0)
                return "#" + brief.PlayerId.ToString(CultureInfo.InvariantCulture);
            return "玩家";
        }

        public static string FormatFriendLine(FriendBrief row)
        {
            if (row == null)
                return "";
            var text = (row.Online ? "在线" : "离线") + "  " + DisplayName(row, false) +
                       (row.Level == 0 ? "  等级未知" : "  Lv." + row.Level.ToString(CultureInfo.InvariantCulture));
            if (row.Profession != 0)
                text += "  职业 " + row.Profession.ToString(CultureInfo.InvariantCulture);
            if (!row.Online)
                text += "  " + FormatLastOnline(row.LastOnlineTime);
            if (!string.IsNullOrEmpty(row.MapName))
                text += "  " + ClipText(row.MapName);
            if (!string.IsNullOrEmpty(row.Remark))
                text += "  备注 " + ClipText(row.Remark);
            return text;
        }

        public string DiagnosticsText()
        {
            var op = LastOperationId ?? "";
            if (op.Length > 8)
                op = op.Substring(0, 8);
            return "code=" + (LastErrorCode ?? "") +
                   " retry=" + (ShowRetryHint ? "1" : "0") +
                   " hold=" + (PresenceHold ? "1" : "0") +
                   " load=" + (FriendsLoading ? "1" : "0") + (RequestsLoading ? "1" : "0") +
                   (BlockedLoading ? "1" : "0") +
                   " n=" + Friends.Count.ToString(CultureInfo.InvariantCulture) + "/" +
                   Requests.Count.ToString(CultureInfo.InvariantCulture) + "/" +
                   Blocked.Count.ToString(CultureInfo.InvariantCulture) +
                   " op=" + op;
        }

        async Task<GameResponse> SendWriteAsync(GameRequest req, int sessionGen, CancellationToken ct)
        {
            try
            {
                return await _request(req, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsTransportFailure(ex))
            {
                if (sessionGen != _sessionGeneration)
                    throw;
                return await _request(req, ct).ConfigureAwait(false);
            }
        }

        static bool IsTransportFailure(Exception ex)
        {
            var mesh = ex as GameMeshException;
            if (mesh != null)
            {
                return mesh.ErrorCode == GameMeshErrorCode.ClientTimeout ||
                       mesh.ErrorCode == GameMeshErrorCode.ClientDisconnected;
            }

            return ex is TimeoutException || ex is System.IO.IOException;
        }

        bool Ok(GameResponse rsp, bool bodyOk, string bodyCode, string message, bool cooldownOnRetry = false)
        {
            var code = ProtocolMapper.ExtractErrorCode(rsp);
            if (string.IsNullOrEmpty(code))
                code = bodyCode ?? "";
            if (rsp != null && rsp.Ok && bodyOk)
            {
                ShowRetryHint = false;
                LastErrorCode = "";
                return true;
            }

            var retryable = rsp != null && rsp.Retryable;
            if (!retryable && !string.IsNullOrEmpty(code))
                retryable = GameErrorCatalog.Resolve(code).Retryable;
            ShowRetryHint = retryable;
            LastErrorCode = string.IsNullOrEmpty(code) ? GameMeshErrorCode.ServerError : code;
            if (!string.IsNullOrEmpty(LastErrorCode))
                GameMeshLog.Warn("friend code=" + LastErrorCode);
            if (code == "ERR_RELATION_CONFLICT" || code == "ERR_DEPENDENCY_UNAVAILABLE" ||
                code == "ERR_OPERATION_TOO_FREQUENT")
                LastError = GameErrorCatalog.FormatUi(code, message);
            else
                LastError = GameErrorCatalog.FormatUi(
                    string.IsNullOrEmpty(code) ? GameMeshErrorCode.ServerError : code, message);
            if (retryable)
                LastError += "\n稍后重试";
            if (retryable && cooldownOnRetry)
                MarkCooldown();
            return false;
        }
    }
}
