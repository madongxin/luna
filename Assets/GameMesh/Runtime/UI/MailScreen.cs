using System;
using System.Globalization;
using GameMesh.Bootstrap;
using GameMesh.Mail;
using GameMesh.Network;
using GameMesh.Protocol;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace GameMesh.UI
{
    /// <summary>
    /// Playable inbox. The corner button expands this panel on its own.
    /// </summary>
    public sealed class MailScreen : MonoBehaviour
    {
        public static bool OwnsPointer { get; private set; }

        static MailScreen _instance;
        GameMeshClient _client;
        Font _font;
        GameObject _panel;
        Text _badge;
        Text _summary;
        Text _status;
        RectTransform _rows;
        RectTransform _detail;
        int _signature;
        bool _open;
        ulong _deleteId;
        Image _tabAll;
        Image _tabSystem;
        Image _tabActivity;
        Image _filterUnread;
        Image _filterUnclaimed;
        Image _filterExpiring;
        Image _filterFavorite;

        static readonly Color Idle = new Color(0.18f, 0.22f, 0.28f, 1f);
        static readonly Color On = new Color(0.62f, 0.42f, 0.16f, 1f);

        public static void Ensure(GameMeshClient client)
        {
            if (client == null)
                return;
            if (_instance == null)
            {
                var go = new GameObject("MailScreen");
                DontDestroyOnLoad(go);
                _instance = go.AddComponent<MailScreen>();
            }

            _instance.gameObject.SetActive(true);
            _instance._client = client;
            _instance.BuildOnce();
        }

        void BuildOnce()
        {
            if (_panel != null)
                return;
            _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (_font == null)
                _font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            if (EventSystem.current == null)
            {
                var es = new GameObject("EventSystem");
                DontDestroyOnLoad(es);
                es.AddComponent<EventSystem>();
                es.AddComponent<StandaloneInputModule>();
            }

            var canvasGo = new GameObject("Canvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasGo.transform.SetParent(transform, false);
            var canvas = canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 510;
            var scaler = canvasGo.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);

            var open = MakeButton(canvasGo.transform, "邮箱", new Vector2(1, 1), new Vector2(1, 1),
                new Vector2(-24, -20), new Vector2(168, 48), ToggleOpen);
            open.GetComponent<Image>().color = On;
            _badge = open.GetComponentInChildren<Text>();

            _panel = new GameObject("Panel", typeof(Image));
            _panel.transform.SetParent(canvasGo.transform, false);
            var panelRect = _panel.GetComponent<RectTransform>();
            panelRect.anchorMin = new Vector2(1, 1);
            panelRect.anchorMax = new Vector2(1, 1);
            panelRect.pivot = new Vector2(1, 1);
            panelRect.anchoredPosition = new Vector2(-24, -80);
            panelRect.sizeDelta = new Vector2(440, 680);
            _panel.GetComponent<Image>().color = new Color(0.08f, 0.09f, 0.12f, 0.94f);

            var title = MakeLabel(_panel.transform, "邮箱", 22, TextAnchor.MiddleLeft);
            Place(title.rectTransform, 16, -8, 160, 36);
            MakeButton(_panel.transform, "收起", new Vector2(1, 1), new Vector2(1, 1),
                new Vector2(-12, -8), new Vector2(88, 32), ToggleOpen);

            _summary = MakeLabel(_panel.transform, "", 15, TextAnchor.MiddleLeft);
            Place(_summary.rectTransform, 16, -44, 408, 28);

            var tabs = Row(_panel.transform, 12, -80, 416, 36);
            _tabAll = Tab(tabs, "全部", () => SetCategory(""));
            _tabSystem = Tab(tabs, "系统", () => SetCategory("SYSTEM"));
            _tabActivity = Tab(tabs, "活动", () => SetCategory("ACTIVITY"));

            var filters = Row(_panel.transform, 12, -124, 416, 36);
            _filterUnread = Tab(filters, "未读", () => ToggleFilter(0));
            _filterUnclaimed = Tab(filters, "未领取", () => ToggleFilter(1));
            _filterExpiring = Tab(filters, "即将过期", () => ToggleFilter(2));
            _filterFavorite = Tab(filters, "收藏", () => ToggleFilter(3));

            var actions = Row(_panel.transform, 12, -168, 416, 36);
            Action(actions, "刷新", () => Refresh());
            Action(actions, "一键领取", () =>
            {
                if (_client != null)
                    _ = _client.Mail.BatchClaimAsync(default);
            });
            Action(actions, "批量已读", () =>
            {
                if (_client != null)
                    _ = _client.Mail.BatchReadAsync(default);
            });

            _status = MakeLabel(_panel.transform, "", 15, TextAnchor.UpperLeft);
            Place(_status.rectTransform, 16, -208, 408, 40);
            _status.color = new Color(1f, 0.85f, 0.45f);
            _status.horizontalOverflow = HorizontalWrapMode.Wrap;

            var scrollGo = new GameObject("Scroll", typeof(Image), typeof(ScrollRect));
            scrollGo.transform.SetParent(_panel.transform, false);
            var scrollRect = scrollGo.GetComponent<RectTransform>();
            Place(scrollRect, 12, -252, 416, 240);
            scrollGo.GetComponent<Image>().color = new Color(0, 0, 0, 0.25f);
            var viewport = new GameObject("Viewport", typeof(Image), typeof(Mask));
            viewport.transform.SetParent(scrollGo.transform, false);
            var viewRect = viewport.GetComponent<RectTransform>();
            viewRect.anchorMin = Vector2.zero;
            viewRect.anchorMax = Vector2.one;
            viewRect.offsetMin = Vector2.zero;
            viewRect.offsetMax = Vector2.zero;
            viewport.GetComponent<Image>().color = new Color(1, 1, 1, 0.02f);
            var content = new GameObject("Content", typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
            content.transform.SetParent(viewport.transform, false);
            _rows = content.GetComponent<RectTransform>();
            _rows.anchorMin = new Vector2(0, 1);
            _rows.anchorMax = new Vector2(1, 1);
            _rows.pivot = new Vector2(0.5f, 1);
            var layout = content.GetComponent<VerticalLayoutGroup>();
            layout.childControlHeight = true;
            layout.childControlWidth = true;
            layout.childForceExpandHeight = false;
            layout.childForceExpandWidth = true;
            layout.spacing = 4;
            layout.padding = new RectOffset(6, 6, 6, 6);
            content.GetComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            var scroll = scrollGo.GetComponent<ScrollRect>();
            scroll.viewport = viewRect;
            scroll.content = _rows;
            scroll.horizontal = false;

            var detailGo = new GameObject("Detail", typeof(Image));
            detailGo.transform.SetParent(_panel.transform, false);
            _detail = detailGo.GetComponent<RectTransform>();
            Place(_detail, 12, -500, 416, 168);
            detailGo.GetComponent<Image>().color = new Color(0, 0, 0, 0.28f);

            _panel.SetActive(false);
        }

        void LateUpdate()
        {
            var mail = _client != null ? _client.Mail : null;
            if (mail == null || _panel == null)
            {
                OwnsPointer = false;
                return;
            }

            var badge = Badge(mail);
            if (_badge != null && _badge.text != badge)
                _badge.text = badge;
            Paint(mail);
            OwnsPointer = _open;
            if (!_open)
                return;
            var sig = Signature(mail);
            if (sig == _signature)
                return;
            _signature = sig;
            Rebuild(mail);
        }

        public static void Collapse()
        {
            if (_instance == null || !_instance._open)
                return;
            _instance._open = false;
            if (_instance._panel != null)
                _instance._panel.SetActive(false);
            if (_instance._client != null && _instance._client.Mail != null)
                _instance._client.Mail.PanelOpen = false;
            _instance._signature = 0;
        }

        void ToggleOpen()
        {
            _open = !_open;
            if (_open)
                FriendScreen.Collapse();
            _panel.SetActive(_open);
            if (_client != null && _client.Mail != null)
                _client.Mail.PanelOpen = _open;
            _signature = 0;
            if (_open)
                Refresh();
        }

        void Refresh()
        {
            if (_client == null)
                return;
            _ = _client.Mail.RefreshAsync(default);
            _ = _client.Mail.ReconcileBagAsync(default);
        }

        void SetCategory(string category)
        {
            if (_client == null)
                return;
            _client.Mail.Category = category ?? "";
            _signature = 0;
            Refresh();
        }

        void ToggleFilter(int index)
        {
            if (_client == null)
                return;
            var mail = _client.Mail;
            if (index == 0)
                mail.FilterUnread = !mail.FilterUnread;
            else if (index == 1)
                mail.FilterUnclaimed = !mail.FilterUnclaimed;
            else if (index == 2)
                mail.FilterExpiring = !mail.FilterExpiring;
            else
                mail.FilterFavorite = !mail.FilterFavorite;
            _signature = 0;
            Refresh();
        }

        void Paint(MailClient mail)
        {
            var category = mail.Category ?? "";
            Tint(_tabAll, category.Length == 0);
            Tint(_tabSystem, category == "SYSTEM");
            Tint(_tabActivity, category == "ACTIVITY");
            Tint(_filterUnread, mail.FilterUnread);
            Tint(_filterUnclaimed, mail.FilterUnclaimed);
            Tint(_filterExpiring, mail.FilterExpiring);
            Tint(_filterFavorite, mail.FilterFavorite);
        }

        static void Tint(Image image, bool on)
        {
            if (image != null)
                image.color = on ? On : Idle;
        }

        static string Badge(MailClient mail)
        {
            var page = mail.Page;
            if (page.UnreadTotal > 0)
                return "邮箱 (" + page.UnreadTotal.ToString(CultureInfo.InvariantCulture) + ")";
            if (page.Unclaimed > 0)
                return "邮箱 (附件)";
            return "邮箱";
        }

        void Rebuild(MailClient mail)
        {
            var page = mail.Page;
            var capacity = page.Summary == null
                ? ""
                : "    " + page.Summary.CurrentCount.ToString(CultureInfo.InvariantCulture) + "/" +
                  page.Summary.MaxCapacity.ToString(CultureInfo.InvariantCulture);
            _summary.text = "未读 " + page.UnreadTotal.ToString(CultureInfo.InvariantCulture) +
                            "    未领取 " + page.Unclaimed.ToString(CultureInfo.InvariantCulture) +
                            "    " + page.Mails.Count.ToString(CultureInfo.InvariantCulture) + " 封" + capacity;
            var status = !string.IsNullOrEmpty(page.LastError) ? page.LastError : page.LastNotice ?? "";
            if (page.ClaimFailures.Count > 0 && string.IsNullOrEmpty(status))
                status = page.ClaimFailures[0];
            if (mail.Loading && page.Mails.Count > 0)
                status = string.IsNullOrEmpty(status) ? "分页中" : "分页中\n" + status;
            if (_client.Connection != null && _client.Connection.State == ConnectionState.Disconnected)
                status = string.IsNullOrEmpty(status) ? "网络断开，显示待同步" : "网络断开，显示待同步\n" + status;
            _status.text = status;

            for (var i = _rows.childCount - 1; i >= 0; i--)
                Destroy(_rows.GetChild(i).gameObject);
            if (page.Mails.Count == 0)
            {
                AddLine(_rows, mail.Loading ? "加载中" : "还没有邮件。");
            }
            else
            {
                var now = mail.EstimatedServerUtc();
                for (var i = 0; i < page.Mails.Count; i++)
                {
                    var brief = page.Mails[i];
                    if (brief == null)
                        continue;
                    var id = brief.MailId;
                    var sent = MailClient.SentLabel(brief.SentAt);
                    var flags = (sent.Length > 0 ? sent + " " : "") +
                                (MailClient.IsUnread(brief) ? "未读 " : "") +
                                AttachmentFlag(brief) +
                                (brief.IsFavorite ? "收藏 " : "") +
                                MailClient.ExpireLabel(brief.ExpireAt, now);
                    AddMailButton((brief.SenderName ?? "") + "  " + (brief.Title ?? "") + "  " + flags,
                        () => _ = mail.OpenAsync(id, default));
                }
            }

            for (var i = _detail.childCount - 1; i >= 0; i--)
                Destroy(_detail.GetChild(i).gameObject);
            var detail = page.Selected;
            if (detail?.Brief == null)
            {
                var empty = MakeLabel(_detail, "点一封邮件查看正文。", 16, TextAnchor.UpperLeft);
                Place(empty.rectTransform, 8, -8, 400, 28);
                return;
            }

            var when = MailClient.SentLabel(detail.Brief.SentAt);
            var expire = MailClient.ExpireLabel(detail.Brief.ExpireAt, mail.EstimatedServerUtc());
            var head = MakeLabel(_detail, (detail.Brief.SenderName ?? "") + "  " + (detail.Brief.Title ?? "") +
                                          (when.Length > 0 ? "  " + when : "") +
                                          (expire.Length > 0 ? "  " + expire : ""), 16, TextAnchor.UpperLeft);
            Place(head.rectTransform, 8, -6, 400, 24);
            var body = MakeLabel(_detail, MailClient.PlainText(detail.Body), 15, TextAnchor.UpperLeft);
            Place(body.rectTransform, 8, -32, 400, 52);
            body.horizontalOverflow = HorizontalWrapMode.Wrap;
            body.verticalOverflow = VerticalWrapMode.Truncate;
            var attach = "";
            for (var i = 0; i < detail.Attachments.Count; i++)
            {
                var line = MailClient.AttachmentLine(detail.Attachments[i]);
                if (line.Length == 0)
                    continue;
                if (attach.Length > 0)
                    attach += "\n";
                attach += line;
            }

            if (attach.Length > 0)
            {
                var items = MakeLabel(_detail, attach, 14, TextAnchor.UpperLeft);
                Place(items.rectTransform, 8, -86, 400, 36);
                items.horizontalOverflow = HorizontalWrapMode.Wrap;
            }

            var bar = Row(_detail, 8, -126, 400, 34);
            var mailId = detail.Brief.MailId;
            var reward = mail.RewardLabel(mailId);
            if (reward == MailClient.ConfirmedNotice)
            {
                Action(bar, "已到账", () => { });
            }
            else if (MailClient.Allows(detail, "claim") || reward == MailClient.UnknownNotice)
            {
                var busy = mail.IsBusy(mailId);
                var caption = busy ? "领取中" : reward == MailClient.UnknownNotice ? "领取结果未确认" : "领取";
                Action(bar, caption, () =>
                {
                    if (!mail.IsBusy(mailId))
                        _ = mail.ClaimAsync(mailId, default);
                });
            }

            if (reward == MailClient.ClaimedNotice || reward == MailClient.UnknownNotice || reward == MailClient.BagFullNotice)
            {
                Action(bar, "刷新背包", () =>
                {
                    if (!mail.IsBusy(mailId))
                        _ = mail.ReconcileBagAsync(default);
                });
            }
            var favoriteAction = MailClient.FavoriteAction(detail);
            if (favoriteAction != null)
            {
                var turnOn = favoriteAction == "FAVORITE";
                Action(bar, turnOn ? "收藏" : "取消收藏", () =>
                {
                    if (!mail.IsBusy(mailId))
                        _ = mail.FavoriteAsync(mailId, turnOn, default);
                });
            }

            if (MailClient.Allows(detail, "delete"))
            {
                Action(bar, _deleteId == mailId ? "确认删除" : "删除", () =>
                {
                    if (_deleteId != mailId)
                    {
                        _deleteId = mailId;
                        _signature = 0;
                        return;
                    }

                    _deleteId = 0;
                    _ = mail.BatchDeleteAsync(new[] { mailId }, default);
                });
            }
        }

        int Signature(MailClient mail)
        {
            var page = mail.Page;
            unchecked
            {
                var h = page.Mails.Count * 17 + page.UnreadTotal * 13 + page.Unclaimed + page.ClaimFailures.Count +
                        (mail.Loading ? 23 : 0) + mail.BusyCount;
                h = h * 31 + (int)page.MailboxVersion;
                h = h * 31 + (mail.Category ?? "").GetHashCode();
                h = h * 31 + (mail.FilterUnread ? 1 : 0) + (mail.FilterUnclaimed ? 2 : 0) +
                    (mail.FilterExpiring ? 4 : 0) + (mail.FilterFavorite ? 8 : 0);
                h = h * 31 + (page.LastError ?? "").GetHashCode();
                h = h * 31 + (page.LastNotice ?? "").GetHashCode();
                h = h * 31 + mail.Bag.AssetVersion.GetHashCode();
                h = h * 31 + (mail.RewardLabel(page.Selected?.Brief != null ? page.Selected.Brief.MailId : 0) ?? "").GetHashCode();
                h = h * 31 + (int)_deleteId;
                if (page.Selected?.Brief != null)
                    h = h * 31 + page.Selected.Brief.GetHashCode();
                var now = mail.EstimatedServerUtc();
                for (var i = 0; i < page.Mails.Count; i++)
                {
                    var brief = page.Mails[i];
                    if (brief == null)
                        continue;
                    h = h * 31 + brief.GetHashCode();
                    h = h * 31 + MailClient.ExpireLabel(brief.ExpireAt, now).GetHashCode();
                }

                return h;
            }
        }

        static string AttachmentFlag(MailBrief brief)
        {
            if (brief == null || !brief.HasAttachment)
                return "";
            if (string.Equals(brief.AttachmentState, "CLAIMED", StringComparison.OrdinalIgnoreCase))
                return "已领取 ";
            if (string.Equals(brief.AttachmentState, "CLAIMING", StringComparison.OrdinalIgnoreCase))
                return "同步中 ";
            return "附件 ";
        }

        void AddMailButton(string title, UnityEngine.Events.UnityAction click)
        {
            var button = MakeButton(_rows, title, new Vector2(0, 1), new Vector2(1, 1), Vector2.zero,
                new Vector2(0, 36), click);
            var element = button.gameObject.GetComponent<LayoutElement>();
            element.minHeight = 36;
            element.preferredHeight = 36;
            element.flexibleWidth = 1;
            var label = button.GetComponentInChildren<Text>();
            label.alignment = TextAnchor.MiddleLeft;
            label.rectTransform.offsetMin = new Vector2(8, 0);
            label.horizontalOverflow = HorizontalWrapMode.Wrap;
        }

        void AddLine(Transform parent, string text)
        {
            var label = MakeLabel(parent, text, 16, TextAnchor.MiddleLeft);
            var element = label.gameObject.AddComponent<LayoutElement>();
            element.minHeight = 28;
        }

        Image Tab(Transform parent, string title, UnityEngine.Events.UnityAction click)
        {
            var button = MakeButton(parent, title, click);
            button.GetComponent<Image>().color = Idle;
            var element = button.gameObject.GetComponent<LayoutElement>();
            element.minWidth = 88;
            element.preferredWidth = 96;
            return button.GetComponent<Image>();
        }

        void Action(Transform parent, string title, UnityEngine.Events.UnityAction click)
        {
            var button = MakeButton(parent, title, click);
            var element = button.gameObject.GetComponent<LayoutElement>();
            element.minWidth = 96;
            element.preferredWidth = title.Length > 3 ? 120 : 96;
        }

        Transform Row(Transform parent, float x, float y, float w, float h)
        {
            var row = new GameObject("Row", typeof(HorizontalLayoutGroup));
            row.transform.SetParent(parent, false);
            var rect = row.GetComponent<RectTransform>();
            Place(rect, x, y, w, h);
            var layout = row.GetComponent<HorizontalLayoutGroup>();
            layout.spacing = 8;
            layout.childControlWidth = false;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = false;
            layout.childAlignment = TextAnchor.MiddleLeft;
            return row.transform;
        }

        Button MakeButton(Transform parent, string title, UnityEngine.Events.UnityAction click)
        {
            return MakeButton(parent, title, new Vector2(0, 1), new Vector2(0, 1), Vector2.zero, new Vector2(96, 34),
                click);
        }

        Button MakeButton(Transform parent, string title, Vector2 anchorMin, Vector2 anchorMax, Vector2 pos,
            Vector2 size, UnityEngine.Events.UnityAction click)
        {
            var go = new GameObject(title, typeof(Image), typeof(Button), typeof(LayoutElement));
            go.transform.SetParent(parent, false);
            var rect = go.GetComponent<RectTransform>();
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.pivot = new Vector2(1, 1);
            rect.anchoredPosition = pos;
            rect.sizeDelta = size;
            go.GetComponent<Image>().color = On;
            var element = go.GetComponent<LayoutElement>();
            element.preferredWidth = size.x;
            element.preferredHeight = size.y;
            element.minHeight = size.y;
            var button = go.GetComponent<Button>();
            if (click != null)
                button.onClick.AddListener(click);
            var label = MakeLabel(go.transform, title, 16, TextAnchor.MiddleCenter);
            Stretch(label.rectTransform);
            return button;
        }

        Text MakeLabel(Transform parent, string value, int size, TextAnchor anchor)
        {
            var go = new GameObject("Label", typeof(Text));
            go.transform.SetParent(parent, false);
            var text = go.GetComponent<Text>();
            text.font = _font;
            text.fontSize = size;
            text.alignment = anchor;
            text.color = Color.white;
            text.text = value;
            text.raycastTarget = false;
            return text;
        }

        static void Place(RectTransform rect, float x, float y, float w, float h)
        {
            rect.anchorMin = new Vector2(0, 1);
            rect.anchorMax = new Vector2(0, 1);
            rect.pivot = new Vector2(0, 1);
            rect.anchoredPosition = new Vector2(x, y);
            rect.sizeDelta = new Vector2(w, h);
        }

        static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }
    }
}
