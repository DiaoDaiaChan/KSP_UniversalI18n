using System;
using System.Globalization;

namespace UI18n.Core.Update
{
    /// <summary>
    /// RFC 1951（raw DEFLATE）解压器，puff 式两级表解码。
    /// <para>
    /// 为什么要自己写：KSP 1.12 的托管环境里没有 System.IO.Compression
    /// （KSP_x64_Data/Managed 只有 90 个 dll，没有它，也没有 System.Net.*），
    /// 而 net472 目标又禁止拉第三方包（例如包里躺着的 Ionic.Zip）。若直接引用
    /// System.IO.Compression，玩家一旦走到更新路径就会 FileNotFoundException，
    /// 那种异常在 Harmony 之外是整局炸掉。这里用纯 BCL 实现，两个 TFM 同一份源码。
    /// </para>
    /// <para>
    /// 安全：输出上限由调用方给（防 zip 炸弹），距离越界、位流截断、块类型非法
    /// 一律返回 false + 原因串，绝不抛异常、绝不写出超过上限的内存。
    /// </para>
    /// </summary>
    public static class Inflater
    {
        /// <summary>默认输出上限（64 MB）：包本身只有 1 MB 量级，这个上限只是防炸弹。</summary>
        public const int DefaultMaxOutputBytes = 64 * 1024 * 1024;

        private static readonly int[] LengthBase =
        {
            3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 23, 27, 31,
            35, 43, 51, 59, 67, 83, 99, 115, 131, 163, 195, 227, 258
        };

        private static readonly int[] LengthExtra =
        {
            0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2,
            3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0
        };

        private static readonly int[] DistanceBase =
        {
            1, 2, 3, 4, 5, 7, 9, 13, 17, 25, 33, 49, 65, 97, 129, 193,
            257, 385, 513, 769, 1025, 1537, 2049, 3073, 4097, 6145, 8193, 12289, 16385, 24577
        };

        private static readonly int[] DistanceExtra =
        {
            0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6,
            7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13
        };

        private static readonly int[] CodeLengthOrder =
        {
            16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15
        };

        /// <summary>解压（输出上限用 DefaultMaxOutputBytes）。</summary>
        public static bool TryInflate(byte[] input, int offset, int length, out byte[] output, out string error)
        {
            return TryInflate(input, offset, length, DefaultMaxOutputBytes, out output, out error);
        }

        /// <summary>
        /// 解压一段 raw deflate 数据。
        /// </summary>
        /// <param name="input">输入缓冲。</param>
        /// <param name="offset">起偏移。</param>
        /// <param name="length">字节数。</param>
        /// <param name="maxOutputBytes">输出上限（超过即失败，防 zip 炸弹）。</param>
        /// <param name="output">解压结果（失败时为 null）。</param>
        /// <param name="error">失败原因。</param>
        public static bool TryInflate(byte[] input, int offset, int length, int maxOutputBytes, out byte[] output, out string error)
        {
            output = null;
            error = null;

            if (input == null)
            {
                error = "null-input";
                return false;
            }

            if (offset < 0 || length < 0 || offset + length > input.Length)
            {
                error = "range-out-of-bounds";
                return false;
            }

            if (maxOutputBytes <= 0)
            {
                maxOutputBytes = DefaultMaxOutputBytes;
            }

            Decoder decoder = new Decoder(input, offset, length, maxOutputBytes);
            if (!decoder.Run())
            {
                error = decoder.Error;
                return false;
            }

            output = decoder.ToArray();
            return true;
        }

        /// <summary>Huffman 表（counts 按位长、symbol 按码字序）。</summary>
        private sealed class Huffman
        {
            internal readonly short[] Count = new short[16];
            internal short[] Symbol = new short[0];

            internal bool Build(short[] lengths, int n)
            {
                for (int i = 0; i < 16; i++)
                {
                    Count[i] = 0;
                }

                for (int i = 0; i < n; i++)
                {
                    Count[lengths[i]]++;
                }

                if (Count[0] == n)
                {
                    // 整张表都是 0 长度：没有任何符号可用（合法，例如 HDIST=1 且为 0）
                    Symbol = new short[0];
                    return true;
                }

                int left = 1;
                for (int len = 1; len < 16; len++)
                {
                    left <<= 1;
                    left -= Count[len];
                    if (left < 0)
                    {
                        return false; // 过度订阅
                    }
                }

                short[] offsets = new short[16];
                for (int len = 1; len < 15; len++)
                {
                    offsets[len + 1] = (short)(offsets[len] + Count[len]);
                }

                Symbol = new short[n];
                for (int i = 0; i < n; i++)
                {
                    if (lengths[i] != 0)
                    {
                        Symbol[offsets[lengths[i]]++] = (short)i;
                    }
                }

                return true;
            }
        }

        private sealed class Decoder
        {
            private const int FixedLitCount = 288;
            private const int FixedDistCount = 30;

            private readonly byte[] _in;
            private readonly int _end;
            private readonly int _maxOutput;
            private int _pos;
            private int _bitBuf;
            private int _bitCnt;

            private byte[] _out;
            private int _outLen;

            private readonly Huffman _fixedLit = new Huffman();
            private readonly Huffman _fixedDist = new Huffman();
            private readonly Huffman _lit = new Huffman();
            private readonly Huffman _dist = new Huffman();
            private readonly Huffman _codeLen = new Huffman();

            internal Decoder(byte[] input, int offset, int length, int maxOutputBytes)
            {
                _in = input;
                _pos = offset;
                _end = offset + length;
                _maxOutput = maxOutputBytes;
                _out = new byte[Math.Min(maxOutputBytes, 64 * 1024)];
                _outLen = 0;
                BuildFixedTables();
            }

            internal string Error { get; private set; }

            internal byte[] ToArray()
            {
                byte[] result = new byte[_outLen];
                Buffer.BlockCopy(_out, 0, result, 0, _outLen);
                return result;
            }

            private void BuildFixedTables()
            {
                short[] litLengths = new short[FixedLitCount];
                for (int i = 0; i < 144; i++)
                {
                    litLengths[i] = 8;
                }

                for (int i = 144; i < 256; i++)
                {
                    litLengths[i] = 9;
                }

                for (int i = 256; i < 280; i++)
                {
                    litLengths[i] = 7;
                }

                for (int i = 280; i < FixedLitCount; i++)
                {
                    litLengths[i] = 8;
                }

                short[] distLengths = new short[FixedDistCount];
                for (int i = 0; i < FixedDistCount; i++)
                {
                    distLengths[i] = 5;
                }

                _fixedLit.Build(litLengths, FixedLitCount);
                _fixedDist.Build(distLengths, FixedDistCount);
            }

            internal bool Run()
            {
                bool last = false;
                while (!last)
                {
                    int final;
                    int type;
                    if (!TryBits(1, out final))
                    {
                        return Fail("truncated-block-header");
                    }

                    if (!TryBits(2, out type))
                    {
                        return Fail("truncated-block-header");
                    }

                    last = final == 1;

                    if (type == 0)
                    {
                        if (!Stored())
                        {
                            return false;
                        }
                    }
                    else if (type == 1)
                    {
                        if (!Codes(_fixedLit, _fixedDist))
                        {
                            return false;
                        }
                    }
                    else if (type == 2)
                    {
                        if (!Dynamic())
                        {
                            return false;
                        }
                    }
                    else
                    {
                        return Fail("bad-block-type");
                    }
                }

                return true;
            }

            private bool Stored()
            {
                AlignToByte();

                if (_pos + 4 > _end)
                {
                    return Fail("truncated-stored-header");
                }

                int len = _in[_pos] | (_in[_pos + 1] << 8);
                int nlen = _in[_pos + 2] | (_in[_pos + 3] << 8);
                _pos += 4;

                if ((len ^ 0xFFFF) != nlen)
                {
                    return Fail("stored-length-mismatch");
                }

                if (_pos + len > _end)
                {
                    return Fail("truncated-stored-data");
                }

                if (_outLen + len > _maxOutput)
                {
                    return Fail("output-too-large");
                }

                EnsureCapacity(_outLen + len);
                Buffer.BlockCopy(_in, _pos, _out, _outLen, len);
                _pos += len;
                _outLen += len;
                return true;
            }

            private bool Dynamic()
            {
                int hlit;
                int hdist;
                int hclen;
                if (!TryBits(5, out hlit) || !TryBits(5, out hdist) || !TryBits(4, out hclen))
                {
                    return Fail("truncated-dynamic-header");
                }

                int litCount = hlit + 257;
                int distCount = hdist + 1;
                int codeCount = hclen + 4;

                if (litCount > 286 || distCount > 30)
                {
                    return Fail("bad-huffman-count");
                }

                short[] codeLengths = new short[19];
                for (int i = 0; i < 19; i++)
                {
                    codeLengths[i] = 0;
                }

                for (int i = 0; i < codeCount; i++)
                {
                    int bits;
                    if (!TryBits(3, out bits))
                    {
                        return Fail("truncated-code-lengths");
                    }

                    codeLengths[CodeLengthOrder[i]] = (short)bits;
                }

                if (!_codeLen.Build(codeLengths, 19))
                {
                    return Fail("bad-code-length-table");
                }

                short[] lengths = new short[litCount + distCount];
                int index = 0;
                while (index < lengths.Length)
                {
                    int symbol;
                    if (!Decode(_codeLen, out symbol))
                    {
                        return Fail("bad-code-length-symbol");
                    }

                    if (symbol < 16)
                    {
                        lengths[index++] = (short)symbol;
                        continue;
                    }

                    int repeat;
                    int value = 0;
                    if (symbol == 16)
                    {
                        if (index == 0)
                        {
                            return Fail("repeat-without-previous");
                        }

                        value = lengths[index - 1];
                        if (!TryBits(2, out repeat))
                        {
                            return Fail("truncated-repeat");
                        }

                        repeat += 3;
                    }
                    else if (symbol == 17)
                    {
                        if (!TryBits(3, out repeat))
                        {
                            return Fail("truncated-repeat");
                        }

                        repeat += 3;
                    }
                    else if (symbol == 18)
                    {
                        if (!TryBits(7, out repeat))
                        {
                            return Fail("truncated-repeat");
                        }

                        repeat += 11;
                    }
                    else
                    {
                        return Fail("bad-code-length-symbol");
                    }

                    if (index + repeat > lengths.Length)
                    {
                        return Fail("too-many-code-lengths");
                    }

                    for (int i = 0; i < repeat; i++)
                    {
                        lengths[index++] = (short)value;
                    }
                }

                short[] litLengths = new short[litCount];
                for (int i = 0; i < litCount; i++)
                {
                    litLengths[i] = lengths[i];
                }

                short[] distLengths = new short[distCount];
                for (int i = 0; i < distCount; i++)
                {
                    distLengths[i] = lengths[litCount + i];
                }

                if (litLengths[256] == 0)
                {
                    return Fail("missing-end-of-block");
                }

                if (!_lit.Build(litLengths, litCount))
                {
                    return Fail("bad-literal-table");
                }

                if (!_dist.Build(distLengths, distCount))
                {
                    return Fail("bad-distance-table");
                }

                return Codes(_lit, _dist);
            }

            private bool Codes(Huffman lit, Huffman dist)
            {
                while (true)
                {
                    int symbol;
                    if (!Decode(lit, out symbol))
                    {
                        return Fail("bad-literal-symbol");
                    }

                    if (symbol < 256)
                    {
                        if (_outLen + 1 > _maxOutput)
                        {
                            return Fail("output-too-large");
                        }

                        EnsureCapacity(_outLen + 1);
                        _out[_outLen++] = (byte)symbol;
                        continue;
                    }

                    if (symbol == 256)
                    {
                        return true;
                    }

                    symbol -= 257;
                    if (symbol >= 29)
                    {
                        return Fail("bad-length-symbol");
                    }

                    int extra;
                    if (!TryBits(LengthExtra[symbol], out extra))
                    {
                        return Fail("truncated-length-extra");
                    }

                    int copyLength = LengthBase[symbol] + extra;

                    int distSymbol;
                    if (!Decode(dist, out distSymbol))
                    {
                        return Fail("bad-distance-symbol");
                    }

                    if (distSymbol >= 30)
                    {
                        return Fail("bad-distance-symbol");
                    }

                    if (!TryBits(DistanceExtra[distSymbol], out extra))
                    {
                        return Fail("truncated-distance-extra");
                    }

                    int distance = DistanceBase[distSymbol] + extra;
                    if (distance > _outLen)
                    {
                        return Fail("distance-too-far");
                    }

                    if (_outLen + copyLength > _maxOutput)
                    {
                        return Fail("output-too-large");
                    }

                    EnsureCapacity(_outLen + copyLength);
                    int from = _outLen - distance;
                    for (int i = 0; i < copyLength; i++)
                    {
                        _out[_outLen++] = _out[from + i];
                    }
                }
            }

            private bool Decode(Huffman table, out int symbol)
            {
                symbol = -1;

                int code = 0;
                int first = 0;
                int index = 0;
                for (int len = 1; len < 16; len++)
                {
                    int bit;
                    if (!TryBits(1, out bit))
                    {
                        return false;
                    }

                    code |= bit;
                    int count = table.Count[len];
                    if (code - count < first)
                    {
                        symbol = table.Symbol[index + (code - first)];
                        return true;
                    }

                    index += count;
                    first += count;
                    first <<= 1;
                    code <<= 1;
                }

                return false;
            }

            private bool TryBits(int need, out int value)
            {
                value = 0;
                if (need == 0)
                {
                    return true;
                }

                long val = _bitBuf;
                while (_bitCnt < need)
                {
                    if (_pos >= _end)
                    {
                        return false;
                    }

                    val |= (long)_in[_pos++] << _bitCnt;
                    _bitCnt += 8;
                }

                value = (int)(val & ((1L << need) - 1));
                _bitBuf = (int)(val >> need);
                _bitCnt -= need;
                return true;
            }

            private void AlignToByte()
            {
                while (_bitCnt >= 8)
                {
                    _bitCnt -= 8;
                    _bitBuf >>= 8;
                    _pos--;
                }

                _bitBuf = 0;
                _bitCnt = 0;
            }

            private void EnsureCapacity(int needed)
            {
                if (needed <= _out.Length)
                {
                    return;
                }

                int capacity = _out.Length == 0 ? 1024 : _out.Length;
                while (capacity < needed)
                {
                    capacity = capacity * 2;
                    if (capacity > _maxOutput)
                    {
                        capacity = _maxOutput;
                    }

                    if (capacity <= 0)
                    {
                        capacity = needed;
                        break;
                    }

                    if (capacity == _maxOutput && capacity < needed)
                    {
                        capacity = needed;
                        break;
                    }
                }

                byte[] grown = new byte[capacity];
                Buffer.BlockCopy(_out, 0, grown, 0, _outLen);
                _out = grown;
            }

            private bool Fail(string message)
            {
                if (Error == null)
                {
                    Error = message + " at " + _pos.ToString(CultureInfo.InvariantCulture);
                }

                return false;
            }
        }
    }
}
