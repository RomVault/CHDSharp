using CHDSharpLib.Utils;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CHDSharpLib;

internal class CHDHeader
{
    public chd_codec[] compression;
    public CHDReader[] chdReader;

    public ulong totalbytes;
    public uint blocksize;
    public uint totalblocks;

    public mapentry[] map;

    public byte[] md5; // just compressed data
    public byte[] rawsha1; // just compressed data
    public byte[] sha1; // includes the meta data

    public byte[] parentmd5;
    public byte[] parentsha1;

    public ulong metaoffset;
}

internal class mapentry
{
    public compression_type comptype;
    public uint length; // length of compressed data
    public ulong offset; // offset of compressed data in file. Also index of source block for COMPRESSION_SELF 
    public uint? crc = null; // V3 & V4
    public ushort? crc16 = null; // V5

    public mapentry selfMapEntry; // link to self mapentry data used in COMPRESSION_SELF (replaces offset index)

    //Used to optimmize block reading so that any block in only decompressed once.
    public int UseCount;

    public byte[] buffIn = null;
    public byte[] buffOutCache = null;
    public byte[] buffOut = null;

    // Used in Parallel decompress to keep the blocks in order when hashing.
    public bool Processed = false;

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

            if (!Util.IsAllZeroArray(chd.parentmd5) || !Util.IsAllZeroArray(chd.parentsha1))
            {
                SendMessage($"Child CHD found, cannot be processed", ConsoleColor.DarkGreen);
                return;
            }

            if (((ulong)chd.totalblocks * (ulong)chd.blocksize) != chd.totalbytes)
            {
                SendMessage($"{(ulong)chd.totalblocks * (ulong)chd.blocksize} != {chd.totalbytes}", ConsoleColor.Cyan);
            }

            CHDBlockRead.FindRepeatedBlocks(chd);
            CHDBlockRead.FindBlockReaders(chd);

            valid = DecompressDataParallel(s, chd);
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
        // stores the FLAC decompression classes for this instance.
        CHDCodec codec = new CHDCodec();

        using BinaryReader br = new BinaryReader(file, Encoding.UTF8, true);

        using MD5 md5Check = chd.md5 != null ? MD5.Create() : null;
        using SHA1 sha1Check = chd.rawsha1 != null ? SHA1.Create() : null;

        ArrayPool arrPool = new ArrayPool(chd.blocksize);

        byte[] buffer = new byte[chd.blocksize];

        int block = 0;
        ulong sizetoGo = chd.totalbytes;
        while (sizetoGo > 0)
        {
            /* progress */
            if ((block % 1000) == 0)
                Console.Write($"Verifying, {(100 - sizetoGo * 100 / chd.totalbytes):N1}% complete...\r");

            mapentry mapEntry = chd.map[block];
            if (mapEntry.length > 0)
            {
                mapEntry.buffIn = arrPool.Rent(); 
                file.Seek((long)mapEntry.offset, SeekOrigin.Begin);
                file.Read(mapEntry.buffIn, 0, (int)mapEntry.length);
            }

            /* read the block into the cache */
            chd_error err = CHDBlockRead.ReadBlock(mapEntry, arrPool, chd.chdReader, codec, buffer, (int)chd.blocksize);
            if (err != chd_error.CHDERR_NONE)
                return err;

            arrPool.Return(mapEntry.buffIn);
            mapEntry.buffIn = null;

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


    public static int taskCounter = 4;

    internal static chd_error DecompressDataParallel(Stream file, CHDHeader chd)
    {
        using BinaryReader br = new BinaryReader(file, Encoding.UTF8, true);

        using MD5 md5Check = chd.md5 != null ? MD5.Create() : null;
        using SHA1 sha1Check = chd.rawsha1 != null ? SHA1.Create() : null;

        int taskCount = taskCounter;
        using BlockingCollection<int> blocksToDecompress = new BlockingCollection<int>(boundedCapacity: taskCount * 5);
        using BlockingCollection<int> blocksToHash = new BlockingCollection<int>(boundedCapacity: taskCount * 5);
        chd_error errMaster = chd_error.CHDERR_NONE;

        List<Task> allTasks = new List<Task>();

        var ts = new CancellationTokenSource();
        CancellationToken ct = ts.Token;

        ArrayPool arrPool = new ArrayPool(chd.blocksize);

        Task producerThread = Task.Factory.StartNew(() =>
        {
            try
            {
                uint blockPercent = chd.totalblocks / 100;
                if (blockPercent == 0)
                    blockPercent = 1;

                for (int block = 0; block < chd.totalblocks; block++)
                {
                    if (ct.IsCancellationRequested)
                        break;

                    /* progress */
                    if ((block % blockPercent) == 0)
                        Console.Write($"Verifying: {(long)block * 100 / chd.totalblocks:N0}%     Load buffer: {blocksToDecompress.Count}   Hash buffer: {blocksToHash.Count}   \r");

                    mapentry mapentry = chd.map[block];

                    if (mapentry.length > 0)
                    {
                        file.Seek((long)mapentry.offset, SeekOrigin.Begin);
                        mapentry.buffIn = arrPool.Rent();
                        file.Read(mapentry.buffIn, 0, (int)mapentry.length);
                    }

                    blocksToDecompress.Add(block, ct);
                }
                // this must be done to tell all the decompression threads to stop working and return.
                for (int i = 0; i < taskCount; i++)
                    blocksToDecompress.Add(-1);

            }
            catch (Exception e)
            {
                if (ct.IsCancellationRequested)
                    return;

                if (errMaster == chd_error.CHDERR_NONE)
                    errMaster = chd_error.CHDERR_INVALID_FILE;
                ts.Cancel();
            }
        });
        allTasks.Add(producerThread);




        for (int i = 0; i < taskCount; i++)
        {
            Task decompressionThread = Task.Factory.StartNew(() =>
            {
                try
                {
                    CHDCodec codec = new CHDCodec();
                    while (true)
                    {
                        int block = blocksToDecompress.Take(ct);
                        if (block == -1)
                            return;

                        mapentry mapentry = chd.map[block];
                        mapentry.buffOut = arrPool.Rent();
                        chd_error err = CHDBlockRead.ReadBlock(mapentry, arrPool, chd.chdReader, codec, mapentry.buffOut, (int)chd.blocksize);
                        if (err != chd_error.CHDERR_NONE)
                        {
                            ts.Cancel();
                            errMaster = err;
                            return;
                        }
                        blocksToHash.Add(block);

                        if (mapentry.length > 0)
                        {
                            arrPool.Return(mapentry.buffIn);
                            mapentry.buffIn = null;
                        }
                    }
                }
                catch (Exception e)
                {
                    if (ct.IsCancellationRequested)
                        return;
                    if (errMaster == chd_error.CHDERR_NONE)
                        errMaster = chd_error.CHDERR_DECOMPRESSION_ERROR;
                    ts.Cancel();
                }
            });

            allTasks.Add(decompressionThread);

        }

        ulong sizetoGo = chd.totalbytes;
        int proc = 0;
        Task hashingThread = Task.Factory.StartNew(() =>
        {
            try
            {
                while (true)
                {
                    int item = blocksToHash.Take(ct);

                    chd.map[item].Processed = true;
                    while (chd.map[proc].Processed == true)
                    {
                        int sizenext = sizetoGo > (ulong)chd.blocksize ? (int)chd.blocksize : (int)sizetoGo;

                        mapentry mapentry = chd.map[proc];

                        md5Check?.TransformBlock(mapentry.buffOut, 0, sizenext, null, 0);
                        sha1Check?.TransformBlock(mapentry.buffOut, 0, sizenext, null, 0);

                        arrPool.Return(mapentry.buffOut);
                        mapentry.buffOut = null;

                        /* prepare for the next block */
                        sizetoGo -= (ulong)sizenext;

                        proc++;
                        if (proc == chd.totalblocks)
                            return;
                    }
                }
            }
            catch
            {
                if (ct.IsCancellationRequested)
                    return;

                if (errMaster == chd_error.CHDERR_NONE)
                    errMaster = chd_error.CHDERR_DECOMPRESSION_ERROR;
                ts.Cancel();
            }

        });
        allTasks.Add(hashingThread);

        Task.WaitAll(allTasks.ToArray());


        Console.WriteLine($"Verifying, 100% complete.");

        if (errMaster != chd_error.CHDERR_NONE)
            return errMaster;

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
