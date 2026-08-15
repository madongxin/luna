using System.Collections.Generic;
using GameMesh.Network;
using GameMesh.Protocol;
using Google.Protobuf;
using NUnit.Framework;

namespace GameMesh.Tests.EditMode
{
    public sealed class FrameCodecTests
    {
        [Test]
        public void Encode_UsesBigEndianLength()
        {
            var payload = new byte[] { 1, 2, 3, 4 };
            var frame = FrameCodec.Encode(payload);
            Assert.AreEqual(8, frame.Length);
            Assert.AreEqual(0, frame[0]);
            Assert.AreEqual(0, frame[1]);
            Assert.AreEqual(0, frame[2]);
            Assert.AreEqual(4, frame[3]);
            CollectionAssert.AreEqual(payload, Slice(frame, 4, payload.Length));
        }

        [Test]
        public void Decode_HalfPacketThenComplete()
        {
            var payload = new byte[] { 9, 8, 7 };
            var frame = FrameCodec.Encode(payload);
            var buffer = new List<byte>();
            buffer.AddRange(Slice(frame, 0, 5));
            Assert.AreEqual(FrameDecodeStatus.NeedMore, FrameCodec.TryDecode(buffer, out _, out _));
            buffer.AddRange(Slice(frame, 5, frame.Length - 5));
            Assert.AreEqual(FrameDecodeStatus.Ok, FrameCodec.TryDecode(buffer, out var decoded, out var len));
            Assert.AreEqual(3u, len);
            CollectionAssert.AreEqual(payload, decoded);
            Assert.AreEqual(0, buffer.Count);
        }

        [Test]
        public void Decode_CoalescedMultiFrame()
        {
            var a = FrameCodec.Encode(new byte[] { 1 });
            var b = FrameCodec.Encode(new byte[] { 2, 3 });
            var buffer = new List<byte>();
            buffer.AddRange(a);
            buffer.AddRange(b);
            Assert.AreEqual(FrameDecodeStatus.Ok, FrameCodec.TryDecode(buffer, out var pa, out _));
            Assert.AreEqual(FrameDecodeStatus.Ok, FrameCodec.TryDecode(buffer, out var pb, out _));
            CollectionAssert.AreEqual(new byte[] { 1 }, pa);
            CollectionAssert.AreEqual(new byte[] { 2, 3 }, pb);
        }

        [Test]
        public void Decode_RejectsZeroAndOversize()
        {
            var zero = new List<byte> { 0, 0, 0, 0 };
            Assert.AreEqual(FrameDecodeStatus.InvalidLength, FrameCodec.TryDecode(zero, out _, out var zlen));
            Assert.AreEqual(0u, zlen);

            var huge = new List<byte> { 0x01, 0x00, 0x00, 0x01 };
            Assert.AreEqual(FrameDecodeStatus.InvalidLength, FrameCodec.TryDecode(huge, out _, out var hlen));
            Assert.Greater(hlen, (uint)FrameCodec.MaxPayloadBytes);
        }

        [Test]
        public void Encode_RejectsEmpty()
        {
            Assert.Throws<GameMeshException>(() => FrameCodec.Encode(System.Array.Empty<byte>()));
        }

        [Test]
        public void GameResponse_RoundTrip()
        {
            var rsp = new GameResponse { Seq = 7, Ok = true, Message = "ok" };
            var parsed = GameResponse.Parser.ParseFrom(rsp.ToByteArray());
            Assert.AreEqual(7ul, parsed.Seq);
            Assert.IsTrue(parsed.Ok);
        }

        static byte[] Slice(byte[] src, int offset, int count)
        {
            var dst = new byte[count];
            System.Array.Copy(src, offset, dst, 0, count);
            return dst;
        }
    }
}
