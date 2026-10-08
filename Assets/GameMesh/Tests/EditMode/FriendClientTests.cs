using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GameMesh.Auth;
using GameMesh.Friends;
using GameMesh.Protocol;
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
                        FriendApply = new FriendApplyRsp { Ok = false, ErrorCode = "ERR_DEPENDENCY_UNAVAILABLE" }
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

        static FriendClient Client(System.Func<GameRequest, CancellationToken, Task<GameResponse>> request)
        {
            var session = new GameSession();
            session.ApplyLogin(8, "s", "t", 1, "me");
            return new FriendClient(session, request);
        }
    }
}
