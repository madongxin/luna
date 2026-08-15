using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace GameMesh.Network
{
    public enum FrameDecodeStatus
    {
        NeedMore = 0,
        Ok = 1,
        InvalidLength = 2
    }

    public static class FrameCodec
    {
        public const int MaxPayloadBytes = 4 * 1024 * 1024;
        public const int HeaderBytes = 4;

        public static byte[] Encode(ReadOnlySpan<byte> payload)
        {
            if (payload.Length == 0 || payload.Length > MaxPayloadBytes)
                throw new GameMeshException(GameMeshErrorCode.ClientProtocol, $"invalid payload length {payload.Length}");

            var frame = new byte[HeaderBytes + payload.Length];
            BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(0, HeaderBytes), (uint)payload.Length);
            payload.CopyTo(frame.AsSpan(HeaderBytes));
            return frame;
        }

        public static void WriteUInt32BigEndian(Span<byte> dest, uint value)
        {
            BinaryPrimitives.WriteUInt32BigEndian(dest, value);
        }

        public static uint ReadUInt32BigEndian(ReadOnlySpan<byte> src)
        {
            return BinaryPrimitives.ReadUInt32BigEndian(src);
        }

        public static FrameDecodeStatus TryDecode(IList<byte> buffer, out byte[] payload, out uint length)
        {
            payload = Array.Empty<byte>();
            length = 0;
            if (buffer == null || buffer.Count < HeaderBytes)
                return FrameDecodeStatus.NeedMore;

            Span<byte> header = stackalloc byte[HeaderBytes];
            header[0] = buffer[0];
            header[1] = buffer[1];
            header[2] = buffer[2];
            header[3] = buffer[3];
            length = ReadUInt32BigEndian(header);
            if (length == 0 || length > MaxPayloadBytes)
                return FrameDecodeStatus.InvalidLength;
            if (buffer.Count < HeaderBytes + (int)length)
                return FrameDecodeStatus.NeedMore;

            payload = new byte[length];
            for (var i = 0; i < payload.Length; i++)
                payload[i] = buffer[HeaderBytes + i];
            RemoveRange(buffer, 0, HeaderBytes + (int)length);
            return FrameDecodeStatus.Ok;
        }

        static void RemoveRange(IList<byte> buffer, int index, int count)
        {
            if (buffer is List<byte> list)
            {
                list.RemoveRange(index, count);
                return;
            }

            for (var i = 0; i < count; i++)
                buffer.RemoveAt(index);
        }
    }
}
