using CHDSharpLib.Utils;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace CHDSharpLib
{
    internal static class CHDBlockRead
    {
        // search for all COMPRESSION_SELF block, and increase the counter of the block it is referencing.
        // the first time the referenced block is decompressed a copy of its data is kept.
        // this copy is then used (instead of re-decompressing.) until the use count returns to zero
        // at which time the backup copy if removed.

        internal static void FindRepeatedBlocks(CHDHeader chd)
        {
            int totalFound = 0;

            Parallel.ForEach(chd.map, me =>
            {
                if (me.comptype != compression_type.COMPRESSION_SELF)
                    return;

                me.selfMapEntry = chd.map[me.offset];
                switch (me.selfMapEntry.comptype)
                {
                    case compression_type.COMPRESSION_TYPE_0:
                    case compression_type.COMPRESSION_TYPE_1:
                    case compression_type.COMPRESSION_TYPE_2:
                    case compression_type.COMPRESSION_TYPE_3:
                    case compression_type.COMPRESSION_NONE:
                        break;
                    default:
                        Console.WriteLine($"Error {me.selfMapEntry.comptype}");
                        break;
                }
                Interlocked.Increment(ref me.selfMapEntry.UseCount);
                Interlocked.Increment(ref totalFound);
            });

            Console.WriteLine($"Total Blocks {chd.map.Length}, Repeat Blocks {totalFound}");
        }

        internal static chd_error ReadBlock(mapentry mapentry, chd_codec[] compression, CHDCodec codec, ref byte[] buffOut)
        {
            bool checkCrc = true;
            uint blockSize = (uint)buffOut.Length;

            switch (mapentry.comptype)
            {
                case compression_type.COMPRESSION_TYPE_0:
                case compression_type.COMPRESSION_TYPE_1:
                case compression_type.COMPRESSION_TYPE_2:
                case compression_type.COMPRESSION_TYPE_3:
                    {
                        lock (mapentry)
                        {
                            if (mapentry.buffOutCache == null)
                            { 
                                chd_error ret = chd_error.CHDERR_UNSUPPORTED_FORMAT;
                                switch (compression[(int)mapentry.comptype])
                                {
                                    case chd_codec.CHD_CODEC_ZLIB:
                                        ret = CHDReaders.zlib(mapentry.buffIn, buffOut);
                                        break;
                                    case chd_codec.CHD_CODEC_LZMA:
                                        ret = CHDReaders.lzma(mapentry.buffIn, buffOut);
                                        break;
                                    case chd_codec.CHD_CODEC_HUFFMAN:
                                        ret = CHDReaders.huffman(mapentry.buffIn, buffOut);
                                        break;
                                    case chd_codec.CHD_CODEC_FLAC:
                                        ret = CHDReaders.flac(mapentry.buffIn, buffOut, codec);
                                        break;
                                    case chd_codec.CHD_CODEC_CD_ZLIB:
                                        ret = CHDReaders.cdzlib(mapentry.buffIn, buffOut);
                                        break;
                                    case chd_codec.CHD_CODEC_CD_LZMA:
                                        ret = CHDReaders.cdlzma(mapentry.buffIn, buffOut);
                                        break;
                                    case chd_codec.CHD_CODEC_CD_FLAC:
                                        ret = CHDReaders.cdflac(mapentry.buffIn, buffOut, codec);
                                        break;
                                    case chd_codec.CHD_CODEC_AVHUFF:
                                        ret = CHDReaders.avHuff(mapentry.buffIn, buffOut, codec);
                                        break;
                                    default:
                                        Console.WriteLine("Unknown compression type");
                                        break;
                                }
                                mapentry.buffIn = null;

                                if (ret != chd_error.CHDERR_NONE)
                                    return ret;

                                // if this block is re-used keep a copy of it.
                                if (mapentry.UseCount > 0)
                                {
                                    mapentry.buffOutCache = new byte[blockSize];
                                    Array.Copy(buffOut, 0, mapentry.buffOutCache, 0, blockSize);
                                }

                                break;
                            }
                        }

                        Array.Copy(mapentry.buffOutCache, 0, buffOut, 0, (int)blockSize);
                        Interlocked.Decrement(ref mapentry.UseCount);
                        if (mapentry.UseCount == 0)
                            mapentry.buffOutCache = null;

                        checkCrc = false;
                        break;
                    }
                case compression_type.COMPRESSION_NONE:
                    {
                        lock (mapentry)
                        {
                            if (mapentry.buffOutCache == null)
                            {
                                buffOut = mapentry.buffIn;
                                mapentry.buffIn = null;                                

                                if (mapentry.UseCount > 0)
                                {
                                    mapentry.buffOutCache = new byte[blockSize];
                                    Array.Copy(buffOut, 0, mapentry.buffOutCache, 0, blockSize);
                                }
                                break;
                            }
                        }


                        Array.Copy(mapentry.buffOutCache, 0, buffOut, 0, (int)blockSize);
                        Interlocked.Decrement(ref mapentry.UseCount);
                        if (mapentry.UseCount == 0)
                            mapentry.buffOutCache = null;

                        checkCrc = false;
                        break;
                    }

                case compression_type.COMPRESSION_MINI:
                    {
                        byte[] tmp = BitConverter.GetBytes(mapentry.offset);
                        for (int i = 0; i < 8; i++)
                        {
                            buffOut[i] = tmp[7 - i];
                        }

                        for (int i = 8; i < blockSize; i++)
                        {
                            buffOut[i] = buffOut[i - 8];
                        }

                        break;
                    }

                case compression_type.COMPRESSION_SELF:
                    {
                        // should never hit here:
                        chd_error retcs = ReadBlock(mapentry.selfMapEntry, compression, codec, ref buffOut);
                        if (retcs != chd_error.CHDERR_NONE)
                            return retcs;
                        // check CRC in the read_block_into_cache call
                        checkCrc = false;
                        break;
                    }
                default:
                    return chd_error.CHDERR_DECOMPRESSION_ERROR;

            }

            if (checkCrc)
            {
                if (mapentry.crc != null && !CRC.VerifyDigest((uint)mapentry.crc, buffOut, 0, blockSize))
                    return chd_error.CHDERR_DECOMPRESSION_ERROR;
                if (mapentry.crc16 != null && CRC16.calc(buffOut, (int)blockSize) != mapentry.crc16)
                    return chd_error.CHDERR_DECOMPRESSION_ERROR;
            }
            return chd_error.CHDERR_NONE;
        }

    }
}
