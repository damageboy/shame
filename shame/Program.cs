/* The Computer Language Benchmarks Game
   http://benchmarksgame.alioth.debian.org/
 *
 * submitted by Dan Shechter
 *
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using shame;

public class Program
{
    public static unsafe void Main(string[] args)
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
        var prm = (
            from chunk in Enumerable.Range(0, procs)
            from fl in new[] { 1, 2, 3, 4, 6, 12, 18 }
            select (O: GetBufferOffset(chunk, fl), L: bufferLengths[chunk], FL: fl)).ToArray();

        var res = (
            from param in prm.AsParallel()
            select (FL: param.FL, D: CountFrequency(p + param.O, param.L, param.FL))).ToArray();

        var x1 = (
            from r in res
            group r by r.FL
            into g
            select (FL: g.Key, SD: g.Select(x => x.D).ToArray())).ToArray();

        //var x2 = x1.OrderBy(x => x.FL);
        var dicts = (from x in x1.AsParallel()
                     select MergeDictionaries(x.SD)).ToArray();

        var buflen = dicts[0].Sum(e => e.Value);

        WriteFrequencies(dicts[0], buflen, 1);
        WriteFrequencies(dicts[1], buflen, 2);

        WriteCount(dicts[2], "GGT");
        WriteCount(dicts[3], "GGTA");
        WriteCount(dicts[4], "GGTATT");
        WriteCount(dicts[5], "GGTATTTTAATT");
        WriteCount(dicts[6], "GGTATTTTAATTTATAGT");

#if VERIFY
        AssertResults(dicts);
#endif
    }


    static SuperDictionary<ulong, int> MergeDictionaries(SuperDictionary<ulong, int>[] splitDicts)
    {
        var d = new SuperDictionary<ulong, int>();
        foreach (var sd in splitDicts)
            foreach (var kvp in sd)
                d[kvp.Key] += kvp.Value;

        return d;
    }

    static void WriteFrequencies(SuperDictionary<ulong, int> freq, int buflen, int fragmentLength)
    {
        var percent = 100.0 / (buflen - fragmentLength + 1);
        foreach (var line in (from e in freq
            orderby e.Value descending
            select string.Format("{0} {1:f3}", GetKeyAsString(e.Key, fragmentLength),
                (freq.ContainsKey(e.Key) ? e.Value : 0) * percent)))
            Console.WriteLine(line);
        Console.WriteLine();
    }

    static void WriteCount(SuperDictionary<ulong, int> dictionary, string fragment)
    {
        ulong key = 0;
        var keybytes = Encoding.ASCII.GetBytes(fragment.ToLower());
        for (var i = 0; i < keybytes.Length; i++) {
            key <<= 2;
            key |= tonum[keybytes[i]];
        }

        Console.WriteLine("{0}\t{1}",
            dictionary.TryGetValue(key, out var w) ? w : 0,
            fragment);
    }

    static string GetKeyAsString(ulong key, int fragmentLength)
    {
        var items = new char[fragmentLength];
        for (var i = 0; i < fragmentLength; ++i) {
            items[fragmentLength - i - 1] = tochar[key & 0x3];
            key >>= 2;
        }
        return new string(items);
    }

    static ulong GetKeyAsUlong(string key)
    {
        key = key.ToLower();
        ulong rollingKey = 0;
        for (var i = 0; i < key.Length; i++) {
            rollingKey <<= 2;
            rollingKey |= tonum[key[i]];
        }

        return rollingKey;
    }
    static unsafe SuperDictionary<ulong, int> CountFrequency(byte *buffer, int length, int fragmentLength)
    {
        var dictionary = new SuperDictionary<ulong, int>();
        ulong rollingKey = 0;
        ulong mask = 0;
        int cursor;
        for (cursor = 0; cursor < fragmentLength - 1; cursor++) {
            rollingKey <<= 2;
            rollingKey |= tonum[buffer[cursor]];
            mask = (mask << 2) + 3;
        }
        mask = (mask << 2) + 3;
        var stop = length;
        while (cursor < stop) {
            byte cursorByte;
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
        var threeFound = false;
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
        var toread = threebuflen - tocopy;
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
#if VERIFY
    static void AssertResults(SuperDictionary<ulong, int>[] dicts)
    {
        var expectedDicts = new[]
        {
            new Dictionary<string, int>()
            {
                {"T", 37688130},
                {"G", 24693096},
                {"C", 24749421},
                {"A", 37869353},

            },

            new Dictionary<string, int>()
            {
                {"TT", 11364196},
                {"TG", 7445440},
                {"TC", 7463353},
                {"TA", 11415139},
                {"GT", 7446336},
                {"GG", 4877909},
                {"GC", 4888588},
                {"GA", 7480263},
                {"CT", 7464238},
                {"CG", 4885915},
                {"CC", 4896659},
                {"CA", 7502608},
                {"AT", 11413359},
                {"AG", 7483832},
                {"AC", 7500819},
                {"AA", 11471342},
            },

            new Dictionary<string, int>() {{"GGT", 1471758}},
            new Dictionary<string, int>() {{"GGTA", 446535}},
            new Dictionary<string, int>() {{"GGTATT", 47336}},
            new Dictionary<string, int>() {{"GGTATTTTAATT", 893}},
            new Dictionary<string, int>() {{"GGTATTTTAATTTATAGT", 893}},
        };


        foreach (var t in expectedDicts.Zip(dicts, (e, d) => (e, d)))
        foreach (var e in t.e)
        {
            var key = GetKeyAsUlong(e.Key);
            var count = t.d[key];
            if (e.Value != count)
                Console.WriteLine($"Expected to see count {e.Value} for key \"{e.Key}\" but got {count} instead");

        }
    }
#endif
}
