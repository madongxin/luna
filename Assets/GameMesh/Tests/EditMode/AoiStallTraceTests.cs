using System;
using System.IO;
using GameMesh.Aoi;
using NUnit.Framework;

namespace GameMesh.Tests.EditMode
{
    public sealed class AoiStallTraceTests
    {
        [Test]
        public void Reason_SeparatesMissingAoiFromRenderStall()
        {
            Assert.IsNull(AoiStallRules.Reason(0.2f, 10f, 0f, 0f, 2));
            Assert.AreEqual("no_pose", AoiStallRules.Reason(2f, 0f, 0f, 0f, 0));
            Assert.AreEqual("no_sample_time", AoiStallRules.Reason(2f, -1f, 0f, 0f, 1));
            Assert.AreEqual("render_stall", AoiStallRules.Reason(2f, 0.2f, 4f, 0f, 2));
            Assert.AreEqual("no_aoi", AoiStallRules.Reason(2f, 4f, 0.1f, 0f, 2));
            Assert.AreEqual("held_at_sample", AoiStallRules.Reason(2f, 1f, 0.1f, 0f, 2));
        }

        [Test]
        public void Trace_WritesStoppedRemotesOncePerFrame()
        {
            var dir = Path.Combine(Path.GetTempPath(), "aoi-stall-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "stall.jsonl");
            try
            {
                var trace = new AoiStallTrace();
                trace.Open(path);
                var still = Sample(9, "Bot044", 10f, 20f, 5f);
                var moving = Sample(8, "Bot001", 1f, 1f, 0.1f);
                trace.BeginFrame(0f, 1, 0.016f, 0f, 0f);
                trace.Observe(still);
                trace.Observe(moving);
                trace.EndFrame();

                trace.BeginFrame(1.2f, 70, 0.016f, 0f, 0f);
                trace.Observe(still);
                moving.X = 2f;
                trace.Observe(moving);
                trace.EndFrame();
                trace.Flush();

                var text = File.ReadAllText(path);
                StringAssert.Contains("\"kind\":\"aoi_stall_trace\"", text);
                StringAssert.Contains("\"frame\":70", text);
                StringAssert.Contains("\"reason\":\"no_aoi\"", text);
                StringAssert.Contains("Bot044", text);
                StringAssert.DoesNotContain("Bot001", text);
                StringAssert.DoesNotContain("\"frame\":1,", text);
            }
            finally
            {
                try { Directory.Delete(dir, true); }
                catch { /* temp cleanup */ }
            }
        }

        static AoiStallSample Sample(ulong id, string name, float x, float z, float age)
        {
            return new AoiStallSample
            {
                EntityId = id,
                PlayerId = id,
                Name = name,
                X = x,
                Y = 0f,
                Z = z,
                AuthorityX = x,
                AuthorityZ = z,
                TargetX = x,
                TargetZ = z,
                SampleAge = age,
                PoseCount = 2,
                HasAuthority = true,
                StateSeq = 10
            };
        }
    }
}
