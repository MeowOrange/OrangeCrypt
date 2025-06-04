using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OrangeCrypt
{
    internal class OffsetStream : Stream
    {
        private readonly Stream _baseStream;
        private readonly long _offset;
        private long _position;
        private readonly bool _leaveOpen;

        /// <summary>
        /// 创建一个带有偏移量的包装流
        /// </summary>
        /// <param name="baseStream">基础流</param>
        /// <param name="offset">偏移量（字节）</param>
        /// <param name="leaveOpen">当包装流释放时是否保持基础流打开</param>
        public OffsetStream(Stream baseStream, long offset, bool leaveOpen = false)
        {
            _baseStream = baseStream ?? throw new ArgumentNullException(nameof(baseStream));

            if (!_baseStream.CanRead && !_baseStream.CanWrite)
                throw new ArgumentException("Base stream must be at least readable or writable", nameof(baseStream));

            if (!_baseStream.CanSeek)
                throw new ArgumentException("Base stream must support seeking", nameof(baseStream));

            if (offset < 0)
                throw new ArgumentOutOfRangeException(nameof(offset), "Offset must be non-negative");

            _offset = offset;
            _leaveOpen = leaveOpen;
            _position = 0;
        }

        public override bool CanRead => _baseStream.CanRead;
        public override bool CanSeek => _baseStream.CanSeek;
        public override bool CanWrite => _baseStream.CanWrite;
        public override bool CanTimeout => _baseStream.CanTimeout;

        public override long Length
        {
            get
            {
                long baseLength = _baseStream.Length;
                return baseLength > _offset ? baseLength - _offset : 0;
            }
        }

        public override long Position
        {
            get => _position;
            set => Seek(value, SeekOrigin.Begin);
        }

        public override int ReadTimeout
        {
            get => _baseStream.ReadTimeout;
            set => _baseStream.ReadTimeout = value;
        }

        public override int WriteTimeout
        {
            get => _baseStream.WriteTimeout;
            set => _baseStream.WriteTimeout = value;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ValidateBufferArguments(buffer, offset, count);

            if (_position >= Length)
                return 0;

            long realPosition = _offset + _position;
            if (realPosition > _baseStream.Length)
                return 0;

            _baseStream.Position = realPosition;
            int bytesToRead = (int)Math.Min(count, Length - _position);
            int bytesRead = _baseStream.Read(buffer, offset, bytesToRead);
            _position += bytesRead;
            return bytesRead;
        }

        public override int Read(Span<byte> buffer)
        {
            if (_position >= Length)
                return 0;

            long realPosition = _offset + _position;
            if (realPosition > _baseStream.Length)
                return 0;

            _baseStream.Position = realPosition;
            int bytesToRead = (int)Math.Min(buffer.Length, Length - _position);
            int bytesRead = _baseStream.Read(buffer.Slice(0, bytesToRead));
            _position += bytesRead;
            return bytesRead;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            ValidateBufferArguments(buffer, offset, count);

            long realPosition = _offset + _position;
            _baseStream.Position = realPosition;
            _baseStream.Write(buffer, offset, count);
            _position += count;

            // 如果写入扩展了流，更新位置
            if (_position > Length)
            {
                _baseStream.SetLength(realPosition + count);
            }
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            long realPosition = _offset + _position;
            _baseStream.Position = realPosition;
            _baseStream.Write(buffer);
            _position += buffer.Length;

            // 如果写入扩展了流，更新位置
            if (_position > Length)
            {
                _baseStream.SetLength(realPosition + buffer.Length);
            }
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            long newPosition;
            switch (origin)
            {
                case SeekOrigin.Begin:
                    newPosition = offset;
                    break;
                case SeekOrigin.Current:
                    newPosition = _position + offset;
                    break;
                case SeekOrigin.End:
                    newPosition = Length + offset;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(origin));
            }

            if (newPosition < 0)
                throw new IOException("Attempted to seek before the beginning of the stream.");

            _position = newPosition;
            return _position;
        }

        public override void SetLength(long value)
        {
            if (value < 0)
                throw new ArgumentOutOfRangeException(nameof(value), "Length must be non-negative");

            _baseStream.SetLength(_offset + value);

            // 如果当前位置超过了新长度，调整位置
            if (_position > value)
            {
                _position = value;
            }
        }

        public override void Flush()
        {
            _baseStream.Flush();
        }

        protected override void Dispose(bool disposing)
        {
            try
            {
                if (disposing && !_leaveOpen)
                {
                    _baseStream?.Dispose();
                }
            }
            finally
            {
                base.Dispose(disposing);
            }
        }
    }
}
