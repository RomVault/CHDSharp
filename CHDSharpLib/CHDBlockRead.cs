using CHDSharpLib.Utils;
using System;
using System.IO;

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
            for (int i = 0; i < chd.map.Length; i++)
            {
                if (chd.map[i].comptype != compression_type.COMPRESSION_SELF)
                    continue;

                ulong sourceBlock = chd.map[i].offset;
                switch (chd.map[sourceBlock].comptype)
                {
                    case compression_type.COMPRESSION_TYPE_0:
                    case compression_type.COMPRESSION_TYPE_1:
                    case compression_type.COMPRESSION_TYPE_2:
                    case compression_type.COMPRESSION_TYPE_3:
                    case compression_type.COMPRESSION_NONE:
                        break;
                    default:
                        Console.WriteLine($"Error {chd.map[sourceBlock].comptype}");
                        break;
                }
                chd.map[sourceBlock].UseCount += 1;
                totalFound++;
            }
            Console.WriteLine($"Total Blocks {chd.map.Length}, Repeat Blocks {totalFound}");
        }

        internal static chd_error ReadBlock(Stream file, chd_codec[] compression, int mapindex, mapentry[] map, uint blockSize, ref byte[] cache)
        {
            bool checkCrc = true;
            long blockoffs;
            mapentry mapentry = map[mapindex];

            if (compression[0] == chd_codec.CHD_CODEC_NONE)
            {
                blockoffs = (long)mapentry.offset * blockSize;
                if (blockoffs != 0)
                {
                    file.Seek((long)blockoffs, SeekOrigin.Begin);
                    file.Read(cache, 0, (int)blockSize);
                }
                else
                {
                    for (int j = 0; j < blockSize; j++)
                        cache[j] = 0;
                }
                return chd_error.CHDERR_NONE;
            }

            switch (mapentry.comptype)
            {
                case compression_type.COMPRESSION_TYPE_0:
                case compression_type.COMPRESSION_TYPE_1:
                case compression_type.COMPRESSION_TYPE_2:
                case compression_type.COMPRESSION_TYPE_3:

                    if (mapentry.BlockCache != null)
                    {
                        Console.WriteLine("Error");
                    }
                    file.Seek((long)mapentry.offset, SeekOrigin.Begin);
                    byte[] source = new byte[mapentry.length];
                    file.Read(source, 0, source.Length);

                    chd_error ret = chd_error.CHDERR_UNSUPPORTED_FORMAT;
                    switch (compression[(int)mapentry.comptype])
                    {
                        case chd_codec.CHD_CODEC_ZLIB:
                            ret = CHDReaders.zlib(source, cache);
                            break;
                        case chd_codec.CHD_CODEC_LZMA:
                            ret = CHDReaders.lzma(source, cache);
                            break;
                        case chd_codec.CHD_CODEC_HUFFMAN:
                            ret = CHDReaders.huffman(source, cache);
                            break;
                        case chd_codec.CHD_CODEC_FLAC:
                            ret = CHDReaders.flac(source, cache);
                            break;
                        case chd_codec.CHD_CODEC_CD_ZLIB:
                            ret = CHDReaders.cdzlib(source, cache);
                            break;
                        case chd_codec.CHD_CODEC_CD_LZMA:
                            ret = CHDReaders.cdlzma(source, cache);
                            break;
                        case chd_codec.CHD_CODEC_CD_FLAC:
                            ret = CHDReaders.cdflac(source, cache);
                            break;
                        case chd_codec.CHD_CODEC_AVHUFF:
                            ret = CHDReaders.avHuff(source, cache);
                            break;
                        default:
                            Console.WriteLine("Unknown compression type");
                            break;
                    }
                    if (ret != chd_error.CHDERR_NONE)
                        return ret;

                    // if this block is re-used keep a copy of it.
                    if (mapentry.UseCount > 0)
                    {
                        mapentry.BlockCache = new byte[blockSize];
                        Array.Copy(cache, 0, mapentry.BlockCache, 0, blockSize);
                    }

                    break;
                case compression_type.COMPRESSION_NONE:
                    {
                        if (mapentry.BlockCache != null)
                        {
                            Console.WriteLine("Error");
                        }

                        file.Seek((long)mapentry.offset, SeekOrigin.Begin);
                        if (mapentry.length != blockSize)
                            return chd_error.CHDERR_DECOMPRESSION_ERROR;

                        int bytes = file.Read(cache, 0, (int)blockSize);
                        if (bytes != (int)blockSize)
                            return chd_error.CHDERR_READ_ERROR;

                        if (mapentry.UseCount > 0)
                        {
                            mapentry.BlockCache = new byte[blockSize];
                            Array.Copy(cache, 0, mapentry.BlockCache, 0, blockSize);
                        }

                        break;
                    }

                case compression_type.COMPRESSION_MINI:
                    {
                        byte[] tmp = BitConverter.GetBytes(mapentry.offset);
                        for (int i = 0; i < 8; i++)
                        {
                            cache[i] = tmp[7 - i];
                        }

                        for (int i = 8; i < blockSize; i++)
                        {
                            cache[i] = cache[i - 8];
                        }

                        break;
                    }

                case compression_type.COMPRESSION_SELF:
                    {
                        // we should have kept a copy of this block, if we knew it was going to be used again.
                        int sourceIndex = (int)mapentry.offset;
                        if (map[sourceIndex].BlockCache != null)
                        {
                            Array.Copy(map[sourceIndex].BlockCache, 0, cache, 0, (int)blockSize);

                            map[sourceIndex].UseCount--;
                            if (map[sourceIndex].UseCount == 0)
                                map[sourceIndex].BlockCache = null;

                            checkCrc = false;
                            break;
                        }
                        // should never hit here:
                        chd_error retcs = ReadBlock(file, compression, (int)mapentry.offset, map, blockSize, ref cache);
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
                if (mapentry.crc != null && !CRC.VerifyDigest((uint)mapentry.crc, cache, 0, blockSize))
                    return chd_error.CHDERR_DECOMPRESSION_ERROR;
                if (mapentry.crc16 != null && CRC16.calc(cache, (int)blockSize) != mapentry.crc16)
                    return chd_error.CHDERR_DECOMPRESSION_ERROR;
            }
            return chd_error.CHDERR_NONE;
        }

    }
}
