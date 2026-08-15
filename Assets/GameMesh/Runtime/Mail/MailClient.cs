using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GameMesh.Auth;
using GameMesh.Network;
using GameMesh.Protocol;

namespace GameMesh.Mail
{
    public sealed class MailListPage
    {
        public int Generation;
        public readonly List<MailBrief> Mails = new List<MailBrief>();
        public MailboxSummaryRsp Summary;
        public MailDetail Selected;
        public string LastError = "";
        public int UnreadTotal;
        public ulong LastSentMailId;
        public bool LastIdempotentHit;
    }

    public sealed class MailClient
    {
        readonly GameSession _session;
        readonly Func<GameRequest, CancellationToken, Task<GameResponse>> _request;
        int _generation;
        float _debounceUntil;
        string _pendingSendOpId;

        public MailListPage Page { get; } = new MailListPage();
        public float PollIntervalSeconds = 10f;
        public float DebounceSeconds = 0.4f;
        public bool PanelOpen;

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
            Page.LastSentMailId = 0;
            Page.LastIdempotentHit = false;
            _pendingSendOpId = null;
            _debounceUntil = 0f;
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
            var gen = Interlocked.Increment(ref _generation);
            var summaryReq = new GameRequest
            {
                MailboxSummary = new MailboxSummaryReq { PlayerId = _session.PlayerId }
            };
            var listReq = new GameRequest
            {
                MailList = new MailListReq { PlayerId = _session.PlayerId, Limit = 50 }
            };
            var summary = await _request(summaryReq, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            var list = await _request(listReq, ct).ConfigureAwait(false);
            if (gen != _generation)
                return;
            Page.Generation = gen;
            if (summary.MailboxSummary != null)
            {
                Page.Summary = summary.MailboxSummary;
                var s = summary.MailboxSummary;
                Page.UnreadTotal = (int)(s.UnreadSystem + s.UnreadActivity + s.UnreadSocial + s.UnreadTrade);
            }

            Page.Mails.Clear();
            if (list.MailList != null)
                Page.Mails.AddRange(list.MailList.Mails);
            if (!summary.Ok)
                Page.LastError = GameErrorCatalog.FormatUi(summary.MailboxSummary?.ErrorCode, summary.Message);
            else if (!list.Ok)
                Page.LastError = GameErrorCatalog.FormatUi(list.MailList?.ErrorCode, list.Message);
            else
                Page.LastError = "";
        }

        public async Task<MailDetail> GetAsync(ulong mailId, CancellationToken ct)
        {
            var gen = _generation;
            var rsp = await _request(new GameRequest
            {
                MailGet = new MailGetReq { PlayerId = _session.PlayerId, MailId = mailId }
            }, ct).ConfigureAwait(false);
            if (gen != _generation)
                return null;
            if (rsp.MailGet?.Mail != null)
                Page.Selected = rsp.MailGet.Mail;
            return rsp.MailGet?.Mail;
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
    }
}
