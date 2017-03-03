/* The Computer Language Benchmarks Game
   http://benchmarksgame.alioth.debian.org/
 *
 * submitted by Dan Shechter
 *
 */

using System;
using System.IO;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;

public class Program
{
    public unsafe static void Main(string[] args)
    {
        PrepareLookups();
        var buffer = GetBytesForThirdSequence(args);
        var l = buffer.Length;
        var p = (byte *) GCHandle.Alloc(buffer, GCHandleType.Pinned).AddrOfPinnedObject().ToPointer();
        var procs = Environment.ProcessorCount / 2;

        var bufferLengths = Enumerable.Repeat(l / procs, procs).ToArray();
        bufferLengths[procs - 1] += l % procs;
        var bufferOffsets = Enumerable.Range(0, procs).Select(i => i * (l / procs)).ToArray();

        int GetBufferOffset(int c, int fl)
        {
            if (c == 0)
                return bufferOffsets[c];
            return bufferOffsets[c] - (fl - 1);
        }

        //var fragmentLengths = new[] { 1 };
        var prm =
        (
            from chunk in Enumerable.Range(0, procs)
            from fl in new[] { 1, 2, 3, 4, 6, 12, 18 }
            select new {
                O = GetBufferOffset(chunk, fl),
                L = bufferLengths[chunk],
                FL = fl
            }).ToArray();

        var res =
        (from param in prm.AsParallel()
            select new
            {
                param.FL,
                D = CountFrequency(p + param.O, param.L, param.FL),
            }).ToArray();

        var x1 = (from r in res
            group r by r.FL
            into g
            select new
            {
                FL = g.Key,
                SD = g.Select(x => x.D).ToArray(),
            }).ToArray();

        //var x2 = x1.OrderBy(x => x.FL);
        var dicts = (from x in x1.AsParallel()
                     select MergeDictionaries(x.SD)).ToArray();

        int buflen = dicts[0].Values.Sum();
        WriteFrequencies(dicts[0], buflen, 1);
        WriteFrequencies(dicts[1], buflen, 2);
        WriteCount(dicts[2], "GGT");
        WriteCount(dicts[3], "GGTA");
        WriteCount(dicts[4], "GGTATT");
        WriteCount(dicts[5], "GGTATTTTAATT");
        WriteCount(dicts[6], "GGTATTTTAATTTATAGT");
    }

    static SuperDictionary<ulong, int> MergeDictionaries(SuperDictionary<ulong, int>[] splitdicts)
    {
        var d = new SuperDictionary<ulong, int>();
        foreach (var sd in splitdicts)
            foreach (var kvp in sd)
                d[kvp.Key] += kvp.Value;

        return d;
    }

    static void WriteFrequencies(SuperDictionary<ulong, int> freq, int buflen, int fragmentLength)
    {
        double percent = 100.0 / (buflen - fragmentLength + 1);
        foreach (var line in (from k in freq.Keys
            orderby freq[k] descending
            select string.Format("{0} {1:f3}", PrintKey(k, fragmentLength),
                (freq.ContainsKey(k) ? freq[k] : 0) * percent)))
            Console.WriteLine(line);
        Console.WriteLine();
    }

    static void WriteCount(SuperDictionary<ulong, int> dictionary, string fragment)
    {
        ulong key = 0;
        var keybytes = Encoding.ASCII.GetBytes(fragment.ToLower());
        for (int i = 0; i < keybytes.Length; i++)
        {
            key <<= 2;
            key |= tonum[keybytes[i]];
        }
        int w;
        Console.WriteLine("{0}\t{1}",
            dictionary.TryGetValue(key, out w) ? w : 0,
            fragment);
    }

    static string PrintKey(ulong key, int fragmentLength)
    {
        char[] items = new char[fragmentLength];
        for (int i = 0; i < fragmentLength; ++i)
        {
            items[fragmentLength - i - 1] = tochar[key & 0x3];
            key >>= 2;
        }
        return new string(items);
    }

    static unsafe SuperDictionary<ulong, int> CountFrequency(byte *buffer, int length, int fragmentLength)
    {
        var dictionary = new SuperDictionary<ulong, int>();
        ulong rollingKey = 0;
        ulong mask = 0;
        int cursor;
        for (cursor = 0; cursor < fragmentLength - 1; cursor++)
        {
            rollingKey <<= 2;
            rollingKey |= tonum[buffer[cursor]];
            mask = (mask << 2) + 3;
        }
        mask = (mask << 2) + 3;
        int stop = length;
        int w;
        byte cursorByte;
        while (cursor < stop)
        {
            if ((cursorByte = buffer[cursor++]) < (byte)'a')
                cursorByte = buffer[cursor++];
            rollingKey = ((rollingKey << 2) & mask) | tonum[cursorByte];

            dictionary[rollingKey]++;
        }
        return dictionary;
    }

    static byte[] GetBytesForThirdSequence(string[] args)
    {
        const int buffersize = 2500120;
        byte[] threebuffer = null;
        var buffer = new byte[buffersize];
        int amountRead, threebuflen, indexOfFirstByteInThreeSequence, indexOfGreaterThan, threepos, tocopy;
        amountRead = threebuflen = indexOfFirstByteInThreeSequence = indexOfGreaterThan = threepos = tocopy = 0;
        bool threeFound = false;
        var source = args.Length == 1 ? (Stream) File.OpenRead(args[0]) : new BufferedStream(Console.OpenStandardInput());
        while (!threeFound && (amountRead = source.Read(buffer, 0, buffersize)) > 0)
        {
            indexOfGreaterThan = Array.LastIndexOf(buffer, (byte)'>');
            threeFound = (indexOfGreaterThan > -1 &&
                          buffer[indexOfGreaterThan + 1] == (byte)'T' &&
                          buffer[indexOfGreaterThan + 2] == (byte)'H');
            if (threeFound)
            {
                threepos += indexOfGreaterThan;
                threebuflen = threepos - 48;
                threebuffer = new byte[threebuflen];
                indexOfFirstByteInThreeSequence = Array.IndexOf<byte>(buffer, 10, indexOfGreaterThan) + 1;
                tocopy = amountRead - indexOfFirstByteInThreeSequence;
                if (amountRead < buffersize)
                    tocopy -= 1;
                Buffer.BlockCopy(buffer, indexOfFirstByteInThreeSequence, threebuffer, 0, tocopy);
                buffer = null;
            }
            else
                threepos += amountRead;
        }
        int toread = threebuflen - tocopy;
        source.Read(threebuffer, tocopy, toread);
        return threebuffer;
    }

    static byte[] tonum = new byte[256];
    static char[] tochar = new char[4];
    static void PrepareLookups()
    {
        tonum['a'] = 0;
        tonum['c'] = 1;
        tonum['g'] = 2;
        tonum['t'] = 3;
        tochar[0] = 'A';
        tochar[1] = 'C';
        tochar[2] = 'G';
        tochar[3] = 'T';
    }
}
