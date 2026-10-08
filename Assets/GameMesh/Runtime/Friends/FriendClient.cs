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
        int _generation;
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

        public bool Busy => _inFlight > 0;

        public FriendClient(GameSession session, Func<GameRequest, CancellationToken, Task<GameResponse>> request)
        {
            _session = session;
            _request = request;
        }

        public void Clear()
        {
            Interlocked.Increment(ref _generation);
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
            _cooldownUntil = 0f;
        }

        public bool ShouldPoll(float now, float lastPoll, bool panelOpen)
        {
            if (_session == null || !_session.HasIdentity)
                return false;
            var interval = panelOpen ? PollIntervalSeconds : ClosedPanelRequestPollSeconds;
            return now - lastPoll >= interval;
        }

        public async Task RefreshAfterLoginAsync(CancellationToken ct)
        {
            await RefreshFriendsAsync(ct).ConfigureAwait(false);
            await RefreshRequestsAsync(ct).ConfigureAwait(false);
        }

        public async Task RefreshFriendsAsync(CancellationToken ct)
        {
            var gen = Interlocked.Increment(ref _generation);
            var rsp = await _request(new GameRequest
            {
                FriendList = new FriendListReq
                {
                    PlayerId = _session.PlayerId,
                    PageSize = 100
                }
            }, ct).ConfigureAwait(false);
            if (gen != _generation)
                return;
            var body = rsp.FriendList;
            if (!Ok(rsp, body?.Ok ?? false, body?.ErrorCode, rsp.Message))
                return;
            Friends.Clear();
            if (body != null)
            {
                Friends.AddRange(body.Friends);
                FriendCount = body.FriendN;
                FriendCap = body.FriendCap;
            }

            SortFriends();
            LastError = "";
        }

        public async Task RefreshRequestsAsync(CancellationToken ct)
        {
            var gen = _generation;
            Requests.Clear();
            var cursor = "";
            do
            {
                var rsp = await _request(new GameRequest
                {
                    FriendRequestList = new FriendRequestListReq
                    {
                        PlayerId = _session.PlayerId,
                        Cursor = cursor ?? "",
                        PageSize = 20
                    }
                }, ct).ConfigureAwait(false);
                if (gen != _generation)
                    return;
                var body = rsp.FriendRequestList;
                if (!Ok(rsp, body?.Ok ?? false, body?.ErrorCode, rsp.Message))
                    return;
                if (body != null)
                    Requests.AddRange(body.Requests);
                cursor = body != null ? body.NextCursor : "";
            } while (!string.IsNullOrEmpty(cursor));

            RequestBadge = Requests.Count;
            LastError = "";
        }

        public async Task RefreshBlockedAsync(CancellationToken ct)
        {
            var gen = _generation;
            var rsp = await _request(new GameRequest
            {
                FriendBlockList = new FriendBlockListReq
                {
                    PlayerId = _session.PlayerId,
                    PageSize = 100
                }
            }, ct).ConfigureAwait(false);
            if (gen != _generation)
                return;
            var body = rsp.FriendBlockList;
            if (!Ok(rsp, body?.Ok ?? false, body?.ErrorCode, rsp.Message))
                return;
            Blocked.Clear();
            if (body != null)
                Blocked.AddRange(body.Blocked);
            LastError = "";
        }

        public async Task SearchAsync(string query, CancellationToken ct)
        {
            query = (query ?? "").Trim();
            if (query.Length == 0)
            {
                LastError = GameErrorCatalog.FormatUi("ERR_INVALID_ARGUMENT", "请输入角色名或 PlayerID");
                return;
            }

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
            if (Cooling())
                return;
            Interlocked.Increment(ref _inFlight);
            try
            {
                await ApplyCoreAsync(targetPlayerId, exactName, ct).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
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

            var gen = _generation;
            var rsp = await _request(new GameRequest
            {
                FriendApply = new FriendApplyReq
                {
                    PlayerId = _session.PlayerId,
                    TargetPlayerId = targetPlayerId,
                    ExactName = exactName ?? "",
                    OperationId = StableOp("apply", targetPlayerId)
                }
            }, ct).ConfigureAwait(false);
            if (gen != _generation)
                return;
            var body = rsp.FriendApply;
            var code = ProtocolMapper.ExtractErrorCode(rsp);
            if (code == "ERR_INCOMING_REQUEST_EXISTS")
            {
                Tab = FriendPanelTab.Requests;
                LastError = GameErrorCatalog.FormatUi(code);
                await RefreshKeepingNoticeAsync(ct, false).ConfigureAwait(false);
                return;
            }

            if (code == "ERR_OPERATION_TOO_FREQUENT")
            {
                MarkCooldown();
                Ok(rsp, false, code, rsp.Message);
                return;
            }

            if (!await CommitAsync(rsp, body?.Ok ?? false, body?.ErrorCode, rsp.Message, ct).ConfigureAwait(false))
                return;
            ForgetOp("apply", targetPlayerId);
            SearchRelation = FriendRelationState.FriendRelationSentPending;
            LastNotice = body != null && body.RequestId == 0 ? "已提交" : "已发送好友申请";
            LastError = "";
        }

        public async Task AcceptAsync(ulong requestId, CancellationToken ct)
        {
            if (Cooling())
                return;
            Interlocked.Increment(ref _inFlight);
            try
            {
                await AcceptCoreAsync(requestId, ct).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }

        async Task AcceptCoreAsync(ulong requestId, CancellationToken ct)
        {
            var gen = _generation;
            var rsp = await _request(new GameRequest
            {
                FriendAccept = new FriendAcceptReq
                {
                    PlayerId = _session.PlayerId,
                    RequestId = requestId,
                    OperationId = StableOp("accept", requestId)
                }
            }, ct).ConfigureAwait(false);
            if (gen != _generation)
                return;
            var body = rsp.FriendAccept;
            var code = ProtocolMapper.ExtractErrorCode(rsp);
            if (code == "ERR_OPERATION_TOO_FREQUENT")
            {
                MarkCooldown();
                Ok(rsp, false, code, rsp.Message);
                return;
            }

            if (!await CommitAsync(rsp, body?.Ok ?? false, body?.ErrorCode, rsp.Message, ct).ConfigureAwait(false))
                return;
            ForgetOp("accept", requestId);
            RemoveRequest(requestId);
            if (body?.Peer != null)
                UpsertFriend(body.Peer);
            LastNotice = "已成为好友";
            LastError = "";
        }

        public async Task RejectAsync(ulong requestId, CancellationToken ct)
        {
            var gen = _generation;
            var rsp = await _request(new GameRequest
            {
                FriendReject = new FriendRejectReq
                {
                    PlayerId = _session.PlayerId,
                    RequestId = requestId,
                    OperationId = StableOp("reject", requestId)
                }
            }, ct).ConfigureAwait(false);
            if (gen != _generation)
                return;
            var body = rsp.FriendReject;
            if (!await CommitAsync(rsp, body?.Ok ?? false, body?.ErrorCode, rsp.Message, ct).ConfigureAwait(false))
                return;
            ForgetOp("reject", requestId);
            RemoveRequest(requestId);
            LastNotice = "已拒绝申请";
            LastError = "";
        }

        public async Task DeleteAsync(ulong friendPlayerId, CancellationToken ct)
        {
            var gen = _generation;
            var rsp = await _request(new GameRequest
            {
                FriendDelete = new FriendDeleteReq
                {
                    PlayerId = _session.PlayerId,
                    FriendPlayerId = friendPlayerId,
                    OperationId = StableOp("delete", friendPlayerId)
                }
            }, ct).ConfigureAwait(false);
            if (gen != _generation)
                return;
            var body = rsp.FriendDelete;
            if (!await CommitAsync(rsp, body?.Ok ?? false, body?.ErrorCode, rsp.Message, ct).ConfigureAwait(false))
                return;
            ForgetOp("delete", friendPlayerId);
            RemoveFriend(friendPlayerId);
            LastNotice = "已删除好友";
            LastError = "";
        }

        public async Task BlockAsync(ulong targetPlayerId, CancellationToken ct)
        {
            var gen = _generation;
            var rsp = await _request(new GameRequest
            {
                FriendBlock = new FriendBlockReq
                {
                    PlayerId = _session.PlayerId,
                    TargetPlayerId = targetPlayerId,
                    OperationId = StableOp("block", targetPlayerId)
                }
            }, ct).ConfigureAwait(false);
            if (gen != _generation)
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
            }

            SearchRelation = FriendRelationState.FriendRelationBlockedBySelf;
            LastNotice = "已拉黑";
            LastError = "";
        }

        public async Task UnblockAsync(ulong targetPlayerId, CancellationToken ct)
        {
            var gen = _generation;
            var rsp = await _request(new GameRequest
            {
                FriendUnblock = new FriendUnblockReq
                {
                    PlayerId = _session.PlayerId,
                    TargetPlayerId = targetPlayerId,
                    OperationId = StableOp("unblock", targetPlayerId)
                }
            }, ct).ConfigureAwait(false);
            if (gen != _generation)
                return;
            var body = rsp.FriendUnblock;
            if (!await CommitAsync(rsp, body?.Ok ?? false, body?.ErrorCode, rsp.Message, ct).ConfigureAwait(false))
                return;
            ForgetOp("unblock", targetPlayerId);
            for (var i = Blocked.Count - 1; i >= 0; i--)
            {
                if (Blocked[i].PlayerId == targetPlayerId)
                    Blocked.RemoveAt(i);
            }

            if (SearchHit != null && SearchHit.PlayerId == targetPlayerId)
                SearchRelation = FriendRelationState.FriendRelationNone;
            LastNotice = "已解除拉黑";
            LastError = "";
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
                return true;
            }

            if (inner.FriendAddedPush != null && inner.FriendAddedPush.Peer != null)
            {
                UpsertFriend(inner.FriendAddedPush.Peer);
                RemoveRequestsFrom(inner.FriendAddedPush.Peer.PlayerId);
                return true;
            }

            if (inner.FriendRemovedPush != null)
            {
                RemoveFriend(inner.FriendRemovedPush.FriendPlayerId);
                return true;
            }

            if (inner.FriendPresencePush != null)
            {
                ApplyPresence(inner.FriendPresencePush);
                return true;
            }

            return false;
        }

        public static string FormatLastOnline(ulong unixSeconds)
        {
            if (unixSeconds == 0)
                return "未知";
            try
            {
                var dt = DateTimeOffset.FromUnixTimeSeconds((long)unixSeconds).ToLocalTime();
                return dt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            }
            catch
            {
                return "未知";
            }
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
                    return;
                }
            }

            Friends.Add(peer);
            SortFriends();
            FriendCount = (uint)Friends.Count;
        }

        void RemoveFriend(ulong playerId)
        {
            for (var i = Friends.Count - 1; i >= 0; i--)
            {
                if (Friends[i].PlayerId == playerId)
                    Friends.RemoveAt(i);
            }

            FriendCount = (uint)Friends.Count;
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
        }

        void RemoveRequest(ulong requestId)
        {
            for (var i = Requests.Count - 1; i >= 0; i--)
            {
                if (Requests[i].RequestId == requestId)
                    Requests.RemoveAt(i);
            }

            RequestBadge = Requests.Count;
        }

        void RemoveRequestsFrom(ulong playerId)
        {
            for (var i = Requests.Count - 1; i >= 0; i--)
            {
                if (Requests[i].Applicant != null && Requests[i].Applicant.PlayerId == playerId)
                    Requests.RemoveAt(i);
            }

            RequestBadge = Requests.Count;
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

        string StableOp(string kind, ulong id)
        {
            var key = kind + ":" + id.ToString(CultureInfo.InvariantCulture);
            if (_ops.TryGetValue(key, out var existing) && !string.IsNullOrEmpty(existing))
                return existing;
            var op = kind + ":" + _session.PlayerId.ToString(CultureInfo.InvariantCulture) + ":" +
                     id.ToString(CultureInfo.InvariantCulture) + ":" + Guid.NewGuid().ToString("N");
            _ops[key] = op;
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
                LastError = GameErrorCatalog.FormatUi("ERR_OPERATION_TOO_FREQUENT");
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
            var ok = Ok(rsp, bodyOk, bodyCode, message);
            if (!ok && NeedsRelationRefresh(code))
                await RefreshKeepingNoticeAsync(ct, code == "ERR_RELATION_CONFLICT").ConfigureAwait(false);
            return ok;
        }

        static bool NeedsRelationRefresh(string code)
        {
            return code == "ERR_REQUEST_EXPIRED" || code == "ERR_REQUEST_NOT_FOUND" ||
                   code == "ERR_RELATION_CONFLICT";
        }

        async Task RefreshKeepingNoticeAsync(CancellationToken ct, bool friendsToo)
        {
            var error = LastError;
            var notice = LastNotice;
            await RefreshRequestsAsync(ct).ConfigureAwait(false);
            if (friendsToo)
                await RefreshFriendsAsync(ct).ConfigureAwait(false);
            LastError = error;
            if (!string.IsNullOrEmpty(notice))
                LastNotice = notice;
        }

        bool Ok(GameResponse rsp, bool bodyOk, string bodyCode, string message)
        {
            var code = ProtocolMapper.ExtractErrorCode(rsp);
            if (string.IsNullOrEmpty(code))
                code = bodyCode ?? "";
            if (rsp != null && rsp.Ok && bodyOk)
                return true;
            if (code == "ERR_RELATION_CONFLICT" || code == "ERR_DEPENDENCY_UNAVAILABLE" ||
                code == "ERR_OPERATION_TOO_FREQUENT")
            {
                LastError = GameErrorCatalog.FormatUi(code, message);
                return false;
            }

            LastError = GameErrorCatalog.FormatUi(
                string.IsNullOrEmpty(code) ? GameMeshErrorCode.ServerError : code, message);
            return false;
        }
    }
}
