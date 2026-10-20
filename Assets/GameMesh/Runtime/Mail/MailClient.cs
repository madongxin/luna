using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GameMesh.Auth;
using GameMesh.Network;
using GameMesh.Protocol;
using UnityEngine;

namespace GameMesh.Mail
{
    public sealed class MailListPage
    {
        public int Generation;
        public readonly List<MailBrief> Mails = new List<MailBrief>();
        public MailboxSummaryRsp Summary;
        public MailDetail Selected;
        public string LastError = "";
        public string LastNotice = "";
        public int UnreadTotal;
        public int Unclaimed;
        public bool HasBadge;
        public long MailboxVersion;
        public long ServerTimeUtc;
        public string NextCursor = "";
        public ulong LastSentMailId;
        public bool LastIdempotentHit;
        public readonly List<string> ClaimFailures = new List<string>();
    }

    public sealed class MailClient
    {
        readonly GameSession _session;
        readonly Func<GameRequest, CancellationToken, Task<GameResponse>> _request;
        int _generation;
        float _debounceUntil;
        string _pendingSendOpId;
        float _serverMarkUnscaled;
        readonly Dictionary<string, string> _keys = new Dictionary<string, string>();
        readonly Dictionary<ulong, string> _reward = new Dictionary<ulong, string>();
        readonly Dictionary<ulong, ClaimReceipt> _receipts = new Dictionary<ulong, ClaimReceipt>();
        readonly HashSet<ulong> _busy = new HashSet<ulong>();
        int _refreshDepth;
        int _refreshAgain;

        public const int PageLimit = 20;
        public const int MaxPages = 20;
        public const int BatchLimit = 20;
        public const long ExpiringWindowSeconds = 72L * 3600L;

        public string Category = "";
        public bool FilterUnread;
        public bool FilterUnclaimed;
        public bool FilterFavorite;
        public bool FilterExpiring;

        public MailListPage Page { get; } = new MailListPage();
        public AuthoritativeBag Bag { get; } = new AuthoritativeBag();
        public float PollIntervalSeconds = 10f;
        public float DebounceSeconds = 0.4f;
        public bool PanelOpen;
        public bool Loading { get; private set; }
        public int BusyCount => _busy.Count;
        public bool IsBusy(ulong mailId) => _busy.Contains(mailId);

        public MailClient(GameSession session, Func<GameRequest, CancellationToken, Task<GameResponse>> request)
        {
            _session = session;
            _request = request;
        }

        public void Clear()
        {
            Interlocked.Increment(ref _generation);
            Page.Mails.Clear();
            Page.Summary = null;
            Page.Selected = null;
            Page.LastError = "";
            Page.UnreadTotal = 0;
            Page.Unclaimed = 0;
            Page.HasBadge = false;
            Page.MailboxVersion = 0;
            Page.ServerTimeUtc = 0;
            Page.NextCursor = "";
            Page.LastNotice = "";
            Page.ClaimFailures.Clear();
            Page.LastSentMailId = 0;
            Page.LastIdempotentHit = false;
            _pendingSendOpId = null;
            _keys.Clear();
            _reward.Clear();
            _receipts.Clear();
            _busy.Clear();
            Bag.Clear();
            _debounceUntil = 0f;
            _serverMarkUnscaled = 0f;
            Loading = false;
            Interlocked.Exchange(ref _refreshAgain, 0);
        }

        public void NotifyMailboxChanged(float now)
        {
            _debounceUntil = now + DebounceSeconds;
        }

        public bool ShouldPoll(float now, float lastPoll, bool panelOpen)
        {
            if (_debounceUntil > 0f && now >= _debounceUntil)
            {
                _debounceUntil = 0f;
                return true;
            }

            if (!panelOpen)
                return false;
            return now - lastPoll >= PollIntervalSeconds;
        }

        public async Task RefreshAsync(CancellationToken ct)
        {
            Interlocked.Exchange(ref _refreshAgain, 1);
            if (Interlocked.Increment(ref _refreshDepth) > 1)
            {
                Interlocked.Decrement(ref _refreshDepth);
                return;
            }

            try
            {
                while (Interlocked.Exchange(ref _refreshAgain, 0) == 1 && !ct.IsCancellationRequested)
                    await RefreshOnceAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref _refreshDepth);
                if (Interlocked.CompareExchange(ref _refreshAgain, 0, 1) == 1 && !ct.IsCancellationRequested)
                    _ = RefreshAsync(ct);
            }
        }

        async Task RefreshOnceAsync(CancellationToken ct)
        {
            var gen = Interlocked.Increment(ref _generation);
            var playerId = _session.PlayerId;
            Loading = true;
            var category = Category ?? "";
            var unread = FilterUnread;
            var unclaimed = FilterUnclaimed;
            var favorite = FilterFavorite;
            var expiring = FilterExpiring;
            try
            {
            var summary = await _request(new GameRequest
            {
                MailboxSummary = new MailboxSummaryReq { PlayerId = _session.PlayerId }
            }, ct).ConfigureAwait(false);
            if (ct.IsCancellationRequested || !StillCurrent(playerId, gen))
                return;
            if (summary.MailboxSummary != null && NoteVersion(summary.MailboxSummary.MailboxVersion, summary.MailboxSummary.ServerTimeUtc))
            {
                Page.Summary = summary.MailboxSummary;
                ApplySummary(summary.MailboxSummary);
            }

            if (!summary.Ok)
            {
                Page.LastError = GameErrorCatalog.FormatUi(summary.MailboxSummary?.ErrorCode, summary.Message);
                return;
            }

            var cursor = "";
            var seen = new HashSet<string> { "" };
            var acc = new List<MailBrief>();
            var pages = 0;
            while (true)
            {
                pages++;
                if (pages > MaxPages)
                {
                    Page.LastError = GameErrorCatalog.FormatUi(GameMeshErrorCode.ServerError, "邮件列表页数异常");
                    return;
                }

                var list = await _request(new GameRequest
                {
                    MailList = new MailListReq
                    {
                        PlayerId = _session.PlayerId,
                        Category = category,
                        Unread = unread,
                        Unclaimed = unclaimed,
                        Favorite = favorite,
                        ExpiringSoon = expiring,
                        Cursor = cursor,
                        Limit = PageLimit
                    }
                }, ct).ConfigureAwait(false);
                if (ct.IsCancellationRequested || !StillCurrent(playerId, gen))
                    return;
                var body = list.MailList;
                if (body == null || !list.Ok || !body.Ok)
                {
                    Page.LastError = GameErrorCatalog.FormatUi(body?.ErrorCode, list.Message);
                    return;
                }

                NoteVersion(body.MailboxVersion, body.ServerTimeUtc);
                var next = body.NextCursor ?? "";
                if (body.Mails.Count == 0 && next.Length > 0)
                {
                    Page.LastError = GameErrorCatalog.FormatUi(GameMeshErrorCode.ServerError, "邮件列表返回了空页");
                    return;
                }

                for (var i = 0; i < body.Mails.Count; i++)
                    AddUnique(acc, body.Mails[i]);
                if (next.Length == 0)
                {
                    cursor = "";
                    break;
                }

                if (!seen.Add(next))
                {
                    Page.LastError = GameErrorCatalog.FormatUi(GameMeshErrorCode.ServerError, "邮件列表游标重复");
                    return;
                }

                cursor = next;
            }

            if (!StillCurrent(playerId, gen) ||
                category != (Category ?? "") ||
                unread != FilterUnread ||
                unclaimed != FilterUnclaimed ||
                favorite != FilterFavorite ||
                expiring != FilterExpiring)
                return;
            Page.Generation = gen;
            Page.Mails.Clear();
            Page.Mails.AddRange(acc);
            Page.NextCursor = cursor;
                Page.LastError = "";
            }
            finally
            {
                if (gen == _generation)
                    Loading = false;
            }
        }

        public async Task<MailDetail> GetAsync(ulong mailId, CancellationToken ct)
        {
            var gen = _generation;
            var playerId = _session.PlayerId;
            var rsp = await _request(new GameRequest
            {
                MailGet = new MailGetReq { PlayerId = _session.PlayerId, MailId = mailId }
            }, ct).ConfigureAwait(false);
            if (!StillCurrent(playerId, gen) || ct.IsCancellationRequested)
                return null;
            var body = rsp.MailGet;
            if (body == null || !rsp.Ok || !body.Ok || body.Mail == null)
            {
                Page.LastError = GameErrorCatalog.FormatUi(body?.ErrorCode, rsp.Message);
                return null;
            }

            NoteVersion(body.MailboxVersion, body.ServerTimeUtc);
            Page.Selected = body.Mail;
            if (body.Mail.Brief != null)
                TryConsumeReceipt(playerId, body.Mail.Brief.MailId, body.Mail.Receipt);
            Page.LastError = "";
            return body.Mail;
        }

        public async Task OpenAsync(ulong mailId, CancellationToken ct)
        {
            var detail = await GetAsync(mailId, ct).ConfigureAwait(false);
            if (detail?.Brief == null || !Allows(detail, "read") || !IsUnread(detail.Brief))
                return;
            await ReadAsync(mailId, ct).ConfigureAwait(false);
        }

        public async Task ReadAsync(ulong mailId, CancellationToken ct)
        {
            if (mailId == 0 || !Begin(mailId))
                return;
            var gen = _generation;
            var playerId = _session.PlayerId;
            try
            {
                var rsp = await _request(new GameRequest
                {
                    MailRead = new MailReadReq
                    {
                        PlayerId = _session.PlayerId,
                        MailId = mailId,
                        IdempotencyKey = Stable("read", mailId)
                    }
                }, ct).ConfigureAwait(false);
                if (!StillCurrent(playerId, gen))
                    return;
                var body = rsp.MailRead;
                if (body != null && rsp.Ok && body.Ok)
                {
                    Forget("read", mailId);
                    MarkRead(mailId);
                    NoteVersion(body.MailboxVersion, body.ServerTimeUtc);
                    Page.LastNotice = "已读";
                    Page.LastError = "";
                    return;
                }

                Forget("read", mailId);
                Page.LastError = GameErrorCatalog.FormatUi(body?.ErrorCode, body?.Message ?? rsp.Message);
            }
            catch (Exception ex) when (IsTimeout(ex))
            {
                if (gen == _generation)
                    Page.LastNotice = "已读结果未确认，正在同步";
            }
            finally
            {
                End(mailId);
            }
        }

        public async Task ClaimAsync(ulong mailId, CancellationToken ct)
        {
            if (mailId == 0 || !Begin(mailId))
                return;
            var gen = _generation;
            var playerId = _session.PlayerId;
            try
            {
                if (Page.Selected?.Brief != null && Page.Selected.Brief.MailId == mailId)
                {
                    if (!Allows(Page.Selected, "claim"))
                    {
                        Page.LastError = "当前不能领取";
                        return;
                    }

                    if (string.Equals(Page.Selected.Brief.AttachmentState, "CLAIMING", StringComparison.OrdinalIgnoreCase))
                    {
                        MarkReward(mailId, UnknownNotice);
                        Page.LastNotice = UnknownNotice;
                        Page.LastError = "";
                        await RefreshAsync(ct).ConfigureAwait(false);
                        if (_session.PlayerId == playerId)
                            await FinishClaimRecoveryAsync(mailId, playerId, ct).ConfigureAwait(false);
                        return;
                    }
                }

                var key = Stable("claim", mailId);
                var rsp = await _request(new GameRequest
                {
                    MailClaim = new MailClaimReq
                    {
                        PlayerId = _session.PlayerId,
                        MailId = mailId,
                        IdempotencyKey = key,
                        TraceId = key
                    }
                }, ct).ConfigureAwait(false);
                if (!StillCurrent(playerId, gen))
                    return;
                var body = rsp.MailClaim;
                var result = body?.Result;
                var code = FirstCode(result?.ErrorCode, body?.ErrorCode, ProtocolMapper.ExtractErrorCode(rsp));
                if (IsUncertain(code))
                {
                    MarkReward(mailId, UnknownNotice);
                    Page.LastNotice = UnknownNotice;
                    Page.LastError = "";
                    await RefreshAsync(ct).ConfigureAwait(false);
                    if (_session.PlayerId == playerId)
                        await FinishClaimRecoveryAsync(mailId, playerId, ct).ConfigureAwait(false);
                    return;
                }

                if (IsBagFull(code))
                {
                    Forget("claim", mailId);
                    MarkReward(mailId, BagFullNotice);
                    Page.LastNotice = BagFullNotice;
                    Page.LastError = BagFullNotice;
                    return;
                }

                if (IsInvalidAttachment(code))
                {
                    Forget("claim", mailId);
                    var invalid = GameErrorCatalog.FormatUi(code, result?.Message ?? body?.Message ?? rsp.Message);
                    MarkReward(mailId, invalid);
                    Page.LastNotice = invalid;
                    Page.LastError = invalid;
                    return;
                }

                if (IsAlreadyClaimed(code))
                {
                    if (string.Equals(result?.AttachmentState, "CLAIMED", StringComparison.OrdinalIgnoreCase))
                        ApplyClaimed(mailId, result.AttachmentState);
                    NoteVersion(body?.MailboxVersion ?? 0, body?.ServerTimeUtc ?? 0);
                    var claimedId = result != null && result.MailId != 0 ? result.MailId : mailId;
                    TryConsumeReceipt(playerId, claimedId, result?.Receipt);
                    await RefreshAsync(ct).ConfigureAwait(false);
                    if (_session.PlayerId != playerId)
                        return;
                    await PullBagAsync(playerId, ct).ConfigureAwait(false);
                    if (Receipt(mailId) == null)
                        await FetchReceiptAsync(mailId, playerId, ct).ConfigureAwait(false);
                    NoteMailWithoutReceipt(mailId);
                    return;
                }

                if (rsp.Ok && body != null && body.Ok && (result == null || result.Ok))
                {
                    NoteVersion(body.MailboxVersion, body.ServerTimeUtc);
                    ApplyClaimed(mailId, result?.AttachmentState);
                    var claimedId = result != null && result.MailId != 0 ? result.MailId : mailId;
                    TryConsumeReceipt(playerId, claimedId, result?.Receipt);
                    await RefreshAsync(ct).ConfigureAwait(false);
                    if (_session.PlayerId != playerId)
                        return;
                    await PullBagAsync(playerId, ct).ConfigureAwait(false);
                    if (Receipt(mailId) == null)
                        await FetchReceiptAsync(mailId, playerId, ct).ConfigureAwait(false);
                    NoteMailWithoutReceipt(mailId);
                    return;
                }

                Forget("claim", mailId);
                MarkReward(mailId, RetryNotice);
                Page.LastNotice = RetryNotice;
                Page.LastError = GameErrorCatalog.FormatUi(string.IsNullOrEmpty(code) ? GameMeshErrorCode.ServerError : code,
                    result?.Message ?? body?.Message ?? rsp.Message);
            }
            catch (Exception ex) when (IsTimeout(ex))
            {
                if (StillCurrent(playerId, gen))
                {
                    MarkReward(mailId, UnknownNotice);
                    Page.LastNotice = UnknownNotice;
                    Page.LastError = "";
                    await RefreshAsync(ct).ConfigureAwait(false);
                    if (_session.PlayerId == playerId)
                        await FinishClaimRecoveryAsync(mailId, playerId, ct).ConfigureAwait(false);
                }
            }
            finally
            {
                End(mailId);
            }
        }

        public async Task BatchClaimAsync(CancellationToken ct)
        {
            var ids = CollectClaimIds();
            if (ids.Count == 0 || !Begin(0))
                return;
            var gen = _generation;
            var playerId = _session.PlayerId;
            try
            {
                Page.ClaimFailures.Clear();
                var ok = 0;
                var keepKey = false;
                for (var offset = 0; offset < ids.Count; offset += BatchLimit)
                {
                    var count = Math.Min(BatchLimit, ids.Count - offset);
                    var chunk = new List<ulong>(count);
                    for (var i = 0; i < count; i++)
                        chunk.Add(ids[offset + i]);
                    var prefix = StableSet("batch-claim", chunk);
                    var req = new MailBatchClaimReq
                    {
                        PlayerId = _session.PlayerId,
                        IdempotencyKeyPrefix = prefix,
                        TraceId = prefix
                    };
                    for (var i = 0; i < chunk.Count; i++)
                        req.MailIds.Add(chunk[i]);
                    var rsp = await _request(new GameRequest { MailBatchClaim = req }, ct).ConfigureAwait(false);
                    if (!StillCurrent(playerId, gen))
                        return;
                    var body = rsp.MailBatchClaim;
                    if (body == null || body.Results.Count == 0)
                    {
                        keepKey = true;
                        Page.LastError = GameErrorCatalog.FormatUi(body?.ErrorCode, body?.Message ?? rsp.Message);
                        continue;
                    }

                    NoteVersion(body.MailboxVersion, body.ServerTimeUtc);
                    var rowFail = false;
                    var rowUncertain = IsUncertain(FirstCode(body.ErrorCode, ProtocolMapper.ExtractErrorCode(rsp)));
                    for (var i = 0; i < body.Results.Count; i++)
                    {
                        var row = body.Results[i];
                        if (row == null)
                            continue;
                        if (row.Ok)
                        {
                            ok++;
                            ApplyClaimed(row.MailId, row.AttachmentState);
                            if (!TryConsumeReceipt(playerId, row.MailId, row.Receipt))
                                MarkReward(row.MailId, AwaitingCreditNotice);
                            rowUncertain = true;
                            continue;
                        }

                        if (row.Receipt != null)
                            TryConsumeReceipt(playerId, row.MailId, row.Receipt);

                        if (IsUncertain(row.ErrorCode))
                        {
                            rowUncertain = true;
                            MarkReward(row.MailId, UnknownNotice);
                        }
                        else if (IsBagFull(row.ErrorCode))
                        {
                            rowFail = true;
                            MarkReward(row.MailId, BagFullNotice);
                        }
                        else
                        {
                            rowFail = true;
                            MarkReward(row.MailId, RetryNotice);
                        }

                        var reason = IsBagFull(row.ErrorCode)
                            ? BagFullNotice
                            : GameErrorCatalog.FormatUi(row.ErrorCode, row.Message);
                        Page.ClaimFailures.Add("#" + row.MailId + " " + reason);
                    }

                    if (rowFail || rowUncertain || !body.Ok)
                        keepKey = true;
                    else
                        ForgetSet("batch-claim", chunk);
                }

                if (_session.PlayerId == playerId)
                    await PullBagAsync(playerId, ct).ConfigureAwait(false);
                RefreshReceiptLabels();
                if (ok > 0 && Page.ClaimFailures.Count > 0)
                    Page.LastNotice = "邮件领取 " + ok + "，失败 " + Page.ClaimFailures.Count;
                else if (ok > 0 && Page.LastNotice != ConfirmedNotice &&
                         Page.LastNotice != AwaitingCreditNotice && Page.LastNotice != UnverifiableNotice)
                    Page.LastNotice = AwaitingCreditNotice;
                else if (keepKey)
                    Page.LastNotice = UnknownNotice;
                else
                    Page.LastNotice = "失败 " + Page.ClaimFailures.Count;
                Page.LastError = Page.ClaimFailures.Count > 0 ? Page.ClaimFailures[0] : "";
                var notice = Page.LastNotice;
                var failures = new List<string>(Page.ClaimFailures);
                await RefreshAsync(ct).ConfigureAwait(false);
                if (Page.LastNotice.Length == 0)
                    Page.LastNotice = notice;
                if (Page.ClaimFailures.Count == 0 && failures.Count > 0)
                {
                    Page.ClaimFailures.AddRange(failures);
                    Page.LastError = failures[0];
                }
            }
            catch (Exception ex) when (IsTimeout(ex))
            {
                if (StillCurrent(playerId, gen))
                {
                    Page.LastNotice = UnknownNotice;
                    Page.LastError = "";
                }
            }
            finally
            {
                End(0);
            }
        }

        public async Task FavoriteAsync(ulong mailId, bool favorite, CancellationToken ct)
        {
            if (mailId == 0 || !Begin(mailId))
                return;
            var gen = _generation;
            var playerId = _session.PlayerId;
            try
            {
                var action = FavoriteAction(Page.Selected);
                if (Page.Selected?.Brief == null || Page.Selected.Brief.MailId != mailId || action == null ||
                    (action == "FAVORITE") != favorite)
                {
                    Page.LastError = "当前不能收藏";
                    return;
                }

                var rsp = await _request(new GameRequest
                {
                    MailFavorite = new MailFavoriteReq
                    {
                        PlayerId = _session.PlayerId,
                        MailId = mailId,
                        Favorite = favorite,
                        IdempotencyKey = Stable("favorite", mailId)
                    }
                }, ct).ConfigureAwait(false);
                if (!StillCurrent(playerId, gen))
                    return;
                var body = rsp.MailFavorite;
                if (body != null && rsp.Ok && body.Ok)
                {
                    Forget("favorite", mailId);
                    NoteVersion(body.MailboxVersion, body.ServerTimeUtc);
                    SetFavorite(mailId, body.IsFavorite);
                    Page.LastNotice = body.IsFavorite ? "已收藏" : "已取消收藏";
                    Page.LastError = "";
                    return;
                }

                Forget("favorite", mailId);
                Page.LastError = GameErrorCatalog.FormatUi(body?.ErrorCode, body?.Message ?? rsp.Message);
            }
            finally
            {
                End(mailId);
            }
        }

        public async Task BatchReadAsync(CancellationToken ct)
        {
            var ids = CollectUnreadIds();
            if (ids.Count == 0)
                return;
            var playerId = _session.PlayerId;
            var req = new MailBatchReadReq
            {
                PlayerId = _session.PlayerId,
                IdempotencyKey = StableSet("batch-read", ids)
            };
            for (var i = 0; i < ids.Count; i++)
                req.MailIds.Add(ids[i]);
            var gen = _generation;
            var rsp = await _request(new GameRequest { MailBatchRead = req }, ct).ConfigureAwait(false);
            if (!StillCurrent(playerId, gen))
                return;
            var body = rsp.MailBatchRead;
            if (body == null || !rsp.Ok || !body.Ok)
            {
                ForgetSet("batch-read", ids);
                Page.LastError = GameErrorCatalog.FormatUi(body?.ErrorCode, body?.Message ?? rsp.Message);
                return;
            }

            ForgetSet("batch-read", ids);
            NoteVersion(body.MailboxVersion, body.ServerTimeUtc);
            if (body.SuccessCount == (uint)ids.Count)
            {
                for (var i = 0; i < ids.Count; i++)
                    MarkRead(ids[i]);
            }

            Page.LastNotice = "已读 " + body.SuccessCount;
            Page.LastError = "";
        }

        public async Task BatchDeleteAsync(IList<ulong> mailIds, CancellationToken ct)
        {
            if (mailIds == null || mailIds.Count == 0)
                return;
            var req = new MailBatchDeleteReq { PlayerId = _session.PlayerId };
            var n = 0;
            for (var i = 0; i < mailIds.Count && n < BatchLimit; i++)
            {
                if (mailIds[i] == 0 || HasUnclaimed(mailIds[i]))
                    continue;
                req.MailIds.Add(mailIds[i]);
                n++;
            }

            if (req.MailIds.Count == 0)
            {
                Page.LastError = "未领取附件不能删除";
                return;
            }

            req.IdempotencyKey = StableSet("batch-delete", req.MailIds);

            var gen = _generation;
            var playerId = _session.PlayerId;
            var rsp = await _request(new GameRequest { MailBatchDelete = req }, ct).ConfigureAwait(false);
            if (!StillCurrent(playerId, gen))
                return;
            var body = rsp.MailBatchDelete;
            if (body == null || !rsp.Ok || !body.Ok)
            {
                ForgetSet("batch-delete", req.MailIds);
                Page.LastError = GameErrorCatalog.FormatUi(body?.ErrorCode, body?.Message ?? rsp.Message);
                return;
            }

            ForgetSet("batch-delete", req.MailIds);
            NoteVersion(body.MailboxVersion, body.ServerTimeUtc);
            for (var i = 0; i < req.MailIds.Count; i++)
            {
                if (!ContainsId(body.FailedMailIds, req.MailIds[i]))
                    RemoveMail(req.MailIds[i]);
            }

            Page.LastNotice = "已删除 " + body.SuccessCount;
            Page.LastError = body.FailedMailIds.Count > 0 ? "有邮件未能删除" : "";
        }

        public const string ClaimedNotice = "邮件已领取，背包同步中";
        public const string ConfirmedNotice = "已到账";
        public const string AwaitingCreditNotice = "邮件已领取，到账确认中";
        public const string UnverifiableNotice = "领取状态已确认，到账凭证暂不可验证";
        public const string UnknownNotice = "领取结果未确认，正在同步背包";
        public const string BagFullNotice = "背包已满";
        public const string RetryNotice = "领取失败，可重试";

        public ClaimReceipt Receipt(ulong mailId)
        {
            ClaimReceipt receipt;
            return _receipts.TryGetValue(mailId, out receipt) && receipt.PlayerId == _session.PlayerId ? receipt : null;
        }

        public bool AcceptReceipt(ulong playerId, ulong mailId, string claimTxId, ulong assetVersionAtClaim, IList<MailItemGrant> grants)
        {
            if (playerId == 0 || playerId != _session.PlayerId || mailId == 0)
                return false;
            if (string.IsNullOrEmpty(claimTxId) || assetVersionAtClaim == 0 || grants == null || grants.Count == 0)
                return false;
            ClaimReceipt existing;
            if (_receipts.TryGetValue(mailId, out existing))
            {
                if (existing.PlayerId == playerId)
                    return false;
                _receipts.Remove(mailId);
            }

            var receipt = new ClaimReceipt
            {
                PlayerId = playerId,
                MailId = mailId,
                ClaimTxId = claimTxId,
                AssetVersionAtClaim = assetVersionAtClaim,
                ClaimState = "CLAIMED"
            };
            for (var i = 0; i < grants.Count; i++)
                receipt.Grants.Add(grants[i]);
            _receipts[mailId] = receipt;
            ApplyReceiptLabel(mailId);
            return true;
        }

        public string RewardLabel(ulong mailId)
        {
            string label;
            return _reward.TryGetValue(mailId, out label) ? label : "";
        }

        public bool ObserveBag(FullStateSnapshotRsp snap)
        {
            if (!Bag.TryReplace(snap, _session.PlayerId, out _))
                return false;
            RefreshReceiptLabels();
            return true;
        }

        public async Task ReconcileBagAsync(CancellationToken ct)
        {
            var playerId = _session.PlayerId;
            await PullBagAsync(playerId, ct).ConfigureAwait(false);
            if (_session.PlayerId != playerId)
                return;
            RefreshReceiptLabels();
        }

        public static string FavoriteAction(MailDetail detail)
        {
            if (detail?.Brief == null)
                return null;
            if (detail.Brief.IsFavorite)
                return Allows(detail, "UNFAVORITE") ? "UNFAVORITE" : null;
            return Allows(detail, "FAVORITE") ? "FAVORITE" : null;
        }

        public static bool Allows(MailDetail detail, string action)
        {
            if (detail == null || string.IsNullOrEmpty(action))
                return false;
            for (var i = 0; i < detail.AllowedActions.Count; i++)
            {
                if (string.Equals(detail.AllowedActions[i], action, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        public static bool IsUnread(MailBrief brief)
        {
            return brief != null && string.Equals(brief.ReadState, "UNREAD", StringComparison.OrdinalIgnoreCase);
        }

        public static string SentLabel(long sentAt)
        {
            if (sentAt <= 0)
                return "";
            try
            {
                return DateTimeOffset.FromUnixTimeSeconds(sentAt).UtcDateTime.ToString("MM-dd HH:mm",
                    System.Globalization.CultureInfo.InvariantCulture);
            }
            catch (ArgumentOutOfRangeException)
            {
                return "";
            }
        }

        public static string ExpireLabel(long expireAt, long serverUtc)
        {
            if (expireAt <= 0 || serverUtc <= 0)
                return "";
            var left = expireAt - serverUtc;
            if (left <= 0)
                return "已过期";
            if (left <= ExpiringWindowSeconds)
                return "即将过期";
            return "";
        }

        public static string AttachmentLine(MailAttachmentDto row)
        {
            if (row == null)
                return "";
            var name = row.AssetId == 0 ? "未知物品" : "物品 #" + row.AssetId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var line = name + " x" + row.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!string.IsNullOrEmpty(row.BindType))
                line += " " + row.BindType;
            return line;
        }

        public static string AttachmentLines(IList<MailAttachmentDto> rows)
        {
            if (rows == null || rows.Count == 0)
                return "";
            var text = "";
            for (var i = 0; i < rows.Count; i++)
            {
                var line = AttachmentLine(rows[i]);
                if (line.Length == 0)
                    continue;
                if (text.Length > 0)
                    text += "\n";
                text += line;
            }

            return text;
        }

        public static string PlainText(string body)
        {
            if (string.IsNullOrEmpty(body))
                return "";
            return body.Replace("<", "＜").Replace(">", "＞");
        }

        public string PeekOrCreateSendOpId()
        {
            if (string.IsNullOrEmpty(_pendingSendOpId))
                _pendingSendOpId = Guid.NewGuid().ToString("N");
            return _pendingSendOpId;
        }

        public void ClearSendOpId() => _pendingSendOpId = null;

        public async Task<string> SendAsync(ulong receiverId, string title, string body, CancellationToken ct)
        {
            if (receiverId == 0 || string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(body))
                return "title/body/receiver required";
            if (title.Length > 64 || body.Length > 2000)
                return "title or body too long";
            if (receiverId == _session.PlayerId)
                return GameErrorCatalog.FormatUi("ERR_MAIL_SELF");

            var opId = PeekOrCreateSendOpId();
            var rsp = await _request(new GameRequest
            {
                PlayerMailSend = new PlayerMailSendReq
                {
                    SenderPlayerId = _session.PlayerId,
                    ReceiverPlayerId = receiverId,
                    Title = title,
                    Body = body,
                    OperationId = opId
                }
            }, ct).ConfigureAwait(false);

            var send = rsp.PlayerMailSend;
            if (send != null && (send.Ok || send.IdempotentHit))
            {
                Page.LastSentMailId = send.MailId;
                Page.LastIdempotentHit = send.IdempotentHit;
                ClearSendOpId();
                return "";
            }

            var code = send?.ErrorCode ?? ProtocolMapper.ExtractErrorCode(rsp);
            if (code == "ERR_MAIL_RATE_LIMIT")
                return GameErrorCatalog.FormatUi(code, send?.Message ?? rsp.Message);
            ClearSendOpId();
            return GameErrorCatalog.FormatUi(
                string.IsNullOrEmpty(code) ? GameMeshErrorCode.ServerError : code,
                send?.Message ?? rsp.Message);
        }

        public long EstimatedServerUtc()
        {
            if (Page.ServerTimeUtc <= 0)
                return 0;
            var mark = ReadUnscaledTime();
            var elapsed = (mark >= 0f ? mark : 0f) - _serverMarkUnscaled;
            if (elapsed < 0f)
                elapsed = 0f;
            return Page.ServerTimeUtc + (long)elapsed;
        }

        bool StillCurrent(ulong playerId, int gen)
        {
            return gen == _generation && playerId == _session.PlayerId;
        }

        bool NoteVersion(long version, long serverUtc)
        {
            if (version > 0)
                Page.MailboxVersion = version;
            if (serverUtc > 0)
            {
                Page.ServerTimeUtc = serverUtc;
                var mark = ReadUnscaledTime();
                if (mark >= 0f)
                    _serverMarkUnscaled = mark;
            }

            return true;
        }

        void ApplySummary(MailboxSummaryRsp summary)
        {
            Page.UnreadTotal = (int)summary.UnreadSystem + (int)summary.UnreadActivity;
            Page.Unclaimed = (int)summary.UnclaimedAttachment;
            Page.HasBadge = Page.UnreadTotal > 0 || Page.Unclaimed > 0;
        }

        static void AddUnique(List<MailBrief> acc, MailBrief mail)
        {
            if (mail == null || mail.MailId == 0)
                return;
            for (var i = 0; i < acc.Count; i++)
            {
                if (acc[i].MailId != mail.MailId)
                    continue;
                acc[i] = mail;
                return;
            }

            acc.Add(mail);
        }

        bool Begin(ulong mailId) => _busy.Add(mailId);

        void End(ulong mailId) => _busy.Remove(mailId);

        string StableSet(string kind, IList<ulong> ids)
        {
            return Stable(SetKind(kind, ids), 0);
        }

        void ForgetSet(string kind, IList<ulong> ids)
        {
            Forget(SetKind(kind, ids), 0);
        }

        static string SetKind(string kind, IList<ulong> ids)
        {
            var copy = new List<ulong>();
            if (ids != null)
            {
                for (var i = 0; i < ids.Count; i++)
                {
                    if (ids[i] != 0)
                        copy.Add(ids[i]);
                }
            }

            copy.Sort();
            var text = kind ?? "";
            for (var i = 0; i < copy.Count; i++)
                text += ":" + copy[i].ToString(System.Globalization.CultureInfo.InvariantCulture);
            return text;
        }

        string Stable(string kind, ulong mailId)
        {
            var key = _session.PlayerId.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" +
                      kind + ":" + mailId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!_keys.TryGetValue(key, out var id))
            {
                id = Guid.NewGuid().ToString("N");
                _keys[key] = id;
            }

            return id;
        }

        void Forget(string kind, ulong mailId)
        {
            _keys.Remove(_session.PlayerId.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" +
                         kind + ":" + mailId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        void MarkRead(ulong mailId)
        {
            for (var i = 0; i < Page.Mails.Count; i++)
            {
                var brief = Page.Mails[i];
                if (brief == null || brief.MailId != mailId || !IsUnread(brief))
                    continue;
                brief.ReadState = "READ";
                if (Page.UnreadTotal > 0)
                    Page.UnreadTotal--;
            }

            if (Page.Selected?.Brief != null && Page.Selected.Brief.MailId == mailId)
                Page.Selected.Brief.ReadState = "READ";
            Page.HasBadge = Page.UnreadTotal > 0 || Page.Unclaimed > 0;
        }

        void ApplyClaimed(ulong mailId, string state)
        {
            var next = string.IsNullOrEmpty(state) ? "CLAIMED" : state;
            for (var i = 0; i < Page.Mails.Count; i++)
            {
                var brief = Page.Mails[i];
                if (brief == null || brief.MailId != mailId)
                    continue;
                if (IsOpenAttachment(brief.AttachmentState) && !IsOpenAttachment(next) && Page.Unclaimed > 0)
                    Page.Unclaimed--;
                brief.AttachmentState = next;
            }

            if (Page.Selected?.Brief != null && Page.Selected.Brief.MailId == mailId)
                Page.Selected.Brief.AttachmentState = next;
            Page.HasBadge = Page.UnreadTotal > 0 || Page.Unclaimed > 0;
        }

        void SetFavorite(ulong mailId, bool favorite)
        {
            for (var i = 0; i < Page.Mails.Count; i++)
            {
                if (Page.Mails[i] != null && Page.Mails[i].MailId == mailId)
                    Page.Mails[i].IsFavorite = favorite;
            }

            if (Page.Selected?.Brief != null && Page.Selected.Brief.MailId == mailId)
                Page.Selected.Brief.IsFavorite = favorite;
        }

        void RemoveMail(ulong mailId)
        {
            for (var i = Page.Mails.Count - 1; i >= 0; i--)
            {
                if (Page.Mails[i] != null && Page.Mails[i].MailId == mailId)
                    Page.Mails.RemoveAt(i);
            }

            if (Page.Selected?.Brief != null && Page.Selected.Brief.MailId == mailId)
                Page.Selected = null;
        }

        List<ulong> CollectClaimIds()
        {
            var ids = new List<ulong>();
            for (var i = 0; i < Page.Mails.Count; i++)
            {
                var brief = Page.Mails[i];
                if (brief == null || !brief.HasAttachment || !IsOpenAttachment(brief.AttachmentState))
                    continue;
                if (string.Equals(brief.AttachmentState, "CLAIMING", StringComparison.OrdinalIgnoreCase))
                    continue;
                ids.Add(brief.MailId);
            }

            return ids;
        }

        List<ulong> CollectUnreadIds()
        {
            var ids = new List<ulong>();
            for (var i = 0; i < Page.Mails.Count && ids.Count < BatchLimit; i++)
            {
                if (Page.Mails[i] != null && IsUnread(Page.Mails[i]))
                    ids.Add(Page.Mails[i].MailId);
            }

            return ids;
        }

        bool HasUnclaimed(ulong mailId)
        {
            if (Page.Selected?.Brief != null && Page.Selected.Brief.MailId == mailId)
            {
                return Page.Selected.Brief.HasAttachment &&
                       IsOpenAttachment(Page.Selected.Brief.AttachmentState) &&
                       !Allows(Page.Selected, "delete");
            }

            for (var i = 0; i < Page.Mails.Count; i++)
            {
                var brief = Page.Mails[i];
                if (brief == null || brief.MailId != mailId)
                    continue;
                return brief.HasAttachment && IsOpenAttachment(brief.AttachmentState);
            }

            return false;
        }

        static bool IsOpenAttachment(string state)
        {
            return string.IsNullOrEmpty(state) ||
                   string.Equals(state, "UNCLAIMED", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(state, "PARTIAL", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(state, "CLAIMING", StringComparison.OrdinalIgnoreCase);
        }

        static bool ContainsId(IList<ulong> ids, ulong id)
        {
            if (ids == null)
                return false;
            for (var i = 0; i < ids.Count; i++)
            {
                if (ids[i] == id)
                    return true;
            }

            return false;
        }

        static string FirstCode(params string[] codes)
        {
            for (var i = 0; i < codes.Length; i++)
            {
                if (!string.IsNullOrEmpty(codes[i]))
                    return codes[i];
            }

            return "";
        }

        async Task FinishClaimRecoveryAsync(ulong mailId, ulong playerId, CancellationToken ct)
        {
            await PullBagAsync(playerId, ct).ConfigureAwait(false);
            if (_session.PlayerId != playerId)
                return;
            if (Receipt(mailId) == null)
                await FetchReceiptAsync(mailId, playerId, ct).ConfigureAwait(false);
            if (Receipt(mailId) != null)
                ApplyReceiptLabel(mailId);
            else if (MailLooksClaimed(mailId))
                NoteMailWithoutReceipt(mailId);
        }

        void NoteMailWithoutReceipt(ulong mailId)
        {
            if (Receipt(mailId) != null)
            {
                ApplyReceiptLabel(mailId);
                return;
            }

            if (RewardLabel(mailId) == UnverifiableNotice)
                return;

            if (!MailLooksClaimed(mailId))
            {
                MarkReward(mailId, UnknownNotice);
                Page.LastNotice = UnknownNotice;
                Page.LastError = "";
                return;
            }

            MarkReward(mailId, AwaitingCreditNotice);
            Page.LastNotice = AwaitingCreditNotice;
            Page.LastError = "";
        }

        bool TryConsumeReceipt(ulong playerId, ulong mailId, MailClaimReceipt receipt)
        {
            if (playerId != _session.PlayerId || mailId == 0 || receipt == null)
                return false;
            if (!receipt.Verifiable || !string.Equals(receipt.ClaimState, "CLAIMED", StringComparison.OrdinalIgnoreCase))
            {
                if (Receipt(mailId) == null)
                {
                    MarkReward(mailId, UnverifiableNotice);
                    Page.LastNotice = UnverifiableNotice;
                }

                return false;
            }

            var grants = new List<MailItemGrant>();
            for (var i = 0; i < receipt.Grants.Count; i++)
            {
                var grant = receipt.Grants[i];
                if (grant == null || grant.Count == 0)
                    continue;
                grants.Add(new MailItemGrant { AssetId = grant.AssetId, Count = grant.Count });
            }

            return AcceptReceipt(playerId, mailId, receipt.ClaimTxId, receipt.AssetVersionAtClaim, grants);
        }

        async Task FetchReceiptAsync(ulong mailId, ulong playerId, CancellationToken ct)
        {
            var gen = _generation;
            GameResponse rsp;
            try
            {
                rsp = await _request(new GameRequest
                {
                    MailGet = new MailGetReq { PlayerId = playerId, MailId = mailId }
                }, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsTimeout(ex))
            {
                return;
            }

            if (!StillCurrent(playerId, gen) || rsp?.MailGet?.Mail == null)
                return;
            var detail = rsp.MailGet.Mail;
            var id = detail.Brief != null && detail.Brief.MailId != 0 ? detail.Brief.MailId : mailId;
            TryConsumeReceipt(playerId, id, detail.Receipt);
        }

        void RefreshReceiptLabels()
        {
            var ids = new List<ulong>(_receipts.Keys);
            for (var i = 0; i < ids.Count; i++)
                ApplyReceiptLabel(ids[i]);
        }

        void ApplyReceiptLabel(ulong mailId)
        {
            ClaimReceipt receipt;
            if (!_receipts.TryGetValue(mailId, out receipt) || receipt.PlayerId != _session.PlayerId)
                return;
            if (!receipt.SeenSufficientSnapshot && Bag.HasSnapshot && Bag.PlayerId == receipt.PlayerId &&
                Bag.AssetVersion >= receipt.AssetVersionAtClaim)
                receipt.SeenSufficientSnapshot = true;
            if (receipt.SeenSufficientSnapshot)
            {
                Forget("claim", mailId);
                MarkReward(mailId, ConfirmedNotice);
                Page.LastNotice = ConfirmedNotice;
                Page.LastError = "";
                return;
            }

            MarkReward(mailId, AwaitingCreditNotice);
            Page.LastNotice = AwaitingCreditNotice;
            Page.LastError = "";
        }

        async Task<bool> PullBagAsync(ulong playerId, CancellationToken ct)
        {
            var gen = _generation;
            GameResponse rsp;
            try
            {
                rsp = await _request(new GameRequest
                {
                    WorldSnapshot = new WorldSnapshotReq { PlayerId = playerId }
                }, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsTimeout(ex))
            {
                return false;
            }

            if (!StillCurrent(playerId, gen) || rsp == null)
                return false;
            var advanced = false;
            Bag.TryReplace(rsp.FullSnapshot, playerId, out advanced);
            return advanced;
        }

        void MarkReward(ulong mailId, string label)
        {
            if (mailId == 0 || string.IsNullOrEmpty(label))
                return;
            _reward[mailId] = label;
        }

        bool MailLooksClaimed(ulong mailId)
        {
            if (Page.Selected?.Brief != null && Page.Selected.Brief.MailId == mailId &&
                string.Equals(Page.Selected.Brief.AttachmentState, "CLAIMED", StringComparison.OrdinalIgnoreCase))
                return true;
            for (var i = 0; i < Page.Mails.Count; i++)
            {
                var brief = Page.Mails[i];
                if (brief != null && brief.MailId == mailId &&
                    string.Equals(brief.AttachmentState, "CLAIMED", StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        static bool IsSyncRequired(string code)
        {
            return !string.IsNullOrEmpty(code) &&
                   code.IndexOf("STATE_SYNC", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static bool IsUncertain(string code)
        {
            return IsSyncRequired(code) ||
                   (!string.IsNullOrEmpty(code) &&
                    code.IndexOf("CLAIM_IN_PROGRESS", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        static bool IsBagFull(string code)
        {
            return !string.IsNullOrEmpty(code) &&
                   (code.IndexOf("INVENTORY_FULL", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    code.IndexOf("BAG_FULL", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        static bool IsInvalidAttachment(string code)
        {
            return !string.IsNullOrEmpty(code) &&
                   code.IndexOf("INVALID_ATTACHMENT", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static bool IsAlreadyClaimed(string code)
        {
            return !string.IsNullOrEmpty(code) &&
                   code.IndexOf("ALREADY_CLAIMED", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static bool IsTimeout(Exception ex)
        {
            return ex is TimeoutException || ex is System.IO.IOException;
        }

        static float ReadUnscaledTime()
        {
            try
            {
                return Time.unscaledTime;
            }
            catch (UnityException)
            {
                return -1f;
            }
        }

        public struct MailItemGrant
        {
            public ulong AssetId;
            public uint Count;
        }

        public sealed class ClaimReceipt
        {
            public ulong PlayerId;
            public ulong MailId;
            public string ClaimTxId = "";
            public ulong AssetVersionAtClaim;
            public string ClaimState = "";
            public bool SeenSufficientSnapshot;
            public readonly List<MailItemGrant> Grants = new List<MailItemGrant>();
        }
    }
}
