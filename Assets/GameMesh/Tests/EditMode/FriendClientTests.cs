using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GameMesh.Auth;
using GameMesh.Friends;
using GameMesh.Network;
using GameMesh.Protocol;
using GameMesh.UI;
using NUnit.Framework;

namespace GameMesh.Tests.EditMode
{
    public sealed class FriendClientTests
    {
        [Test]
        public void Apply_SubmittedNotice_IncomingSwitchesTab_CooldownDoesNotResend()
        {
            var sends = 0;
            var friends = Client((req, ct) =>
            {
                sends++;
                if (sends == 1)
                {
                    return Task.FromResult(new GameResponse
                    {
                        Ok = true,
                        FriendApply = new FriendApplyRsp { Ok = true, RequestId = 0 }
                    });
                }

                if (req.FriendApply != null)
                {
                    return Task.FromResult(new GameResponse
                    {
                        Ok = false,
                        FriendApply = new FriendApplyRsp
                        {
                            Ok = false,
                            ErrorCode = sends == 2 ? "ERR_INCOMING_REQUEST_EXISTS" : "ERR_OPERATION_TOO_FREQUENT"
                        }
                    });
                }

                return Task.FromResult(new GameResponse
                {
                    Ok = true,
                    FriendRequestList = new FriendRequestListRsp { Ok = true }
                });
            });

            friends.ApplyAsync(42, "Peer", CancellationToken.None).GetAwaiter().GetResult();
            Assert.AreEqual("已提交", friends.LastNotice);
            Assert.AreEqual("", friends.LastError);

            friends.ApplyAsync(42, "Peer", CancellationToken.None).GetAwaiter().GetResult();
            Assert.AreEqual(FriendPanelTab.Requests, friends.Tab);
            StringAssert.Contains("对方已向你申请", friends.LastError);
            var afterIncoming = sends;

            friends.ApplyAsync(42, "Peer", CancellationToken.None).GetAwaiter().GetResult();
            Assert.Greater(sends, afterIncoming);
            var duringCooldown = sends;
            friends.ApplyAsync(42, "Peer", CancellationToken.None).GetAwaiter().GetResult();
            Assert.AreEqual(duringCooldown, sends);
            StringAssert.Contains("操作过于频繁", friends.LastError);
        }

        [Test]
        public void Apply_RetriesReuseOperationId_UntilSuccess()
        {
            var ops = new List<string>();
            var n = 0;
            var friends = Client((req, ct) =>
            {
                n++;
                ops.Add(req.FriendApply.OperationId);
                if (n == 1)
                {
                    return Task.FromResult(new GameResponse
                    {
                        Ok = false,
                        FriendApply = new FriendApplyRsp { Ok = false, ErrorCode = "ERR_PLAYER_NOT_FOUND" }
                    });
                }

                return Task.FromResult(new GameResponse
                {
                    Ok = true,
                    FriendApply = new FriendApplyRsp { Ok = true, RequestId = n == 2 ? 0UL : 9UL }
                });
            });

            friends.ApplyAsync(7, "A", CancellationToken.None).GetAwaiter().GetResult();
            friends.ApplyAsync(7, "A", CancellationToken.None).GetAwaiter().GetResult();
            Assert.AreEqual("已提交", friends.LastNotice);
            friends.ApplyAsync(7, "A", CancellationToken.None).GetAwaiter().GetResult();
            Assert.AreEqual(3, ops.Count);
            Assert.AreEqual(ops[0], ops[1]);
            Assert.AreNotEqual(ops[1], ops[2]);
        }

        [Test]
        public void Accept_InsertsPeerAndClearsRequest()
        {
            var friends = Client((req, ct) => Task.FromResult(new GameResponse
            {
                Ok = true,
                FriendAccept = new FriendAcceptRsp
                {
                    Ok = true,
                    Peer = new FriendBrief { PlayerId = 42, Name = "Peer", Online = true }
                }
            }));
            friends.ApplyPush(new GameResponse
            {
                FriendRequestPush = new FriendRequestPush
                {
                    RequestId = 7,
                    Applicant = new FriendBrief { PlayerId = 42, Name = "Peer" }
                }
            });
            Assert.AreEqual(1, friends.RequestBadge);
            friends.AcceptAsync(7, CancellationToken.None).GetAwaiter().GetResult();
            Assert.AreEqual(0, friends.Requests.Count);
            Assert.AreEqual(0, friends.RequestBadge);
            Assert.AreEqual(1, friends.Friends.Count);
            Assert.AreEqual("Peer", friends.Friends[0].Name);
        }

        [Test]
        public void Pushes_RemoveAddAndDedupeRequests_PresenceDoesNotChangeMembership()
        {
            var friends = Client((req, ct) => Task.FromResult(new GameResponse { Ok = true }));
            friends.ApplyPush(new GameResponse
            {
                FriendAddedPush = new FriendAddedPush
                {
                    Peer = new FriendBrief { PlayerId = 9, Name = "peer" }
                }
            });
            friends.ApplyPush(new GameResponse
            {
                FriendRequestPush = new FriendRequestPush
                {
                    RequestId = 3,
                    Applicant = new FriendBrief { PlayerId = 9, Name = "peer" }
                }
            });
            friends.ApplyPush(new GameResponse
            {
                FriendRequestPush = new FriendRequestPush
                {
                    RequestId = 3,
                    Applicant = new FriendBrief { PlayerId = 9, Name = "peer" }
                }
            });
            Assert.AreEqual(1, friends.Requests.Count);
            friends.ApplyPush(new GameResponse
            {
                FriendAddedPush = new FriendAddedPush
                {
                    Peer = new FriendBrief { PlayerId = 9, Name = "peer", Online = true }
                }
            });
            Assert.AreEqual(0, friends.Requests.Count);
            Assert.AreEqual(1, friends.Friends.Count);

            friends.ApplyPush(new GameResponse
            {
                FriendRemovedPush = new FriendRemovedPush { FriendPlayerId = 9 }
            });
            Assert.AreEqual(0, friends.Friends.Count);

            friends.ApplyPush(new GameResponse
            {
                FriendPresencePush = new FriendPresencePush { FriendPlayerId = 11, Online = true }
            });
            Assert.AreEqual(0, friends.Friends.Count);
            friends.ApplyPush(new GameResponse
            {
                FriendAddedPush = new FriendAddedPush
                {
                    Peer = new FriendBrief { PlayerId = 11, Name = "p", Online = false }
                }
            });
            friends.ApplyPush(new GameResponse
            {
                FriendPresencePush = new FriendPresencePush
                {
                    FriendPlayerId = 11,
                    Online = true,
                    LastOnlineTime = 5
                }
            });
            Assert.AreEqual(1, friends.Friends.Count);
            Assert.IsTrue(friends.Friends[0].Online);
        }

        [Test]
        public void Apply_IgnoresSuccessAfterClear()
        {
            FriendClient friends = null;
            friends = Client((req, ct) =>
            {
                friends.Clear();
                return Task.FromResult(new GameResponse
                {
                    Ok = true,
                    FriendApply = new FriendApplyRsp { Ok = true, RequestId = 0 }
                });
            });
            friends.ApplyAsync(42, "Peer", CancellationToken.None).GetAwaiter().GetResult();
            Assert.AreEqual("", friends.LastNotice);
            Assert.AreEqual(FriendRelationState.FriendRelationNone, friends.SearchRelation);
        }

        [Test]
        public void Clear_DropsLateResponseFromPreviousGeneration()
        {
            FriendClient friends = null;
            friends = Client((req, ct) =>
            {
                friends.Clear();
                var body = new FriendListRsp { Ok = true };
                body.Friends.Add(new FriendBrief { PlayerId = 9, Name = "late" });
                return Task.FromResult(new GameResponse { Ok = true, FriendList = body });
            });
            friends.RefreshFriendsAsync(CancellationToken.None).GetAwaiter().GetResult();
            Assert.AreEqual(0, friends.Friends.Count);
            Assert.AreEqual(0, friends.RequestBadge);
        }

        [Test]
        public void Block_RemovesFriendAndRequests_UnblockReversesSearchRelation()
        {
            var friends = Client((req, ct) =>
            {
                if (req.FriendBlock != null)
                {
                    return Task.FromResult(new GameResponse
                    {
                        Ok = true,
                        FriendBlock = new FriendBlockRsp { Ok = true }
                    });
                }

                return Task.FromResult(new GameResponse
                {
                    Ok = true,
                    FriendUnblock = new FriendUnblockRsp { Ok = true }
                });
            });
            friends.SearchHit = new FriendBrief { PlayerId = 42, Name = "Peer" };
            friends.SearchRelation = FriendRelationState.FriendRelationFriend;
            friends.Friends.Add(new FriendBrief { PlayerId = 42, Name = "Peer" });
            friends.Requests.Add(new FriendRequestInfo
            {
                RequestId = 1,
                Applicant = new FriendBrief { PlayerId = 42, Name = "Peer" }
            });
            friends.BlockAsync(42, CancellationToken.None).GetAwaiter().GetResult();
            Assert.AreEqual(0, friends.Friends.Count);
            Assert.AreEqual(0, friends.Requests.Count);
            Assert.AreEqual(1, friends.Blocked.Count);
            Assert.AreEqual(FriendRelationState.FriendRelationBlockedBySelf, friends.SearchRelation);

            friends.UnblockAsync(42, CancellationToken.None).GetAwaiter().GetResult();
            Assert.AreEqual(0, friends.Blocked.Count);
            Assert.AreEqual(FriendRelationState.FriendRelationNone, friends.SearchRelation);
        }

        [Test]
        public void ExpiredRequest_RefreshesRequestListAndKeepsCopy()
        {
            var lists = 0;
            var friends = Client((req, ct) =>
            {
                if (req.FriendAccept != null)
                {
                    return Task.FromResult(new GameResponse
                    {
                        Ok = false,
                        FriendAccept = new FriendAcceptRsp { Ok = false, ErrorCode = "ERR_REQUEST_EXPIRED" }
                    });
                }

                lists++;
                return Task.FromResult(new GameResponse
                {
                    Ok = true,
                    FriendRequestList = new FriendRequestListRsp { Ok = true }
                });
            });
            friends.AcceptAsync(4, CancellationToken.None).GetAwaiter().GetResult();
            Assert.GreaterOrEqual(lists, 1);
            StringAssert.Contains("申请已过期", friends.LastError);
        }

        [Test]
        public void ClosedPanel_PollsRequestsSlowly_AndStopsWithoutIdentity()
        {
            var session = new GameSession();
            session.ApplyLogin(8, "s", "t", 1, "me");
            var friends = new FriendClient(session, (req, ct) => Task.FromResult(new GameResponse { Ok = true }));
            Assert.IsFalse(friends.ShouldPoll(10f, 0f, false));
            Assert.IsTrue(friends.ShouldPoll(60f, 0f, false));
            Assert.IsFalse(friends.ShouldPoll(10f, 0f, true));
            Assert.IsTrue(friends.ShouldPoll(15f, 0f, true));
            session.ClearSensitive();
            Assert.IsFalse(friends.ShouldPoll(120f, 0f, true));
        }

        [Test]
        public void Apply_SurvivesFriendListRefreshWhileInFlight()
        {
            FriendClient friends = null;
            friends = Client((req, ct) =>
            {
                if (req.FriendApply != null)
                {
                    friends.RefreshFriendsAsync(CancellationToken.None).GetAwaiter().GetResult();
                    return Task.FromResult(new GameResponse
                    {
                        Ok = true,
                        FriendApply = new FriendApplyRsp { Ok = true, RequestId = 0 }
                    });
                }

                var body = new FriendListRsp { Ok = true };
                body.Friends.Add(new FriendBrief { PlayerId = 3, Name = "listed" });
                return Task.FromResult(new GameResponse { Ok = true, FriendList = body });
            });
            friends.ApplyAsync(42, "Peer", CancellationToken.None).GetAwaiter().GetResult();
            Assert.AreEqual("已提交", friends.LastNotice);
            Assert.AreEqual(1, friends.Friends.Count);
            Assert.AreEqual("listed", friends.Friends[0].Name);
        }

        [Test]
        public void RefreshRequests_KeepsPreviousListWhenAPageFails()
        {
            var friends = Client((req, ct) =>
            {
                var body = new FriendRequestListRsp { Ok = true, NextCursor = "next" };
                body.Requests.Add(new FriendRequestInfo
                {
                    RequestId = 9,
                    Applicant = new FriendBrief { PlayerId = 2, Name = "new" }
                });
                if (req.FriendRequestList != null && req.FriendRequestList.Cursor == "next")
                {
                    return Task.FromResult(new GameResponse
                    {
                        Ok = false,
                        FriendRequestList = new FriendRequestListRsp { Ok = false, ErrorCode = "ERR_DEPENDENCY_UNAVAILABLE" }
                    });
                }

                return Task.FromResult(new GameResponse { Ok = true, FriendRequestList = body });
            });
            friends.Requests.Add(new FriendRequestInfo
            {
                RequestId = 4,
                Applicant = new FriendBrief { PlayerId = 1, Name = "old" }
            });
            friends.RequestBadge = 1;
            friends.RefreshRequestsAsync(CancellationToken.None).GetAwaiter().GetResult();
            Assert.AreEqual(1, friends.Requests.Count);
            Assert.AreEqual(4UL, friends.Requests[0].RequestId);
            Assert.AreEqual(1, friends.RequestBadge);
        }

        [Test]
        public void AlreadyFriendAndNotFriend_RefreshFriendList()
        {
            var lists = 0;
            var friends = Client((req, ct) =>
            {
                if (req.FriendList != null)
                {
                    lists++;
                    return Task.FromResult(new GameResponse
                    {
                        Ok = true,
                        FriendList = new FriendListRsp { Ok = true }
                    });
                }

                var code = req.FriendApply != null ? "ERR_ALREADY_FRIEND" : "ERR_NOT_FRIEND";
                if (req.FriendApply != null)
                {
                    return Task.FromResult(new GameResponse
                    {
                        Ok = false,
                        FriendApply = new FriendApplyRsp { Ok = false, ErrorCode = code }
                    });
                }

                return Task.FromResult(new GameResponse
                {
                    Ok = false,
                    FriendDelete = new FriendDeleteRsp { Ok = false, ErrorCode = code }
                });
            });
            friends.ApplyAsync(5, "A", CancellationToken.None).GetAwaiter().GetResult();
            Assert.GreaterOrEqual(lists, 1);
            StringAssert.Contains("已经是好友", friends.LastError);
            var afterApply = lists;
            friends.DeleteAsync(5, CancellationToken.None).GetAwaiter().GetResult();
            Assert.Greater(lists, afterApply);
            StringAssert.Contains("还不是好友", friends.LastError);
        }

        [Test]
        public void ClosedSession_DoesNotSendOrPoll()
        {
            var session = new GameSession();
            session.ApplyLogin(8, "s", "t", 1, "me");
            var sends = 0;
            var friends = new FriendClient(session, (req, ct) =>
            {
                sends++;
                return Task.FromResult(new GameResponse { Ok = true });
            });
            session.SessionReplaced = true;
            friends.ApplyAsync(1, "A", CancellationToken.None).GetAwaiter().GetResult();
            Assert.AreEqual(0, sends);
            Assert.IsFalse(friends.ShouldPoll(120f, 0f, true));

            session.SessionReplaced = false;
            friends.SetRequestsOpen(true);
            Assert.IsTrue(friends.CanRequest);
            friends.SetRequestsOpen(false);
            friends.ApplyAsync(1, "A", CancellationToken.None).GetAwaiter().GetResult();
            Assert.AreEqual(0, sends);
            Assert.IsFalse(friends.ShouldPoll(120f, 0f, true));

            friends.Clear();
            session.ApplyLogin(8, "s", "t", 1, "me");
            friends.RefreshAfterLoginAsync(CancellationToken.None).GetAwaiter().GetResult();
            Assert.IsTrue(friends.CanRequest);
            Assert.Greater(sends, 0);
        }

        [Test]
        public void Push_UsesProfileFields_AndRefreshesWhenNameMissing()
        {
            var requestLists = 0;
            var friendLists = 0;
            var friends = Client((req, ct) =>
            {
                if (req.FriendRequestList != null)
                    requestLists++;
                if (req.FriendList != null)
                    friendLists++;
                if (req.FriendRequestList != null)
                {
                    var requests = new FriendRequestListRsp { Ok = true };
                    requests.Requests.Add(new FriendRequestInfo
                    {
                        RequestId = 7,
                        Applicant = new FriendBrief { PlayerId = 10, Level = 2 }
                    });
                    return Task.FromResult(new GameResponse { Ok = true, FriendRequestList = requests });
                }

                return Task.FromResult(new GameResponse
                {
                    Ok = true,
                    FriendList = new FriendListRsp { Ok = true }
                });
            });
            Assert.IsTrue(friends.ApplyPush(new GameResponse
            {
                FriendRequestPush = new FriendRequestPush
                {
                    RequestId = 6,
                    ExpireAt = 80,
                    Applicant = new FriendBrief { PlayerId = 9, Name = "Ann", Level = 8 }
                }
            }));
            Assert.AreEqual("Ann", friends.Requests[0].Applicant.Name);
            Assert.AreEqual(8u, friends.Requests[0].Applicant.Level);
            Assert.AreEqual(80UL, friends.Requests[0].ExpireAt);
            Assert.AreEqual(0, requestLists);

            Assert.IsTrue(friends.ApplyPush(new GameResponse
            {
                FriendRequestPush = new FriendRequestPush
                {
                    RequestId = 7,
                    Applicant = new FriendBrief { PlayerId = 10, Level = 2 }
                }
            }));
            Assert.AreEqual("#10", FriendClient.DisplayName(friends.Requests[0].Applicant, false));
            Assert.GreaterOrEqual(requestLists, 1);

            Assert.IsTrue(friends.ApplyPush(new GameResponse
            {
                FriendAddedPush = new FriendAddedPush
                {
                    Peer = new FriendBrief { PlayerId = 11, Name = "Bo", Level = 4, Online = false, LastOnlineTime = 3 }
                }
            }));
            Assert.AreEqual(0, friendLists);
            Assert.IsTrue(friends.ApplyPush(new GameResponse
            {
                FriendPresencePush = new FriendPresencePush
                {
                    FriendPlayerId = 11,
                    Online = true,
                    LastOnlineTime = 19
                }
            }));
            Assert.IsTrue(friends.Friends[0].Online);
            Assert.AreEqual(19UL, friends.Friends[0].LastOnlineTime);

            Assert.IsTrue(friends.ApplyPush(new GameResponse
            {
                FriendAddedPush = new FriendAddedPush
                {
                    Peer = new FriendBrief { PlayerId = 12, Level = 1 }
                }
            }));
            Assert.GreaterOrEqual(friendLists, 1);
            Assert.AreEqual("加载中", FriendClient.DisplayName(new FriendBrief { PlayerId = 12 }, true));
        }

        [Test]
        public void FormatLastOnline_RelativeBuckets_DoNotThrow()
        {
            var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
            Assert.AreEqual("未知", FriendClient.FormatLastOnline(0, now));
            Assert.AreEqual("刚刚", FriendClient.FormatLastOnline(1_700_000_000 - 10, now));
            Assert.AreEqual("5 分钟前", FriendClient.FormatLastOnline(1_700_000_000 - 5 * 60, now));
            Assert.AreEqual("3 小时前", FriendClient.FormatLastOnline(1_700_000_000 - 3 * 3600, now));
            Assert.AreEqual("2 天前", FriendClient.FormatLastOnline(1_700_000_000 - 2 * 86400, now));
            Assert.AreEqual("刚刚", FriendClient.FormatLastOnline(1_700_000_000 - 59, now));
            Assert.AreEqual("1 分钟前", FriendClient.FormatLastOnline(1_700_000_000 - 60, now));
            Assert.AreEqual("1 小时前", FriendClient.FormatLastOnline(1_700_000_000 - 3600, now));
            Assert.AreEqual("1 天前", FriendClient.FormatLastOnline(1_700_000_000 - 86400, now));
            Assert.AreEqual("364 天前", FriendClient.FormatLastOnline(1_700_000_000 - 364L * 86400, now));
            Assert.AreEqual("未知", FriendClient.FormatLastOnline(1_700_000_000 - 365L * 86400, now));
            Assert.AreEqual("刚刚", FriendClient.FormatLastOnline(1_700_000_000 + 30, now));
            Assert.AreEqual("未知", FriendClient.FormatLastOnline(1_700_000_000 + 3600, now));
            Assert.AreEqual("未知", FriendClient.FormatLastOnline(ulong.MaxValue, now));
        }

        [Test]
        public void RetryableFriendError_ShowsLaterHint_AndDoesNotAutoResend()
        {
            var sends = 0;
            var friends = Client((req, ct) =>
            {
                sends++;
                return Task.FromResult(new GameResponse
                {
                    Ok = false,
                    Retryable = true,
                    FriendApply = new FriendApplyRsp { Ok = false, ErrorCode = "ERR_OPERATION_TOO_FREQUENT" }
                });
            });
            friends.ApplyAsync(3, "A", CancellationToken.None).GetAwaiter().GetResult();
            Assert.AreEqual(1, sends);
            Assert.IsTrue(friends.ShowRetryHint);
            StringAssert.Contains("稍后重试", friends.LastError);
            friends.ApplyAsync(3, "A", CancellationToken.None).GetAwaiter().GetResult();
            Assert.AreEqual(1, sends);

            var terminal = Client((req, ct) => Task.FromResult(new GameResponse
            {
                Ok = false,
                Retryable = false,
                FriendApply = new FriendApplyRsp { Ok = false, ErrorCode = "ERR_ALREADY_FRIEND" }
            }));
            terminal.ApplyAsync(3, "A", CancellationToken.None).GetAwaiter().GetResult();
            Assert.IsFalse(terminal.ShowRetryHint);
            StringAssert.DoesNotContain("稍后重试", terminal.LastError);
            AssertNoRetryHint("ERR_FRIEND_LIMIT", friends =>
                friends.ApplyAsync(3, "A", CancellationToken.None).GetAwaiter().GetResult());
            AssertNoRetryHint("ERR_NOT_FRIEND", friends =>
                friends.DeleteAsync(3, CancellationToken.None).GetAwaiter().GetResult());
            AssertNoRetryHint("ERR_REQUEST_EXPIRED", friends =>
                friends.AcceptAsync(3, CancellationToken.None).GetAwaiter().GetResult());
        }

        [Test]
        public void TransportTimeout_RetriesOnceWithSameOperationId()
        {
            var ops = new List<string>();
            var n = 0;
            var friends = Client((req, ct) =>
            {
                n++;
                ops.Add(req.FriendApply.OperationId);
                if (n == 1)
                    throw new GameMeshException(GameMeshErrorCode.ClientTimeout, "timeout");
                return Task.FromResult(new GameResponse
                {
                    Ok = true,
                    FriendApply = new FriendApplyRsp { Ok = true, RequestId = 0 }
                });
            });
            friends.ApplyAsync(9, "A", CancellationToken.None).GetAwaiter().GetResult();
            Assert.AreEqual(2, n);
            Assert.AreEqual(ops[0], ops[1]);
            Assert.AreEqual("已提交", friends.LastNotice);
        }

        [Test]
        public void PresenceHold_KeepsLastStateUntilFriendListArrives()
        {
            var friends = Client((req, ct) =>
            {
                var body = new FriendListRsp { Ok = true };
                body.Friends.Add(new FriendBrief { PlayerId = 4, Name = "Ann", Online = false, LastOnlineTime = 10 });
                return Task.FromResult(new GameResponse { Ok = true, FriendList = body });
            });
            friends.Friends.Add(new FriendBrief { PlayerId = 4, Name = "Ann", Online = true, LastOnlineTime = 1 });
            friends.BeginPresenceHold();
            friends.ApplyPush(new GameResponse
            {
                FriendPresencePush = new FriendPresencePush { FriendPlayerId = 4, Online = false, LastOnlineTime = 9 }
            });
            Assert.IsTrue(friends.Friends[0].Online);
            friends.RefreshFriendsAsync(CancellationToken.None).GetAwaiter().GetResult();
            Assert.IsFalse(friends.PresenceHold);
            Assert.IsFalse(friends.Friends[0].Online);
        }

        [Test]
        public void Search_DebouncesAndAcceptsOddCharacters()
        {
            var sends = 0;
            var friends = Client((req, ct) =>
            {
                sends++;
                return Task.FromResult(new GameResponse
                {
                    Ok = true,
                    FriendSearch = new FriendSearchRsp
                    {
                        Ok = true,
                        Player = new FriendBrief { PlayerId = 6, Name = "n" },
                        Relation = FriendRelationState.FriendRelationNone
                    }
                });
            });
            friends.SearchAsync("*\n\t名字", CancellationToken.None).GetAwaiter().GetResult();
            friends.SearchAsync("另一个", CancellationToken.None).GetAwaiter().GetResult();
            Assert.AreEqual(1, sends);
            friends.SearchAsync("   ", CancellationToken.None).GetAwaiter().GetResult();
            Assert.AreEqual(1, sends);
            StringAssert.Contains("请输入", friends.LastError);
        }

        [Test]
        public void ErrorCatalog_WrongRouteHasCopy()
        {
            var info = GameErrorCatalog.Resolve("ERR_WRONG_ROUTE");
            Assert.AreEqual("服务暂不可用，请稍后重试", info.Chinese);
            Assert.IsTrue(info.Retryable);
            StringAssert.Contains("重新登录", info.Detail);
            StringAssert.DoesNotContain("错误的服务", info.Detail);
        }

        [Test]
        public void ErrorCatalog_FriendCodesHaveCopy_UnknownCodeFallsBack()
        {
            string[] codes =
            {
                "ERR_PLAYER_NOT_FOUND",
                "ERR_CANNOT_ADD_SELF",
                "ERR_ALREADY_FRIEND",
                "ERR_REQUEST_ALREADY_SENT",
                "ERR_INCOMING_REQUEST_EXISTS",
                "ERR_REQUEST_NOT_FOUND",
                "ERR_REQUEST_EXPIRED",
                "ERR_FRIEND_LIMIT",
                "ERR_TARGET_FRIEND_LIMIT",
                "ERR_PENDING_LIMIT",
                "ERR_ALREADY_BLOCKED",
                "ERR_NOT_FRIEND",
                "ERR_OPERATION_TOO_FREQUENT",
                "ERR_RELATION_CONFLICT",
                "ERR_DEPENDENCY_UNAVAILABLE"
            };
            for (var i = 0; i < codes.Length; i++)
            {
                var info = GameErrorCatalog.Resolve(codes[i]);
                Assert.IsFalse(string.IsNullOrEmpty(info.Chinese), codes[i]);
                StringAssert.DoesNotContain("拉黑了你", info.Chinese);
                StringAssert.DoesNotContain("拉黑了你", GameErrorCatalog.FormatUi(codes[i]));
            }

            var unknown = GameErrorCatalog.Resolve("ERR_FRIEND_NO_SUCH");
            Assert.IsFalse(string.IsNullOrEmpty(unknown.Chinese));
            StringAssert.Contains("ERR_FRIEND_NO_SUCH", GameErrorCatalog.FormatUi("ERR_FRIEND_NO_SUCH"));
            StringAssert.DoesNotContain("拉黑了你", unknown.Chinese);
        }

        [Test]
        public void TransportTimeout_BothFailuresDoNotWrite()
        {
            var n = 0;
            var friends = Client((req, ct) =>
            {
                n++;
                throw new GameMeshException(GameMeshErrorCode.ClientTimeout, "timeout");
            });
            friends.ApplyAsync(9, "A", CancellationToken.None).GetAwaiter().GetResult();
            Assert.AreEqual(2, n);
            Assert.AreEqual(0, friends.Friends.Count);
            Assert.AreNotEqual(FriendRelationState.FriendRelationSentPending, friends.SearchRelation);
            Assert.IsTrue(friends.ShowRetryHint);
            StringAssert.Contains("稍后重试", friends.LastError);
            Assert.AreEqual(GameMeshErrorCode.ClientTimeout, friends.LastErrorCode);
        }

        [Test]
        public void TransportTimeout_ClearDuringRetryDoesNotResend()
        {
            FriendClient friends = null;
            var n = 0;
            friends = Client((req, ct) =>
            {
                n++;
                friends.Clear();
                throw new GameMeshException(GameMeshErrorCode.ClientTimeout, "timeout");
            });
            friends.ApplyAsync(9, "A", CancellationToken.None).GetAwaiter().GetResult();
            Assert.AreEqual(1, n);
            Assert.AreEqual(0, friends.Friends.Count);
            Assert.AreNotEqual(FriendRelationState.FriendRelationSentPending, friends.SearchRelation);
        }

        [Test]
        public void InFlightFriendRefresh_DoesNotWipeLocalUpsert()
        {
            FriendClient friends = null;
            friends = Client((req, ct) =>
            {
                if (req.FriendList != null)
                {
                    friends.ApplyPush(new GameResponse
                    {
                        FriendAddedPush = new FriendAddedPush
                        {
                            Peer = new FriendBrief { PlayerId = 9, Name = "kept", Online = true }
                        }
                    });
                    return Task.FromResult(new GameResponse
                    {
                        Ok = true,
                        FriendList = new FriendListRsp { Ok = true }
                    });
                }

                return Task.FromResult(new GameResponse { Ok = true });
            });
            friends.RefreshFriendsAsync(CancellationToken.None).GetAwaiter().GetResult();
            Assert.AreEqual(1, friends.Friends.Count);
            Assert.AreEqual(9UL, friends.Friends[0].PlayerId);
        }

        [Test]
        public void ConcurrentWrites_DistinctTargetsBothApply_SameTargetDoesNotResend()
        {
            FriendClient friends = null;
            var ops = new System.Collections.Generic.List<string>();
            friends = Client((req, ct) =>
            {
                if (req.FriendDelete != null)
                {
                    ops.Add(req.FriendDelete.FriendPlayerId + ":" + req.FriendDelete.OperationId);
                    if (req.FriendDelete.FriendPlayerId == 1 && ops.Count == 1)
                    {
                        friends.DeleteAsync(2, CancellationToken.None).GetAwaiter().GetResult();
                        friends.DeleteAsync(1, CancellationToken.None).GetAwaiter().GetResult();
                    }

                    if (req.FriendDelete.FriendPlayerId == 5)
                        friends.AcceptAsync(4, CancellationToken.None).GetAwaiter().GetResult();

                    return Task.FromResult(new GameResponse
                    {
                        Ok = true,
                        FriendDelete = new FriendDeleteRsp { Ok = true }
                    });
                }

                if (req.FriendAccept != null)
                {
                    ops.Add("a:" + req.FriendAccept.OperationId);
                    return Task.FromResult(new GameResponse
                    {
                        Ok = true,
                        FriendAccept = new FriendAcceptRsp
                        {
                            Ok = true,
                            Peer = new FriendBrief { PlayerId = 3, Name = "Cee" }
                        }
                    });
                }

                return Task.FromResult(new GameResponse { Ok = true, FriendList = new FriendListRsp { Ok = true } });
            });
            friends.Friends.Add(new FriendBrief { PlayerId = 1, Name = "Ann" });
            friends.Friends.Add(new FriendBrief { PlayerId = 2, Name = "Bo" });
            friends.DeleteAsync(1, CancellationToken.None).GetAwaiter().GetResult();
            Assert.AreEqual(2, ops.Count);
            Assert.IsTrue(ops[0].StartsWith("1:"));
            Assert.IsTrue(ops[1].StartsWith("2:"));
            Assert.AreNotEqual(ops[0], ops[1]);
            Assert.AreEqual(0, friends.Friends.Count);
            StringAssert.Contains("已删除好友", friends.LastNotice);

            friends.Requests.Add(new FriendRequestInfo
            {
                RequestId = 4,
                Applicant = new FriendBrief { PlayerId = 3, Name = "Cee" }
            });
            friends.Friends.Add(new FriendBrief { PlayerId = 5, Name = "Dee" });
            friends.DeleteAsync(5, CancellationToken.None).GetAwaiter().GetResult();
            Assert.AreEqual(1, friends.Friends.Count);
            Assert.AreEqual(3UL, friends.Friends[0].PlayerId);
            StringAssert.Contains("已成为好友", friends.LastNotice);
            StringAssert.Contains("已删除好友", friends.LastNotice);
        }

        [Test]
        public void PresencePushes_DoNotRefreshFriendList()
        {
            var lists = 0;
            var friends = Client((req, ct) =>
            {
                if (req.FriendList != null)
                    lists++;
                return Task.FromResult(new GameResponse { Ok = true, FriendList = new FriendListRsp { Ok = true } });
            });
            friends.Friends.Add(new FriendBrief { PlayerId = 1, Name = "Ann", Online = false });
            friends.Friends.Add(new FriendBrief { PlayerId = 2, Name = "Bo", Online = false });
            friends.ApplyPush(new GameResponse
            {
                FriendPresencePush = new FriendPresencePush { FriendPlayerId = 1, Online = true, LastOnlineTime = 4 }
            });
            friends.ApplyPush(new GameResponse
            {
                FriendPresencePush = new FriendPresencePush { FriendPlayerId = 2, Online = true, LastOnlineTime = 5 }
            });
            friends.ApplyPush(new GameResponse
            {
                FriendPresencePush = new FriendPresencePush { FriendPlayerId = 1, Online = false, LastOnlineTime = 6 }
            });
            Assert.AreEqual(0, lists);
            Assert.AreEqual(2, friends.Friends.Count);
            Assert.AreEqual(2UL, friends.Friends[0].PlayerId);
            Assert.IsTrue(friends.Friends[0].Online);
            Assert.AreEqual(1UL, friends.Friends[1].PlayerId);
            Assert.IsFalse(friends.Friends[1].Online);
            Assert.AreEqual(6UL, friends.Friends[1].LastOnlineTime);
        }

        [Test]
        public void RowShape_ReusesLabelsUntilMembershipChanges()
        {
            var friends = Client((req, ct) => Task.FromResult(new GameResponse { Ok = true }));
            var now = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            friends.Friends.Add(new FriendBrief
            {
                PlayerId = 1,
                Name = "Ann",
                Level = 3,
                Online = true,
                LastOnlineTime = now
            });
            var shape = FriendScreen.RowShape(friends);
            var before = FriendScreen.CollectLabels(friends, 0, false);
            friends.Friends[0].Level = 9;
            friends.Friends[0].Online = false;
            friends.Friends[0].LastOnlineTime = now - 120;
            Assert.AreEqual(shape, FriendScreen.RowShape(friends));
            var after = FriendScreen.CollectLabels(friends, 0, false);
            Assert.AreEqual(before.Count, after.Count);
            Assert.AreNotEqual(before[0], after[0]);
            StringAssert.Contains("Lv.9", after[0]);
            StringAssert.Contains("分钟前", after[0]);
            friends.Friends.Add(new FriendBrief { PlayerId = 2, Name = "Bo", Online = true });
            Assert.AreNotEqual(shape, FriendScreen.RowShape(friends));
            friends.Tab = FriendPanelTab.Requests;
            Assert.AreNotEqual(FriendScreen.RowShape(friends), shape);
        }

        [Test]
        public void FormatFriendLine_ClipsLongText_AndSkipsEmptyRemark()
        {
            var empty = FriendClient.FormatFriendLine(new FriendBrief
            {
                Name = "Ann",
                Online = true,
                Level = 2,
                Remark = ""
            });
            StringAssert.DoesNotContain("备注", empty);
            var longName = new string('名', 20);
            var longRemark = new string('备', 20);
            var line = FriendClient.FormatFriendLine(new FriendBrief
            {
                Name = longName,
                Online = true,
                Level = 2,
                Remark = longRemark,
                MapName = new string('图', 20)
            });
            StringAssert.Contains("…", line);
            StringAssert.Contains("备注", line);
            StringAssert.DoesNotContain(longName, line);
            StringAssert.DoesNotContain(longRemark, line);
        }

        [Test]
        public void KickedSession_DoesNotSendFriendRequests()
        {
            var session = new GameSession();
            session.ApplyLogin(8, "s", "t", 1, "me");
            var sends = 0;
            var friends = new FriendClient(session, (req, ct) =>
            {
                sends++;
                return Task.FromResult(new GameResponse { Ok = true });
            });
            session.SessionReplaced = true;
            Assert.IsFalse(friends.CanRequest);
            friends.ApplyAsync(1, "A", CancellationToken.None).GetAwaiter().GetResult();
            friends.DeleteAsync(1, CancellationToken.None).GetAwaiter().GetResult();
            Assert.AreEqual(0, sends);
        }

        [Test]
        public void DiagnosticsText_UsesCodeAndShortOperationId()
        {
            var friends = Client((req, ct) => Task.FromResult(new GameResponse
            {
                Ok = false,
                Retryable = false,
                FriendApply = new FriendApplyRsp { Ok = false, ErrorCode = "ERR_FRIEND_LIMIT" }
            }));
            friends.ApplyAsync(3, "A", CancellationToken.None).GetAwaiter().GetResult();
            var text = friends.DiagnosticsText();
            StringAssert.Contains("code=ERR_FRIEND_LIMIT", text);
            StringAssert.Contains("retry=0", text);
            Assert.IsFalse(string.IsNullOrEmpty(friends.LastOperationId));
            StringAssert.Contains("op=" + friends.LastOperationId.Substring(0, 8), text);
            StringAssert.DoesNotContain(friends.LastOperationId.Substring(8), text);
            StringAssert.DoesNotContain("token", text);
        }

        static void AssertNoRetryHint(string code, System.Action<FriendClient> send)
        {
            var friends = Client((req, ct) =>
            {
                if (req.FriendList != null)
                    return Task.FromResult(new GameResponse { Ok = true, FriendList = new FriendListRsp { Ok = true } });
                if (req.FriendRequestList != null)
                    return Task.FromResult(new GameResponse
                    {
                        Ok = true,
                        FriendRequestList = new FriendRequestListRsp { Ok = true }
                    });
                if (req.FriendApply != null)
                    return Task.FromResult(new GameResponse
                    {
                        Ok = false,
                        Retryable = false,
                        FriendApply = new FriendApplyRsp { Ok = false, ErrorCode = code }
                    });
                if (req.FriendDelete != null)
                    return Task.FromResult(new GameResponse
                    {
                        Ok = false,
                        Retryable = false,
                        FriendDelete = new FriendDeleteRsp { Ok = false, ErrorCode = code }
                    });
                if (req.FriendAccept != null)
                    return Task.FromResult(new GameResponse
                    {
                        Ok = false,
                        Retryable = false,
                        FriendAccept = new FriendAcceptRsp { Ok = false, ErrorCode = code }
                    });
                return Task.FromResult(new GameResponse { Ok = true });
            });
            send(friends);
            Assert.IsFalse(friends.ShowRetryHint, code);
            StringAssert.DoesNotContain("稍后重试", friends.LastError, code);
        }

        static FriendClient Client(System.Func<GameRequest, CancellationToken, Task<GameResponse>> request)
        {
            var session = new GameSession();
            session.ApplyLogin(8, "s", "t", 1, "me");
            return new FriendClient(session, request);
        }
    }
}
