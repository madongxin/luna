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
    }

    public sealed class MailClient
    {
        readonly GameSession _session;
        readonly Func<GameRequest, Task<GameResponse>> _request;
        int _generation;
        float _debounceUntil;
        string _pendingSendOpId;

        public MailListPage Page { get; } = new MailListPage();
        public float PollIntervalSeconds = 10f;
        public float DebounceSeconds = 0.4f;

        public MailClient(GameSession session, Func<GameRequest, Task<GameResponse>> request)
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
            _pendingSendOpId = null;
        }

        public void NotifyMailboxChanged(float now)
        {
            _debounceUntil = now + DebounceSeconds;
        }

        public bool ShouldPoll(float now, float lastPoll, bool panelOpen)
        {
            if (panelOpen && now < _debounceUntil && _debounceUntil > 0f)
                return now >= _debounceUntil;
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
            var summary = await _request(summaryReq).ConfigureAwait(false);
            var list = await _request(listReq).ConfigureAwait(false);
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
                Page.LastError = summary.Message;
            else if (!list.Ok)
                Page.LastError = list.Message;
            else
                Page.LastError = "";
        }

        public async Task<MailDetail> GetAsync(ulong mailId, CancellationToken ct)
        {
            var gen = _generation;
            var rsp = await _request(new GameRequest
            {
                MailGet = new MailGetReq { PlayerId = _session.PlayerId, MailId = mailId }
            }).ConfigureAwait(false);
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

        public Task<string> SendAsync(ulong receiverId, string title, string body, CancellationToken ct)
        {
            if (receiverId == 0 || string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(body))
                return Task.FromResult("title/body/receiver required");
            if (title.Length > 64 || body.Length > 2000)
                return Task.FromResult("title or body too long");
            if (!ProtocolCapabilities.HasPlayerMailSend)
            {
                return Task.FromResult(
                    "current server game.proto has no PlayerMailSendReq; client will not invent MailDeliver");
            }

            PeekOrCreateSendOpId();
            return Task.FromResult("PlayerMailSendReq present but request mapping is not compiled into this proto snapshot");
        }
    }
}
