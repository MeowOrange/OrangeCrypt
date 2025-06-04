using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;

namespace OrangeCrypt
{
    internal sealed class EncryptedStreamOpt : Stream
    {
        private readonly Stream _baseStream;
        private readonly ICryptoTransform _tweakEncryptor;
        private readonly ICryptoTransform _dataEncryptor;
        private readonly ICryptoTransform _dataDecryptor;
        private readonly int _sectorSize;
        private long _position;
        private bool _disposed;

        // 可重用缓冲区
        private readonly byte[] _sectorBuffer;
        private readonly byte[] _tweakBuffer = new byte[16];
        private readonly byte[] _blockBuffer = new byte[16];
        private readonly byte[] _encryptedBlockBuffer = new byte[16];

        public EncryptedStreamOpt(Stream baseStream, byte[] key, int sectorSize = 512)
        {
            if (baseStream == null) throw new ArgumentNullException(nameof(baseStream));
            if (key == null || key.Length != 64)
                throw new ArgumentException("Key must be 64 bytes (512 bits) for AES-256-XTS");
            if (sectorSize % 16 != 0)
                throw new ArgumentException("Sector size must be a multiple of 16 bytes", nameof(sectorSize));

            _baseStream = baseStream;
            _sectorSize = sectorSize;
            _sectorBuffer = new byte[sectorSize];

            // 初始化数据加密器
            using (var dataAes = Aes.Create())
            {
                dataAes.KeySize = 256;
                dataAes.Mode = CipherMode.ECB;
                dataAes.Padding = PaddingMode.None;
                dataAes.Key = key.AsSpan(0, 32).ToArray();
                _dataEncryptor = dataAes.CreateEncryptor();
                _dataDecryptor = dataAes.CreateDecryptor();
            }

            // 初始化tweak加密器
            using (var tweakAes = Aes.Create())
            {
                tweakAes.KeySize = 256;
                tweakAes.Mode = CipherMode.ECB;
                tweakAes.Padding = PaddingMode.None;
                tweakAes.Key = key.AsSpan(32, 32).ToArray();
                _tweakEncryptor = tweakAes.CreateEncryptor();
            }
        }

        #region XTS-AES 核心算法 (修正版)
        private void XtsTransform(
            ReadOnlySpan<byte> input,
            Span<byte> output,
            long sectorIndex,
            bool encrypt)
        {
            // 生成并加密tweak值
            _tweakBuffer.AsSpan().Clear();
            BinaryPrimitives.WriteInt64LittleEndian(_tweakBuffer, sectorIndex);
            _tweakEncryptor.TransformBlock(_tweakBuffer, 0, 16, _tweakBuffer, 0);

            // 复制初始tweak用于后续更新
            Span<byte> currentTweak = stackalloc byte[16];
            _tweakBuffer.AsSpan().CopyTo(currentTweak);

            for (int pos = 0; pos < _sectorSize; pos += 16)
            {
                // 1. XOR tweak
                for (int i = 0; i < 16; i++)
                {
                    _blockBuffer[i] = (byte)(input[pos + i] ^ currentTweak[i]);
                }

                // 2. AES加密/解密块
                if (encrypt)
                {
                    _dataEncryptor.TransformBlock(_blockBuffer, 0, 16, _encryptedBlockBuffer, 0);
                }
                else
                {
                    _dataDecryptor.TransformBlock(_blockBuffer, 0, 16, _encryptedBlockBuffer, 0);
                }

                // 3. 再次XOR tweak
                for (int i = 0; i < 16; i++)
                {
                    output[pos + i] = (byte)(_encryptedBlockBuffer[i] ^ currentTweak[i]);
                }

                // 4. 为下一个块更新tweak (GF乘法)
                if (pos + 16 < _sectorSize)
                {
                    byte carry = 0;
                    for (int i = 15; i >= 0; i--)
                    {
                        byte b = currentTweak[i];
                        byte newByte = (byte)((b << 1) | carry);
                        carry = (byte)(b >> 7);
                        currentTweak[i] = newByte;
                    }
                    if (carry != 0)
                    {
                        currentTweak[15] ^= 0x87; // 模约简多项式
                    }
                }
            }
        }
        #endregion

        #region Stream Properties
        public override bool CanRead => _baseStream.CanRead;
        public override bool CanSeek => _baseStream.CanSeek;
        public override bool CanWrite => _baseStream.CanWrite;
        public override long Length => _baseStream.Length;

        public override long Position
        {
            get => _position;
            set => Seek(value, SeekOrigin.Begin);
        }
        #endregion

        #region Core Stream Methods
        public override void Flush() => _baseStream.Flush();

        public override int Read(byte[] buffer, int offset, int count)
        {
            ValidateDisposed();
            if (count == 0) return 0;

            int totalRead = 0;
            long currentPosition = _position;

            while (count > 0)
            {
                int sectorOffset = (int)(currentPosition % _sectorSize);
                long sectorIndex = currentPosition / _sectorSize;
                int bytesToProcess = Math.Min(count, _sectorSize - sectorOffset);

                // 读取整个扇区
                long sectorPos = sectorIndex * _sectorSize;
                int bytesRead = ReadSector(sectorPos, _sectorBuffer);
                if (bytesRead == 0) break;

                // 处理数据
                XtsTransform(_sectorBuffer, _sectorBuffer, sectorIndex, false);
                int copyLength = Math.Min(bytesToProcess, bytesRead - sectorOffset);

                Buffer.BlockCopy(
                    _sectorBuffer, sectorOffset,
                    buffer, offset + totalRead,
                    copyLength
                );

                totalRead += copyLength;
                count -= copyLength;
                currentPosition += copyLength;
            }

            _position = currentPosition;
            return totalRead;
        }

        private int ReadSector(long position, byte[] buffer)
        {
            lock (_baseStream)
            {
                if (_baseStream.Position != position)
                    _baseStream.Position = position;

                int totalRead = 0;
                while (totalRead < buffer.Length)
                {
                    int read = _baseStream.Read(buffer, totalRead, buffer.Length - totalRead);
                    if (read == 0) break;
                    totalRead += read;
                }
                return totalRead;
            }
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            ValidateDisposed();
            if (count == 0) return;

            long currentPosition = _position;

            while (count > 0)
            {
                int sectorOffset = (int)(currentPosition % _sectorSize);
                long sectorIndex = currentPosition / _sectorSize;
                int bytesToProcess = Math.Min(count, _sectorSize - sectorOffset);

                // 读取并解密现有扇区
                long sectorPos = sectorIndex * _sectorSize;
                bool fullSectorWrite = (sectorOffset == 0) && (bytesToProcess == _sectorSize);

                if (!fullSectorWrite && sectorPos < _baseStream.Length)
                {
                    ReadSector(sectorPos, _sectorBuffer);
                    XtsTransform(_sectorBuffer, _sectorBuffer, sectorIndex, false);
                }
                else
                {
                    // 新扇区或完整覆盖时不需要读取
                    Array.Clear(_sectorBuffer, 0, _sectorSize);
                }

                // 合并新数据
                Buffer.BlockCopy(
                    buffer, offset,
                    _sectorBuffer, sectorOffset,
                    bytesToProcess
                );

                // 加密并写回
                XtsTransform(_sectorBuffer, _sectorBuffer, sectorIndex, true);
                WriteSector(sectorPos, _sectorBuffer);

                offset += bytesToProcess;
                count -= bytesToProcess;
                currentPosition += bytesToProcess;
            }

            _position = currentPosition;
        }

        private void WriteSector(long position, byte[] data)
        {
            lock (_baseStream)
            {
                if (_baseStream.Position != position)
                    _baseStream.Position = position;

                _baseStream.Write(data, 0, data.Length);
            }
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            ValidateDisposed();
            long newPos = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                SeekOrigin.End => _baseStream.Length + offset,
                _ => throw new ArgumentException("Invalid seek origin", nameof(origin))
            };

            if (newPos < 0) throw new IOException("Attempt to seek before beginning of stream");
            return _position = newPos;
        }

        public override void SetLength(long value)
        {
            ValidateDisposed();
            long currentLen = _baseStream.Length;

            if (value > currentLen)
            {
                // 扩展文件
                long newSectors = (value - currentLen + _sectorSize - 1) / _sectorSize;
                long startSector = currentLen / _sectorSize;

                lock (_baseStream)
                {
                    _baseStream.SetLength(value);
                    _baseStream.Position = currentLen;

                    for (int i = 0; i < newSectors; i++)
                    {
                        RandomNumberGenerator.Fill(_sectorBuffer.AsSpan(0, _sectorSize));
                        XtsTransform(_sectorBuffer, _sectorBuffer, startSector + i, true);
                        _baseStream.Write(_sectorBuffer, 0, _sectorSize);
                    }
                }
            }
            else
            {
                _baseStream.SetLength(value);
                if (_position > value) _position = value;
            }
        }
        #endregion

        #region IDisposable
        protected override void Dispose(bool disposing)
        {
            if (_disposed) return;

            if (disposing)
            {
                _tweakEncryptor?.Dispose();
                _dataEncryptor?.Dispose();
                _dataDecryptor?.Dispose();
                _baseStream.Dispose();
            }

            _disposed = true;
            base.Dispose(disposing);
        }

        private void ValidateDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(GetType().Name);
        }
        #endregion
    }
}
