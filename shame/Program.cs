/* The Computer Language Benchmarks Game
   http://benchmarksgame.alioth.debian.org/
 *
 * submitted by Dan Shechter
 *
 */

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Experimental.Collections;
public unsafe class Program
{
    public static unsafe void Main(string[] args)
    {
#if PROFILE
		long startTicks, readDataTicks, preProcessTicks, countFreqTicks, mergeDictsTicks, endTicks;
        startTicks = Stopwatch.GetTimestamp();
#endif

        PrepareLookups();
        var buffer = GetBytesForThirdSequence(args);
#if PROFILE
        readDataTicks = Stopwatch.GetTimestamp();
#endif
        var p = PreprocessBuffer(buffer, out var len);

#if PROFILE
        preProcessTicks = Stopwatch.GetTimestamp();
#endif
        var procs = Environment.ProcessorCount;

        var bufferLengths = Enumerable.Repeat(len / procs, procs).ToArray();
        bufferLengths[procs - 1] += len % procs;
        var bufferOffsets = Enumerable.Range(0, procs).Select(i => i * (len / procs)).ToArray();

        int GetBufferOffset(int c, int fl)
        {
            if (c == 0)
                return bufferOffsets[c];
            return bufferOffsets[c] - (fl - 1);
        }

        var prm = (
            from chunk in Enumerable.Range(0, procs)
            from fl in new[] { 1, 2, 3, 4, 6, 12, 18 }
            select (O: GetBufferOffset(chunk, fl), L: bufferLengths[chunk], FL: fl)).ToArray();

        var res = (
            from param in prm.AsParallel()//.WithDegreeOfParallelism(procs)
            select (FL: param.FL, D: CountFrequency(p + param.O, param.L, param.FL))).ToArray();

#if PROFILE
        countFreqTicks = Stopwatch.GetTimestamp();
#endif

        var x1 = (
            from r in res
            group r by r.FL
            into g
            select (FL: g.Key, SD: g.Select(x => x.D).ToArray())).ToArray();

        //var x2 = x1.OrderBy(x => x.FL);
        var dicts = (from x in x1.AsParallel()
                     select MergeDictionaries(x.SD)).ToArray();

#if PROFILE
        mergeDictsTicks = Stopwatch.GetTimestamp();
#endif
        var buflen = dicts[0].Sum(e => e.Value);

        WriteFrequencies(dicts[0], buflen, 1);
        WriteFrequencies(dicts[1], buflen, 2);

        WriteCount(dicts[2], "GGT");
        WriteCount(dicts[3], "GGTA");
        WriteCount(dicts[4], "GGTATT");
        WriteCount(dicts[5], "GGTATTTTAATT");
        WriteCount(dicts[6], "GGTATTTTAATTTATAGT");

#if PROFILE
        endTicks = Stopwatch.GetTimestamp();
#endif

#if VERIFY
        AssertResults(dicts);

        static void AssertResults(RefDictionary<ulong, int>[] dicts)
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

#if PROFILE
        PrintTimes();

        void PrintTimes()
        {
            endTicks -= startTicks;
            mergeDictsTicks -= countFreqTicks;
            countFreqTicks -= preProcessTicks;
            preProcessTicks -= readDataTicks;
            readDataTicks -= startTicks;

            long ToUsec(long ticks) => ticks * 1_000_000 / Stopwatch.Frequency;

            Console.WriteLine($"Entire program took: {ToUsec(endTicks):N0}us");
            Console.WriteLine($"Reading data took: {ToUsec(readDataTicks):N0}us");
            Console.WriteLine($"Pre processing took: {ToUsec(preProcessTicks):N0}us");
            Console.WriteLine($"Counting frequencies took: {ToUsec(countFreqTicks):N0}us");
            Console.WriteLine($"merging results took: {ToUsec(mergeDictsTicks):N0}us");
        }
#endif
    }

    /// <summary>
    /// This pre-processes the buffer so that all non ACGT chars are discarded from the target buffer on the one hand,
    /// and while doing that translates the ACGT/acgt chars to 0-3 bytes
    /// </summary>
    /// <param name="buffer">The buffer</param>
    /// <param name="len">the resulting length</param>
    /// <returns>the new buffer that was allocated with <see cref="Marshal.AllocHGlobal(int)"/></returns>
    static unsafe byte *PreprocessBuffer(byte[] buffer, out int len)
    {
        var p = (byte *) Marshal.AllocHGlobal(buffer.Length).ToPointer();
        var tonum = stackalloc byte[256];
        tonum['a'] = tonum['A'] = 0;
        tonum['c'] = tonum['C'] = 1;
        tonum['g'] = tonum['G'] = 2;
        tonum['t'] = tonum['T'] = 3;
        int i = 0;
        int j = 0;
        for (; i < buffer.Length; i++)
        {
            if (buffer[i] < 'a')
                continue;
            p[j++] = tonum[buffer[i]];
        }

        len = j;
        return p;
    }

    static unsafe RefDictionary<ulong, int> CountFrequency(byte *buffer, int length, int fragmentLength)
    {
        var dictionary = new RefDictionary<ulong, int>();
        var stop = buffer + length;
        ulong rollingKey = 0;
        ulong mask = 0;
        int i;
        // Preseed the rolling-key with the initial fragment - 1,
        // so that the main loop keeps reading a single byte/value
        // while also calculating the mask for the next loads
        for (i = 0; i < fragmentLength - 1; i++) {
            rollingKey <<= 2;
            rollingKey |= *(buffer++);
            mask = (mask << 2) + 0b11;
        }

        // The mask is actually one element larger
        mask = (mask << 2) + 0b11;

        // Read the rest of the data to the end
        while (buffer < stop) {
            rollingKey = ((rollingKey << 2) & mask) | *(buffer++);
            dictionary[rollingKey]++;
        }

        return dictionary;
    }

    static RefDictionary<ulong, int> MergeDictionaries(RefDictionary<ulong, int>[] splitDicts)
    {
        //var d = new SuperDictionary<ulong, int>();
        var d = splitDicts[0];
        for (var i = 1; i < splitDicts.Length; i++) {
            var sd = splitDicts[i];
            foreach (var kvp in sd)
                d[kvp.Key] += kvp.Value;
        }

        return d;
    }

    static void WriteFrequencies(RefDictionary<ulong, int> freq, int buflen, int fragmentLength)
    {
        var percent = 100.0 / (buflen - fragmentLength + 1);
        foreach (var line in (from e in freq
            orderby e.Value descending
            select string.Format("{0} {1:f3}", GetKeyAsString(e.Key, fragmentLength),
                (freq.ContainsKey(e.Key) ? e.Value : 0) * percent)))
            Console.WriteLine(line);
        Console.WriteLine();
    }

    static void WriteCount(RefDictionary<ulong, int> dictionary, string fragment)
    {
        ulong key = 0;
        var keybytes = Encoding.ASCII.GetBytes(fragment.ToLower());
        for (var i = 0; i < keybytes.Length; i++) {
            key <<= 2;
            key |= _tonum[keybytes[i]];
        }


        Console.WriteLine("{0}\t{1}",
            dictionary.GetValueOrDefault(key),
            fragment);
    }

    static string GetKeyAsString(ulong key, int fragmentLength)
    {
        var items = new char[fragmentLength];
        for (var i = 0; i < fragmentLength; ++i) {
            items[fragmentLength - i - 1] = _tochar[key & 0x3];
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
            rollingKey |= _tonum[key[i]];
        }

        return rollingKey;
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
            if (!threeFound)
				threepos += amountRead;
			else {
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
        }
        var toread = threebuflen - tocopy;
        source.Read(threebuffer, tocopy, toread);
        return threebuffer;
    }

    static readonly byte[] _tonum = new byte[256];
    static readonly char[] _tochar = new char[4];
    static void PrepareLookups()
    {
        _tonum['a'] = 0;
        _tonum['c'] = 1;
        _tonum['g'] = 2;
        _tonum['t'] = 3;
        _tochar[0] = 'A';
        _tochar[1] = 'C';
        _tochar[2] = 'G';
        _tochar[3] = 'T';
    }
}
