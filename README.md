# shame
my stab at [knucleotide benchmark](https://benchmarksgame-team.pages.debian.net/benchmarksgame/performance/knucleotide.html)

based on the submission by [Josh Goldfoot](https://benchmarksgame.alioth.debian.org/u64q/program.php?test=knucleotide&lang=csharpcore&id=6)
which I started off of...

# build
Use `dotnet build -c release` and friends / VS 2017 / Whatever you like
Alternatively, if you wish to publish as a self contained binary, use either:
`dotnet publish -c release -r linux-x64` for publishing on linux -or-
`dotnet publish -c release -r win10-x64` for windows

# generate sample data 

once compiled, the `mkdata.exe` can be used as such:
```
cd mkdata
dotnet run 25000000 > knucleotide-input25000000.txt
```

to generate enough data to start having fun with (namely to generate the same amount of data that was used in [the submussion I based this one](https://benchmarksgame-team.pages.debian.net/benchmarksgame/program/knucleotide-csharpcore-8.html)

# running it
```
./shame < knucleotide-input25000000.txt
```

or (this works better with profilers of sorts):

```
./shame knucleotide-input25000000.txt
```

# how / what

This submission does a few things "better", namely:
* [SuperDictionary.cs](https://github.com/damageboy/shame/blob/master/shame/SuperDictionary.cs): a hacked up version of the coreclr [`Dictionary<T,K>`](https://github.com/dotnet/coreclr/blob/master/src/mscorlib/src/System/Collections/Generic/Dictionary.cs) with the rough following mods:
  * ref-return based `this[TKey key]` operator
  * remove the concurrent use detection logic
  * constrain `TKey` / `TValue` to implement `IEquatable<T>`, eliminating `EqualityComparer<T>.Default`
  * Remove the `_version` tracking
  * Remove `Keys` / `Values` properties of the Dictionary

* Some more complex / yet more efficient chunking/ordering of the parallelization code that reduces the "straggeler effect" of having one task/thread work on a more complex part of the problem while all other tasks/threads have completed

