using CHDSharpLib.Utils;
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace CHDSharpLib;

internal class CHDHeader
{
    public chd_codec[] compression;

    public ulong totalbytes;
    public uint blocksize;
    public uint totalblocks;

    public mapentry[] map;

    public byte[] md5; // just compressed data
    public byte[] rawsha1; // just compressed data
    public byte[] sha1; // includes the meta data
    public ulong metaoffset;
}

internal class mapentry
{
    public compression_type comptype;
    public uint length; // length of compressed data
    public ulong offset; // offset of compressed data in file.
    public uint? crc = null; // V3 & V4
    public ushort? crc16 = null; // V5

    //Used to optimmize block reading so that any block in only decompressed once.
    public uint UseCount;

    public byte[] source = null;
    public byte[] BlockCache = null;
    public byte[] cache = null;

    public bool Procesed = false;
}


public static class CHD
{
    public static void TestCHD(string filename)
    {
        Console.WriteLine("");
        Console.WriteLine($"Testing :{filename}");
        using (Stream s = File.Open(filename, FileMode.Open, FileAccess.Read))
        {
            if (!CheckHeader(s, out uint length, out uint version))
                return;

            Console.WriteLine($@"CHD Version {version}");

            chd_error valid = chd_error.CHDERR_INVALID_DATA;
            CHDHeader chd;
            switch (version)
            {
                case 1:
                    valid = CHDHeaders.ReadHeaderV1(s, out chd);
                    break;
                case 2:
                    valid = CHDHeaders.ReadHeaderV2(s, out chd);
                    break;
                case 3:
                    valid = CHDHeaders.ReadHeaderV3(s, out chd);
                    break;
                case 4:
                    valid = CHDHeaders.ReadHeaderV4(s, out chd);
                    break;
                case 5:
                    valid = CHDHeaders.ReadHeaderV5(s, out chd);
                    break;
                default:
                    Console.WriteLine($"Unknown version {version}");
                    return;
            }
            if (valid != chd_error.CHDERR_NONE)
            {
                SendMessage($"Error Reading Header: {valid}", ConsoleColor.Red);
            }

            if (((ulong)chd.totalblocks * (ulong)chd.blocksize) != chd.totalbytes)
            {
                SendMessage($"{(ulong)chd.totalblocks * (ulong)chd.blocksize} != {chd.totalbytes}", ConsoleColor.Cyan);
            }

            CHDBlockRead.FindRepeatedBlocks(chd);

            valid = DecompressData(s, chd);
            if (valid != chd_error.CHDERR_NONE)
            {
                SendMessage($"Data Decompress Failed: {valid}", ConsoleColor.Red);
                return;
            }

            valid = CHDMetaData.ReadMetaData(s, chd);

            if (valid != chd_error.CHDERR_NONE)
            {
                SendMessage($"Meta Data Failed: {valid}", ConsoleColor.Red);
                return;
            }

            SendMessage($"Valid", ConsoleColor.Green);
        }
    }

    private static void SendMessage(string msg, ConsoleColor cc)
    {
        ConsoleColor consoleColor = Console.ForegroundColor;
        Console.ForegroundColor = cc;
        Console.WriteLine(msg);
        Console.ForegroundColor = consoleColor;
    }

    private static readonly uint[] HeaderLengths = new uint[] { 0, 76, 80, 120, 108, 124 };
    private static readonly byte[] id = { (byte)'M', (byte)'C', (byte)'o', (byte)'m', (byte)'p', (byte)'r', (byte)'H', (byte)'D' };

    public static bool CheckHeader(Stream file, out uint length, out uint version)
    {
        for (int i = 0; i < id.Length; i++)
        {
            byte b = (byte)file.ReadByte();
            if (b != id[i])
            {
                length = 0;
                version = 0;
                return false;
            }
        }

        using (BinaryReader br = new BinaryReader(file, Encoding.UTF8, true))
        {
            length = br.ReadUInt32BE();
            version = br.ReadUInt32BE();
            return HeaderLengths[version] == length;
        }
    }


    internal static chd_error DecompressData(Stream file, CHDHeader chd)
    {
        using BinaryReader br = new BinaryReader(file, Encoding.UTF8, true);

        using MD5 md5Check = chd.md5 != null ? MD5.Create() : null;
        using SHA1 sha1Check = chd.rawsha1 != null ? SHA1.Create() : null;

        byte[] buffer = new byte[chd.blocksize];

        int block = 0;
        ulong sizetoGo = chd.totalbytes;
        while (sizetoGo > 0)
        {
            /* progress */
            if ((block % 1000) == 0)
                Console.Write($"Verifying, {(100 - sizetoGo * 100 / chd.totalbytes):N1}% complete...\r");

            /* read the block into the cache */
            chd_error err = CHDBlockRead.ReadBlock(file, chd.compression, block, chd.map, (uint)chd.blocksize, ref buffer);
            if (err != chd_error.CHDERR_NONE)
                return err;

            int sizenext = sizetoGo > (ulong)chd.blocksize ? (int)chd.blocksize : (int)sizetoGo;

            md5Check?.TransformBlock(buffer, 0, sizenext, null, 0);
            sha1Check?.TransformBlock(buffer, 0, sizenext, null, 0);

            /* prepare for the next block */
            block++;
            sizetoGo -= (ulong)sizenext;

        }
        Console.WriteLine($"Verifying, 100.0% complete...");

        byte[] tmp = new byte[0];
        md5Check?.TransformFinalBlock(tmp, 0, 0);
        sha1Check?.TransformFinalBlock(tmp, 0, 0);

        // here it is now using the rawsha1 value from the header to validate the raw binary data.
        if (chd.md5 != null && !Util.ByteArrEquals(chd.md5, md5Check.Hash))
        {
            return chd_error.CHDERR_DECOMPRESSION_ERROR;
        }
        if (chd.rawsha1 != null && !Util.ByteArrEquals(chd.rawsha1, sha1Check.Hash))
        {
            return chd_error.CHDERR_DECOMPRESSION_ERROR;
        }

        return chd_error.CHDERR_NONE;
    }
}
