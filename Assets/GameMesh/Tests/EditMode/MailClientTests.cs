using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GameMesh.Auth;
using GameMesh.Mail;
using GameMesh.Protocol;
using NUnit.Framework;

namespace GameMesh.Tests.EditMode
{
    public sealed class MailClientTests
    {
        [Test]
        public void Refresh_ZeroOneAndManyPages_ReplacesOnlyAfterTheLastPage()
        {
            var mail = Client(Script(
                Summary(1, 0, 9, 8, 0, 3, 1_000),
                List(3, 1_000, ""),
                Summary(1, 2, 9, 8, 4, 4, 1_100),
                List(4, 1_100, "", Brief(7, "SYSTEM", "UNREAD", false)),
                Summary(1, 2, 0, 0, 4, 5, 1_200),
                List(5, 1_200, "c1", Brief(1, "SYSTEM", "UNREAD", true), Brief(1, "SYSTEM", "READ", true)),
                List(5, 1_200, "", Brief(2, "ACTIVITY", "UNREAD", false))));

            mail.RefreshAsync(CancellationToken.None).GetAwaiter().GetResult();
            Assert.AreEqual(0, mail.Page.Mails.Count);
            Assert.IsTrue(mail.Page.HasBadge);
            Assert.AreEqual(1, mail.Page.UnreadTotal);

            mail.RefreshAsync(CancellationToken.None).GetAwaiter().GetResult();
            Assert.AreEqual(1, mail.Page.Mails.Count);
            Assert.AreEqual(3, mail.Page.UnreadTotal);
            Assert.AreEqual(4, mail.Page.Unclaimed);
            Assert.AreEqual(1_100, mail.EstimatedServerUtc());

            mail.RefreshAsync(CancellationToken.None).GetAwaiter().GetResult();
            Assert.AreEqual(2, mail.Page.Mails.Count);
            Assert.AreEqual("READ", mail.Page.Mails[0].ReadState);
            Assert.AreEqual(2ul, mail.Page.Mails[1].MailId);
        }

        [Test]
        public void EmptyPageOrRepeatedCursor_KeepsPreviousList()
        {
            var mail = Client(Script(
                Summary(0, 0, 0, 0, 0, 2, 10),
                List(2, 10, "", Brief(4, "SYSTEM", "READ", false)),
                Summary(0, 0, 0, 0, 0, 3, 11),
                List(3, 11, "loop"),
                Summary(0, 0, 0, 0, 0, 4, 12),
                List(4, 12, "a", Brief(9, "SYSTEM", "READ", false)),
                List(4, 12, "a", Brief(10, "SYSTEM", "READ", false))));

            mail.RefreshAsync(CancellationToken.None).GetAwaiter().GetResult();
            mail.RefreshAsync(CancellationToken.None).GetAwaiter().GetResult();
            Assert.AreEqual(1, mail.Page.Mails.Count);
            Assert.AreEqual(4ul, mail.Page.Mails[0].MailId);
            StringAssert.Contains("空页", mail.Page.LastError);

            mail.RefreshAsync(CancellationToken.None).GetAwaiter().GetResult();
            Assert.AreEqual(4ul, mail.Page.Mails[0].MailId);
            StringAssert.Contains("游标重复", mail.Page.LastError);
        }

        [Test]
        public void OlderSnapshotAndFilterChange_DoNotReplace()
        {
            var mail = Client(Script(
                Summary(1, 0, 0, 0, 0, 8, 50),
                List(8, 50, "", Brief(1, "SYSTEM", "UNREAD", false)),
                Summary(0, 0, 5, 5, 0, 7, 40),
                List(7, 40, "", Brief(2, "ACTIVITY", "READ", false)),
                Summary(2, 0, 0, 0, 0, 9, 60),
                List(9, 60, "", Brief(3, "ACTIVITY", "UNREAD", false))));
            mail.RefreshAsync(CancellationToken.None).GetAwaiter().GetResult();
            mail.RefreshAsync(CancellationToken.None).GetAwaiter().GetResult();
            Assert.AreEqual(1ul, mail.Page.Mails[0].MailId);
            Assert.AreEqual(8, mail.Page.MailboxVersion);
            Assert.AreEqual(1, mail.Page.UnreadTotal);

            var gate = new TaskCompletionSource<GameResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            var switched = Client((req, ct) =>
            {
                if (req.MailboxSummary != null)
                    return Task.FromResult(Summary(0, 0, 0, 0, 0, 2, 10));
                return gate.Task;
            });
            switched.Page.Mails.Add(Brief(5, "SYSTEM", "READ", false));
            var pending = switched.RefreshAsync(CancellationToken.None);
            switched.Category = "ACTIVITY";
            gate.SetResult(List(2, 10, "", Brief(6, "SYSTEM", "READ", false)));
            pending.GetAwaiter().GetResult();
            Assert.AreEqual(5ul, switched.Page.Mails[0].MailId);
        }

        [Test]
        public void SocialAndTradeUnread_DoNotRaiseTheBadge()
        {
            var mail = Client(Script(Summary(0, 0, 6, 7, 0, 1, 10), List(1, 10, "")));
            mail.RefreshAsync(CancellationToken.None).GetAwaiter().GetResult();
            Assert.AreEqual(0, mail.Page.UnreadTotal);
            Assert.IsFalse(mail.Page.HasBadge);
        }

        [Test]
        public void Filters_AreSentAndDeliverIsNeverBuilt()
        {
            var seen = new List<GameRequest>();
            var mail = Client((req, ct) =>
            {
                seen.Add(req);
                if (req.MailboxSummary != null)
                    return Task.FromResult(Summary(0, 0, 0, 0, 0, 1, 10));
                return Task.FromResult(List(1, 10, ""));
            });
            mail.Category = "SYSTEM";
            mail.FilterUnread = true;
            mail.FilterUnclaimed = true;
            mail.FilterFavorite = true;
            mail.FilterExpiring = true;
            mail.RefreshAsync(CancellationToken.None).GetAwaiter().GetResult();
            Assert.AreEqual("SYSTEM", seen[1].MailList.Category);
            Assert.IsTrue(seen[1].MailList.Unread && seen[1].MailList.Unclaimed);
            Assert.IsTrue(seen[1].MailList.Favorite && seen[1].MailList.ExpiringSoon);
            Assert.AreEqual(MailClient.PageLimit, seen[1].MailList.Limit);
            foreach (var req in seen)
                Assert.IsNull(req.MailDeliver);
        }

        [Test]
        public void Claim_DoubleClickAndTimeout_ReuseOneKey_SyncDoesNotInventItems()
        {
            var keys = new List<string>();
            var gate = new TaskCompletionSource<GameResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            var once = Client((req, ct) =>
            {
                if (req.MailClaim == null)
                    return Task.FromResult(new GameResponse { Ok = true });
                keys.Add(req.MailClaim.IdempotencyKey);
                return gate.Task;
            });
            once.Page.Selected = Detail(9, "UNCLAIMED", "claim");
            var first = once.ClaimAsync(9, CancellationToken.None);
            var second = once.ClaimAsync(9, CancellationToken.None);
            Assert.AreEqual(1, keys.Count);
            gate.SetResult(ClaimOk(9, "CLAIMED"));
            first.GetAwaiter().GetResult();
            second.GetAwaiter().GetResult();
            Assert.IsNull(once.Page.Selected.Attachments.Count == 0 ? null : once.Page.Selected.Attachments[0]);

            string kept = null;
            var timed = Client((req, ct) =>
            {
                if (req.MailClaim != null)
                {
                    if (kept == null)
                    {
                        kept = req.MailClaim.IdempotencyKey;
                        throw new TimeoutException("mail");
                    }

                    Assert.AreEqual(kept, req.MailClaim.IdempotencyKey);
                    return Task.FromResult(ClaimOk(9, "CLAIMED"));
                }

                if (req.MailboxSummary != null)
                    return Task.FromResult(Summary(0, 0, 0, 0, 1, 2, 20));
                if (req.MailDeliver != null)
                    Assert.Fail("deliver");
                return Task.FromResult(List(2, 20, ""));
            });
            timed.Page.Selected = Detail(9, "UNCLAIMED", "claim");
            timed.Page.Mails.Add(timed.Page.Selected.Brief);
            timed.ClaimAsync(9, CancellationToken.None).GetAwaiter().GetResult();
            timed.ClaimAsync(9, CancellationToken.None).GetAwaiter().GetResult();
            Assert.AreEqual("CLAIMED", timed.Page.Selected.Brief.AttachmentState);

            var sync = Client((req, ct) =>
            {
                if (req.MailClaim != null)
                {
                    return Task.FromResult(new GameResponse
                    {
                        Ok = false,
                        MailClaim = new MailClaimRsp { Ok = false, ErrorCode = "STATE_SYNC_REQUIRED" }
                    });
                }

                if (req.MailDeliver != null || req.GrantItem != null)
                    Assert.Fail("grant");
                if (req.MailboxSummary != null)
                    return Task.FromResult(Summary(0, 0, 0, 0, 1, 3, 30));
                return Task.FromResult(List(3, 30, "", Brief(9, "SYSTEM", "READ", true, "UNCLAIMED")));
            });
            sync.Page.Selected = Detail(9, "UNCLAIMED", "claim");
            sync.Page.Mails.Add(sync.Page.Selected.Brief);
            sync.ClaimAsync(9, CancellationToken.None).GetAwaiter().GetResult();
            StringAssert.Contains("正在同步背包", sync.Page.LastNotice);
            Assert.AreEqual("UNCLAIMED", sync.Page.Mails[0].AttachmentState);
        }

        [Test]
        public void Claim_NotAllowedOrAlreadyClaiming_DoesNotSendANewClaim()
        {
            var claims = 0;
            var mail = Client((req, ct) =>
            {
                if (req.MailClaim != null)
                    claims++;
                if (req.MailboxSummary != null)
                    return Task.FromResult(Summary(0, 0, 0, 0, 0, 1, 10));
                return Task.FromResult(List(1, 10, ""));
            });
            mail.Page.Selected = Detail(3, "UNCLAIMED", "read");
            mail.ClaimAsync(3, CancellationToken.None).GetAwaiter().GetResult();
            Assert.AreEqual(0, claims);

            mail.Page.Selected = Detail(3, "CLAIMING", "claim");
            mail.ClaimAsync(3, CancellationToken.None).GetAwaiter().GetResult();
            Assert.AreEqual(0, claims);
            StringAssert.Contains("正在同步背包", mail.Page.LastNotice);
        }

        [Test]
        public void BatchClaim_KeepsFailuresSeparate()
        {
            var mail = Client((req, ct) =>
            {
                Assert.IsNull(req.MailDeliver);
                Assert.AreEqual(2, req.MailBatchClaim.MailIds.Count);
                Assert.IsFalse(string.IsNullOrEmpty(req.MailBatchClaim.IdempotencyKeyPrefix));
                return Task.FromResult(new GameResponse
                {
                    Ok = true,
                    MailBatchClaim = new MailBatchClaimRsp
                    {
                        Ok = true,
                        Results =
                        {
                            new MailClaimResult { MailId = 1, Ok = true, AttachmentState = "CLAIMED" },
                            new MailClaimResult { MailId = 2, Ok = false, ErrorCode = "ERR_MAIL_BAG_FULL", Message = "full" }
                        }
                    }
                });
            });
            mail.Page.Mails.Add(Brief(1, "SYSTEM", "READ", true, "UNCLAIMED"));
            mail.Page.Mails.Add(Brief(2, "ACTIVITY", "READ", true, "PARTIAL"));
            mail.Page.Mails.Add(Brief(3, "SYSTEM", "READ", true, "CLAIMING"));
            mail.BatchClaimAsync(CancellationToken.None).GetAwaiter().GetResult();
            Assert.AreEqual("CLAIMED", mail.Page.Mails[0].AttachmentState);
            Assert.AreEqual("PARTIAL", mail.Page.Mails[1].AttachmentState);
            Assert.AreEqual(1, mail.Page.ClaimFailures.Count);
            StringAssert.Contains("失败 1", mail.Page.LastNotice);
        }

        [Test]
        public void DeleteWithoutDeleteAction_DoesNotRemoveOrSend()
        {
            var sends = 0;
            var mail = Client((req, ct) =>
            {
                sends++;
                return Task.FromResult(new GameResponse { Ok = true });
            });
            var brief = Brief(8, "SYSTEM", "READ", true, "UNCLAIMED");
            mail.Page.Mails.Add(brief);
            mail.Page.Selected = new MailDetail { Brief = brief };
            mail.BatchDeleteAsync(new[] { 8ul }, CancellationToken.None).GetAwaiter().GetResult();
            Assert.AreEqual(0, sends);
            Assert.AreEqual(1, mail.Page.Mails.Count);
            StringAssert.Contains("未领取附件不能删除", mail.Page.LastError);
        }

        [Test]
        public void Delete_FailedIdStays_ReadFailureDoesNotDropUnread()
        {
            var mail = Client((req, ct) =>
            {
                if (req.MailBatchDelete != null)
                {
                    return Task.FromResult(new GameResponse
                    {
                        Ok = true,
                        MailBatchDelete = new MailBatchDeleteRsp
                        {
                            Ok = true,
                            SuccessCount = 1,
                            FailedMailIds = { 2 }
                        }
                    });
                }

                if (req.MailRead != null)
                {
                    return Task.FromResult(new GameResponse
                    {
                        Ok = false,
                        MailRead = new MailReadRsp { Ok = false, ErrorCode = "ERR_MAIL_EXPIRED" }
                    });
                }

                if (req.MailGet != null)
                {
                    return Task.FromResult(new GameResponse
                    {
                        Ok = true,
                        MailGet = new MailGetRsp { Ok = true, Mail = Detail(4, "NONE", "read") }
                    });
                }

                return Task.FromResult(new GameResponse { Ok = true });
            });
            mail.Page.Mails.Add(Brief(1, "SYSTEM", "READ", false));
            mail.Page.Mails.Add(Brief(2, "SYSTEM", "READ", false));
            mail.Page.Selected = new MailDetail { Brief = mail.Page.Mails[1], AllowedActions = { "delete" } };
            mail.BatchDeleteAsync(new[] { 1ul, 2ul }, CancellationToken.None).GetAwaiter().GetResult();
            Assert.AreEqual(1, mail.Page.Mails.Count);
            Assert.AreEqual(2ul, mail.Page.Mails[0].MailId);

            mail.Page.Mails.Add(Brief(4, "SYSTEM", "UNREAD", false));
            mail.Page.UnreadTotal = 1;
            mail.OpenAsync(4, CancellationToken.None).GetAwaiter().GetResult();
            Assert.AreEqual(1, mail.Page.UnreadTotal);
            Assert.AreEqual("UNREAD", mail.Page.Mails[1].ReadState);
        }

        [Test]
        public void BatchReadAndFavorite_FollowTheServerResult()
        {
            var mail = Client((req, ct) =>
            {
                if (req.MailBatchRead != null)
                {
                    return Task.FromResult(new GameResponse
                    {
                        Ok = true,
                        MailBatchRead = new MailBatchReadRsp { Ok = true, SuccessCount = 1 }
                    });
                }

                return Task.FromResult(new GameResponse
                {
                    Ok = true,
                    MailFavorite = new MailFavoriteRsp { Ok = true, IsFavorite = false }
                });
            });
            mail.Page.Mails.Add(Brief(1, "SYSTEM", "UNREAD", false));
            mail.Page.Mails.Add(Brief(2, "SYSTEM", "UNREAD", false));
            mail.Page.UnreadTotal = 2;
            mail.BatchReadAsync(CancellationToken.None).GetAwaiter().GetResult();
            Assert.AreEqual("UNREAD", mail.Page.Mails[0].ReadState);
            Assert.AreEqual(2, mail.Page.UnreadTotal);

            mail.Page.Selected = new MailDetail { Brief = mail.Page.Mails[0], AllowedActions = { "favorite" } };
            mail.Page.Mails[0].IsFavorite = true;
            mail.FavoriteAsync(1, true, CancellationToken.None).GetAwaiter().GetResult();
            Assert.IsFalse(mail.Page.Mails[0].IsFavorite);
        }

        [Test]
        public void Clear_DropsALateList()
        {
            var gate = new TaskCompletionSource<GameResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            var mail = Client((req, ct) => req.MailboxSummary != null
                ? gate.Task
                : Task.FromResult(List(1, 10, "", Brief(1, "SYSTEM", "UNREAD", false))));
            var pending = mail.RefreshAsync(CancellationToken.None);
            mail.Clear();
            gate.SetResult(Summary(1, 0, 0, 0, 0, 1, 10));
            pending.GetAwaiter().GetResult();
            Assert.AreEqual(0, mail.Page.Mails.Count);
            Assert.IsFalse(mail.Page.HasBadge);
        }

        [Test]
        public void ExpiryAndUnknownItem_UseServerTimeAndDoNotInventNames()
        {
            Assert.AreEqual("已过期", MailClient.ExpireLabel(100, 100));
            Assert.AreEqual("即将过期", MailClient.ExpireLabel(100 + 3600, 100));
            Assert.AreEqual("", MailClient.ExpireLabel(100 + 80L * 3600L, 100));
            Assert.AreEqual("未知物品 x2", MailClient.AttachmentLine(new MailAttachmentDto { AssetId = 0, Count = 2 }));
            Assert.AreEqual("物品 #15 x1", MailClient.AttachmentLine(new MailAttachmentDto { AssetId = 15, Count = 1 }));
            StringAssert.DoesNotContain("<", MailClient.PlainText("<color=red>hi</color>"));
            Assert.IsFalse(MailClient.Allows(new MailDetail(), "claim"));
            Assert.IsTrue(MailClient.Allows(Detail(1, "NONE", "CLAIM"), "claim"));
        }

        static MailClient Client(Func<GameRequest, CancellationToken, Task<GameResponse>> request)
        {
            return new MailClient(new GameSession { PlayerId = 42 }, request);
        }

        static Func<GameRequest, CancellationToken, Task<GameResponse>> Script(params GameResponse[] steps)
        {
            var left = new Queue<GameResponse>(steps);
            return (req, ct) =>
            {
                Assert.IsNull(req.MailDeliver);
                Assert.IsNull(req.GrantItem);
                return Task.FromResult(left.Dequeue());
            };
        }

        static GameResponse Summary(uint system, uint activity, uint social, uint trade, uint unclaimed, long version, long utc)
        {
            return new GameResponse
            {
                Ok = true,
                MailboxSummary = new MailboxSummaryRsp
                {
                    Ok = true,
                    UnreadSystem = system,
                    UnreadActivity = activity,
                    UnreadSocial = social,
                    UnreadTrade = trade,
                    UnclaimedAttachment = unclaimed,
                    MailboxVersion = version,
                    ServerTimeUtc = utc,
                    CurrentCount = 1,
                    MaxCapacity = 100
                }
            };
        }

        static GameResponse List(long version, long utc, string next, params MailBrief[] mails)
        {
            var body = new MailListRsp
            {
                Ok = true,
                NextCursor = next,
                MailboxVersion = version,
                ServerTimeUtc = utc
            };
            for (var i = 0; i < mails.Length; i++)
                body.Mails.Add(mails[i]);
            return new GameResponse { Ok = true, MailList = body };
        }

        static MailBrief Brief(ulong id, string category, string read, bool attachment, string attachmentState = "")
        {
            return new MailBrief
            {
                MailId = id,
                Category = category,
                Title = "t" + id,
                SenderName = "system",
                ReadState = read,
                HasAttachment = attachment,
                AttachmentState = attachment ? (string.IsNullOrEmpty(attachmentState) ? "UNCLAIMED" : attachmentState) : attachmentState
            };
        }

        static MailDetail Detail(ulong id, string attachmentState, string action)
        {
            return new MailDetail
            {
                Brief = Brief(id, "SYSTEM", "UNREAD", attachmentState != "NONE", attachmentState == "NONE" ? "" : attachmentState),
                Body = "body",
                AllowedActions = { action }
            };
        }

        static GameResponse ClaimOk(ulong id, string state)
        {
            return new GameResponse
            {
                Ok = true,
                MailClaim = new MailClaimRsp
                {
                    Ok = true,
                    Result = new MailClaimResult { Ok = true, MailId = id, AttachmentState = state }
                }
            };
        }
    }
}
