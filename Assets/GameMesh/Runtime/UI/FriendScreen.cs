using System;
using System.Collections.Generic;
using System.Globalization;
using GameMesh.Bootstrap;
using GameMesh.Friends;
using GameMesh.Protocol;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace GameMesh.UI
{
    /// <summary>
    /// Playable friend screen on the project's uGUI stack.
    /// The IMGUI friend panel stays available when GameMeshClientConfig.imguiFriendDebug is set.
    /// </summary>
    public sealed class FriendScreen : MonoBehaviour
    {
        public static bool OwnsPointer { get; private set; }

        static FriendScreen _instance;
        GameMeshClient _client;
        Font _font;
        GameObject _panel;
        Text _badge;
        Text _status;
        Text _count;
        InputField _query;
        RectTransform _rows;
        ScrollRect _scroll;
        int _signature;
        string _rowShape = "";
        ulong _confirmId;
        bool _confirmBlock;
        bool _open;
        Text _diag;
        bool _diagOpen;

        public static void Ensure(GameMeshClient client)
        {
            if (client == null)
                return;
            if (client.Config != null && client.Config.imguiFriendDebug)
            {
                OwnsPointer = false;
                if (_instance != null)
                    _instance.gameObject.SetActive(false);
                return;
            }

            if (_instance == null)
            {
                var go = new GameObject("FriendScreen");
                DontDestroyOnLoad(go);
                _instance = go.AddComponent<FriendScreen>();
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
            canvas.sortingOrder = 500;
            var scaler = canvasGo.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);

            var open = MakeButton(canvasGo.transform, "好友", new Vector2(1, 1), new Vector2(1, 1),
                new Vector2(-24, -24), new Vector2(180, 56), ToggleOpen);
            _badge = open.GetComponentInChildren<Text>();

            _panel = new GameObject("Panel", typeof(Image));
            _panel.transform.SetParent(canvasGo.transform, false);
            var panelRect = _panel.GetComponent<RectTransform>();
            panelRect.anchorMin = new Vector2(1, 1);
            panelRect.anchorMax = new Vector2(1, 1);
            panelRect.pivot = new Vector2(1, 1);
            panelRect.anchoredPosition = new Vector2(-24, -96);
            panelRect.sizeDelta = new Vector2(460, 720);
            _panel.GetComponent<Image>().color = new Color(0.08f, 0.09f, 0.12f, 0.94f);

            _count = MakeLabel(_panel.transform, "0/0", 18, TextAnchor.MiddleLeft);
            Place(_count.rectTransform, 16, -8, 200, 32);
            var tabs = new GameObject("Tabs", typeof(HorizontalLayoutGroup));
            tabs.transform.SetParent(_panel.transform, false);
            var tabsRect = tabs.GetComponent<RectTransform>();
            Place(tabsRect, 12, -44, 436, 40);
            tabs.GetComponent<HorizontalLayoutGroup>().spacing = 8;
            MakeButton(tabs.transform, "好友", TabFriends);
            MakeButton(tabs.transform, "申请", TabRequests);
            MakeButton(tabs.transform, "黑名单", TabBlocked);

            var searchRow = new GameObject("Search", typeof(HorizontalLayoutGroup));
            searchRow.transform.SetParent(_panel.transform, false);
            Place(searchRow.GetComponent<RectTransform>(), 12, -92, 436, 40);
            searchRow.GetComponent<HorizontalLayoutGroup>().spacing = 8;
            _query = MakeField(searchRow.transform, "角色名或 PlayerID");
            MakeButton(searchRow.transform, "搜索", DoSearch);

            _status = MakeLabel(_panel.transform, "", 16, TextAnchor.UpperLeft);
            Place(_status.rectTransform, 16, -140, 428, 48);
            _status.color = new Color(1f, 0.85f, 0.45f);

            var scrollGo = new GameObject("Scroll", typeof(Image), typeof(ScrollRect));
            scrollGo.transform.SetParent(_panel.transform, false);
            var scrollRect = scrollGo.GetComponent<RectTransform>();
            Place(scrollRect, 12, -196, 436, 460);
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
            _rows.anchoredPosition = Vector2.zero;
            _rows.sizeDelta = new Vector2(0, 0);
            var layout = content.GetComponent<VerticalLayoutGroup>();
            layout.childControlHeight = true;
            layout.childControlWidth = true;
            layout.childForceExpandHeight = false;
            layout.childForceExpandWidth = true;
            layout.spacing = 6;
            layout.padding = new RectOffset(8, 8, 8, 8);
            content.GetComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            _scroll = scrollGo.GetComponent<ScrollRect>();
            _scroll.viewport = viewRect;
            _scroll.content = _rows;
            _scroll.horizontal = false;
            if (_client != null && _client.Config != null && _client.Config.friendDiagnostics)
            {
                panelRect.sizeDelta = new Vector2(460, 820);
                MakeButton(_panel.transform, "诊断", new Vector2(1, 1), new Vector2(1, 1),
                    new Vector2(-16, -668), new Vector2(120, 36), ToggleDiag);
                _diag = MakeLabel(_panel.transform, "", 14, TextAnchor.UpperLeft);
                Place(_diag.rectTransform, 16, -708, 428, 96);
                _diag.horizontalOverflow = HorizontalWrapMode.Wrap;
                _diag.verticalOverflow = VerticalWrapMode.Overflow;
                _diag.gameObject.SetActive(false);
            }

            _panel.SetActive(false);
        }

        void ToggleDiag()
        {
            _diagOpen = !_diagOpen;
            if (_diag != null)
                _diag.gameObject.SetActive(_diagOpen);
        }

        void LateUpdate()
        {
            var friends = _client != null ? _client.Friends : null;
            if (friends == null || _panel == null)
            {
                OwnsPointer = false;
                return;
            }

            var badgeCount = friends.RequestBadge > 0
                ? "好友 (" + friends.RequestBadge + (friends.RequestHasMore ? "+)" : ")")
                : "好友";
            var badge = badgeCount;
            if (_badge != null && _badge.text != badge)
                _badge.text = badge;
            if (_diag != null && _diagOpen)
                _diag.text = friends.DiagnosticsText();
            OwnsPointer = _open;
            if (!_open)
                return;
            var sig = Signature(friends);
            if (sig == _signature)
                return;
            _signature = sig;
            Rebuild(friends);
        }

        void ToggleOpen()
        {
            _open = !_open;
            _panel.SetActive(_open);
            if (_client != null && _client.Friends != null)
                _client.Friends.PanelOpen = _open;
            _signature = 0;
        }

        void TabFriends()
        {
            SetTab(FriendPanelTab.Friends);
        }

        void TabRequests()
        {
            SetTab(FriendPanelTab.Requests);
        }

        void TabBlocked()
        {
            SetTab(FriendPanelTab.Blocked);
        }

        void SetTab(FriendPanelTab tab)
        {
            var friends = _client.Friends;
            friends.Tab = tab;
            _signature = 0;
            if (tab == FriendPanelTab.Requests)
                _ = friends.RefreshRequestsAsync(default);
            else if (tab == FriendPanelTab.Blocked)
                _ = friends.RefreshBlockedAsync(default);
            else
                _ = friends.RefreshFriendsAsync(default);
        }

        void DoSearch()
        {
            if (_client == null || !_client.Friends.CanRequest)
                return;
            _ = _client.Friends.SearchAsync(_query != null ? _query.text : "", default);
        }

        void Rebuild(FriendClient friends)
        {
            var shape = RowShape(friends);
            if (shape == _rowShape && _rows.childCount > 0 && TryRefreshLabels(friends))
            {
                ApplyStatus(friends);
                return;
            }

            _rowShape = shape;
            for (var i = _rows.childCount - 1; i >= 0; i--)
                Destroy(_rows.GetChild(i).gameObject);

            ApplyStatus(friends);

            if (friends.SearchHit != null)
                AddSearchRow(friends);

            if (_confirmId != 0)
                AddConfirmRow(friends);

            if (friends.Tab == FriendPanelTab.Requests)
                BuildRequests(friends);
            else if (friends.Tab == FriendPanelTab.Blocked)
                BuildBlocked(friends);
            else
                BuildFriends(friends);
        }

        void ApplyStatus(FriendClient friends)
        {
            var status = !string.IsNullOrEmpty(friends.LastError) ? friends.LastError : friends.LastNotice ?? "";
            if (friends.DataStale)
                status = string.IsNullOrEmpty(status) ? "在线状态可能不是最新" : status + "\n在线状态可能不是最新";
            _status.text = status;
            _count.text = friends.FriendCount + "/" +
                          (friends.FriendCap == 0 ? "—" : friends.FriendCap.ToString(CultureInfo.InvariantCulture));
        }

        public static string RowShape(FriendClient friends)
        {
            var text = ((int)friends.Tab).ToString(CultureInfo.InvariantCulture);
            if (friends.SearchHit != null)
                text += "S" + friends.SearchHit.PlayerId.ToString(CultureInfo.InvariantCulture);
            text += "C" + friends.Tab.ToString();
            if (friends.Tab == FriendPanelTab.Requests)
            {
                for (var i = 0; i < friends.Requests.Count; i++)
                    text += "R" + (friends.Requests[i] != null ? friends.Requests[i].RequestId.ToString(CultureInfo.InvariantCulture) : "0");
            }
            else if (friends.Tab == FriendPanelTab.Blocked)
            {
                for (var i = 0; i < friends.Blocked.Count; i++)
                    text += "B" + (friends.Blocked[i] != null ? friends.Blocked[i].PlayerId.ToString(CultureInfo.InvariantCulture) : "0");
            }
            else
            {
                for (var i = 0; i < friends.Friends.Count; i++)
                    text += "F" + (friends.Friends[i] != null ? friends.Friends[i].PlayerId.ToString(CultureInfo.InvariantCulture) : "0");
            }

            return text;
        }

        public static List<string> CollectLabels(FriendClient friends, ulong confirmId, bool confirmBlock)
        {
            var labels = new List<string>();
            if (friends == null)
                return labels;
            if (friends.SearchHit != null)
                labels.Add(SearchLine(friends.SearchHit, friends.SearchRelation));
            if (confirmId != 0)
                labels.Add(confirmBlock ? "确认拉黑 #" + confirmId + "？" : "确认删除好友 #" + confirmId + "？");
            if (friends.Tab == FriendPanelTab.Friends)
            {
                if (friends.Friends.Count == 0)
                    labels.Add(friends.FriendsLoading ? "加载中" : (!string.IsNullOrEmpty(friends.FriendListError) ? "加载失败，请重试" : "还没有好友"));
                else
                    for (var i = 0; i < friends.Friends.Count; i++)
                        if (friends.Friends[i] != null)
                            labels.Add(FriendClient.FormatFriendLine(friends.Friends[i]));
            }
            else if (friends.Tab == FriendPanelTab.Requests)
            {
                if (friends.Requests.Count == 0)
                    labels.Add(friends.RequestsLoading ? "加载中" : (!string.IsNullOrEmpty(friends.RequestListError) ? "加载失败，请重试" : "没有待处理的申请"));
                else
                    for (var i = 0; i < friends.Requests.Count; i++)
                    {
                        var row = friends.Requests[i];
                        if (row == null)
                            continue;
                        labels.Add(RequestLine(row));
                    }
            }
            else if (friends.Blocked.Count == 0)
            {
                labels.Add(friends.BlockedLoading ? "加载中" : (!string.IsNullOrEmpty(friends.BlockListError) ? "加载失败，请重试" : "黑名单是空的"));
            }
            else
            {
                for (var i = 0; i < friends.Blocked.Count; i++)
                {
                    if (friends.Blocked[i] == null)
                        continue;
                    labels.Add(FriendClient.DisplayName(friends.Blocked[i], true) + "  #" + friends.Blocked[i].PlayerId);
                }
            }

            return labels;
        }

        bool TryRefreshLabels(FriendClient friends)
        {
            var labels = CollectLabels(friends, _confirmId, _confirmBlock);

            var seen = 0;
            for (var i = 0; i < _rows.childCount; i++)
            {
                var label = _rows.GetChild(i).GetComponent<Text>();
                if (label == null)
                    continue;
                if (seen >= labels.Count)
                    return false;
                label.text = labels[seen++];
            }

            return seen == labels.Count;
        }

        void BuildFriends(FriendClient friends)
        {
            if (friends.Friends.Count == 0)
                AddLine(friends.FriendsLoading ? "加载中" : (!string.IsNullOrEmpty(friends.FriendListError) ? "加载失败，请重试" : "还没有好友"));
            else
            {
                for (var i = 0; i < friends.Friends.Count; i++)
                {
                    var row = friends.Friends[i];
                    if (row == null)
                        continue;
                    var id = row.PlayerId;
                    AddLine(FriendClient.FormatFriendLine(row));
                    var actions = AddRow();
                    AddSmall(actions, "删除", () => BeginConfirm(id, false));
                    AddSmall(actions, "拉黑", () => BeginConfirm(id, true));
                }
            }

            AddFooter(friends.FriendHasMore, () => _ = friends.RefreshFriendsAsync(default),
                () => _ = friends.LoadMoreFriendsAsync(default));
        }

        void BuildRequests(FriendClient friends)
        {
            if (friends.Requests.Count == 0)
                AddLine(friends.RequestsLoading ? "加载中" : (!string.IsNullOrEmpty(friends.RequestListError) ? "加载失败，请重试" : "没有待处理的申请"));
            else
            {
                for (var i = 0; i < friends.Requests.Count; i++)
                {
                    var row = friends.Requests[i];
                    if (row == null)
                        continue;
                    AddLine(RequestLine(row));
                    if (FriendClient.IsRequestExpired(row))
                        continue;
                    var requestId = row.RequestId;
                    var actions = AddRow();
                    AddSmall(actions, "同意", () =>
                    {
                        if (friends.CanRequest)
                            _ = friends.AcceptAsync(requestId, default);
                    });
                    AddSmall(actions, "拒绝", () =>
                    {
                        if (friends.CanRequest)
                            _ = friends.RejectAsync(requestId, default);
                    });
                }
            }

            AddFooter(friends.RequestHasMore, () => _ = friends.RefreshRequestsAsync(default),
                () => _ = friends.LoadMoreRequestsAsync(default));
        }

        void BuildBlocked(FriendClient friends)
        {
            if (friends.Blocked.Count == 0)
                AddLine(friends.BlockedLoading ? "加载中" : (!string.IsNullOrEmpty(friends.BlockListError) ? "加载失败，请重试" : "黑名单是空的"));
            else
            {
                for (var i = 0; i < friends.Blocked.Count; i++)
                {
                    var row = friends.Blocked[i];
                    if (row == null)
                        continue;
                    var name = FriendClient.DisplayName(row, true);
                    AddLine(name + "  #" + row.PlayerId);
                    var id = row.PlayerId;
                    var actions = AddRow();
                    AddSmall(actions, "解除拉黑", () =>
                    {
                        if (friends.CanRequest)
                            _ = friends.UnblockAsync(id, default);
                    });
                }
            }

            AddFooter(friends.BlockHasMore, () => _ = friends.RefreshBlockedAsync(default),
                () => _ = friends.LoadMoreBlockedAsync(default));
        }

        void AddFooter(bool hasMore, UnityEngine.Events.UnityAction refresh, UnityEngine.Events.UnityAction more)
        {
            var actions = AddRow();
            AddSmall(actions, "刷新", refresh);
            if (hasMore)
                AddSmall(actions, "加载更多", more);
        }

        void AddSearchRow(FriendClient friends)
        {
            var hit = friends.SearchHit;
            AddLine(SearchLine(hit, friends.SearchRelation));
            var actions = AddRow();
            var relation = friends.SearchRelation;
            var id = hit.PlayerId;
            var name = hit.Name;
            if (relation == FriendRelationState.FriendRelationNone)
            {
                AddSmall(actions, "申请", () =>
                {
                    if (friends.CanRequest)
                        _ = friends.ApplyAsync(id, name, default);
                });
            }
            else if (relation == FriendRelationState.FriendRelationReceivedPending)
            {
                AddSmall(actions, "去申请列表", () => SetTab(FriendPanelTab.Requests));
            }
            else if (relation == FriendRelationState.FriendRelationFriend)
            {
                AddSmall(actions, "已是好友", null);
            }
            else if (relation == FriendRelationState.FriendRelationBlockedBySelf)
            {
                AddSmall(actions, "已拉黑", null);
            }
            else
            {
                AddSmall(actions, "已发出申请", null);
            }
        }

        void BeginConfirm(ulong playerId, bool block)
        {
            _confirmId = playerId;
            _confirmBlock = block;
            _signature = 0;
        }

        void AddConfirmRow(FriendClient friends)
        {
            AddLine(_confirmBlock ? "确认拉黑 #" + _confirmId + "？" : "确认删除好友 #" + _confirmId + "？");
            var id = _confirmId;
            var block = _confirmBlock;
            var actions = AddRow();
            AddSmall(actions, "确认", () =>
            {
                _confirmId = 0;
                if (!friends.CanRequest)
                    return;
                if (block)
                    _ = friends.BlockAsync(id, default);
                else
                    _ = friends.DeleteAsync(id, default);
            });
            AddSmall(actions, "取消", () =>
            {
                _confirmId = 0;
                _signature = 0;
            });
        }

        static string SearchLine(FriendBrief hit, FriendRelationState relation)
        {
            return (hit.Online ? "在线  " : "离线  ") + FriendClient.DisplayName(hit, false) + "  #" +
                   hit.PlayerId.ToString(CultureInfo.InvariantCulture) + "  " +
                   (hit.Level == 0 ? "等级未知" : "Lv." + hit.Level.ToString(CultureInfo.InvariantCulture)) + "  " +
                   FriendClient.SearchRelationLabel(relation);
        }

        static string RequestLine(FriendRequestInfo row)
        {
            var who = row != null ? row.Applicant : null;
            var remain = row != null ? Remain(row.ExpireAt) : "";
            return FriendClient.DisplayName(who, false) + "  #" +
                   (who != null ? who.PlayerId : 0UL).ToString(CultureInfo.InvariantCulture) +
                   (string.IsNullOrEmpty(remain) ? "" : "  " + remain);
        }

        static string Remain(ulong expireAt)
        {
            if (expireAt == 0)
                return "";
            TimeSpan left;
            try
            {
                left = DateTimeOffset.FromUnixTimeSeconds((long)expireAt) - DateTimeOffset.UtcNow;
            }
            catch
            {
                return "";
            }

            if (left.TotalSeconds <= 0)
                return "已过期";
            if (left.TotalDays >= 1)
                return "剩余 " + (int)left.TotalDays + " 天";
            if (left.TotalHours >= 1)
                return "剩余 " + (int)left.TotalHours + " 小时";
            return "剩余 " + Math.Max(1, (int)left.TotalMinutes) + " 分钟";
        }

        int Signature(FriendClient friends)
        {
            unchecked
            {
                var h = (int)friends.Tab * 17 + friends.Friends.Count * 31 + friends.Requests.Count * 13 +
                        friends.Blocked.Count + friends.RequestBadge + (friends.Busy ? 7 : 0) +
                        (friends.FriendHasMore ? 11 : 0) + (friends.RequestHasMore ? 13 : 0) +
                        (friends.BlockHasMore ? 17 : 0) + (friends.DataStale ? 19 : 0) +
                        (int)_confirmId + (_confirmBlock ? 3 : 0);
                h = h * 31 + (friends.FriendListError ?? "").GetHashCode();
                h = h * 31 + (friends.RequestListError ?? "").GetHashCode();
                h = h * 31 + (friends.BlockListError ?? "").GetHashCode();
                h = h * 31 + (friends.LastError ?? "").GetHashCode();
                h = h * 31 + (friends.LastNotice ?? "").GetHashCode();
                h = h * 31 + (int)friends.SearchRelation;
                if (friends.SearchHit != null)
                    h = h * 31 + friends.SearchHit.GetHashCode();
                for (var i = 0; i < friends.Friends.Count; i++)
                {
                    if (friends.Friends[i] != null)
                        h = h * 31 + friends.Friends[i].GetHashCode();
                }

                for (var i = 0; i < friends.Requests.Count; i++)
                {
                    if (friends.Requests[i] != null)
                        h = h * 31 + friends.Requests[i].GetHashCode();
                }

                return h;
            }
        }

        GameObject AddRow()
        {
            var row = new GameObject("Row", typeof(HorizontalLayoutGroup));
            row.transform.SetParent(_rows, false);
            var layout = row.GetComponent<HorizontalLayoutGroup>();
            layout.spacing = 8;
            layout.childControlWidth = false;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = false;
            var element = row.AddComponent<LayoutElement>();
            element.minHeight = 36;
            return row;
        }

        void AddLine(string text)
        {
            var label = MakeLabel(_rows, text, 16, TextAnchor.MiddleLeft);
            var element = label.gameObject.AddComponent<LayoutElement>();
            element.minHeight = 28;
            label.horizontalOverflow = HorizontalWrapMode.Wrap;
            label.verticalOverflow = VerticalWrapMode.Overflow;
        }

        void AddSmall(GameObject parent, string title, UnityEngine.Events.UnityAction click)
        {
            var button = MakeButton(parent.transform, title, click);
            var element = button.gameObject.AddComponent<LayoutElement>();
            element.minWidth = 96;
            element.minHeight = 32;
            button.interactable = click != null &&
                                   (_client == null || _client.Friends.CanRequest);
        }

        Button MakeButton(Transform parent, string title, UnityEngine.Events.UnityAction click)
        {
            return MakeButton(parent, title, new Vector2(0, 1), new Vector2(0, 1), Vector2.zero, new Vector2(120, 36),
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
            go.GetComponent<Image>().color = new Color(0.18f, 0.45f, 0.62f, 1f);
            var element = go.GetComponent<LayoutElement>();
            element.preferredWidth = size.x;
            element.preferredHeight = size.y;
            element.minHeight = size.y;
            var button = go.GetComponent<Button>();
            if (click != null)
                button.onClick.AddListener(click);
            var label = MakeLabel(go.transform, title, 18, TextAnchor.MiddleCenter);
            Stretch(label.rectTransform);
            return button;
        }

        InputField MakeField(Transform parent, string placeholder)
        {
            var go = new GameObject("Query", typeof(Image), typeof(InputField), typeof(LayoutElement));
            go.transform.SetParent(parent, false);
            go.GetComponent<LayoutElement>().minWidth = 280;
            go.GetComponent<LayoutElement>().minHeight = 36;
            go.GetComponent<Image>().color = new Color(1, 1, 1, 0.92f);
            var text = MakeLabel(go.transform, "", 16, TextAnchor.MiddleLeft);
            text.color = Color.black;
            Stretch(text.rectTransform);
            text.rectTransform.offsetMin = new Vector2(8, 0);
            var hint = MakeLabel(go.transform, placeholder, 16, TextAnchor.MiddleLeft);
            hint.color = new Color(0, 0, 0, 0.35f);
            hint.fontStyle = FontStyle.Italic;
            Stretch(hint.rectTransform);
            hint.rectTransform.offsetMin = new Vector2(8, 0);
            var field = go.GetComponent<InputField>();
            field.characterLimit = FriendClient.MaxSearchLength;
            field.textComponent = text;
            field.placeholder = hint;
            return field;
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
