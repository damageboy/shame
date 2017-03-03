# shame
my stab at https://benchmarksgame.alioth.debian.org/u64q/performance.php?test=knucleotide

based on the submission by [Josh Goldfoot](https://benchmarksgame.alioth.debian.org/u64q/program.php?test=knucleotide&lang=csharpcore&id=6)
which I started off of

# build
Use `dotnet build -c release` and friends / VS 2017 / Whatever you like

# generate sample data 

once compiled, the `gendata.exe` can be used as such:
```
./gendata > knucleotide-input25000000.txt
```

to generate enough data to stard having fun with (namely to generate the same amount of data that was used in [the submussion I based this one](https://benchmarksgame.alioth.debian.org/u64q/program.php?test=knucleotide&lang=csharpcore&id=6)

# running it
```
./shame < knucleotide-input25000000.txt
```

or (this works better with profilers of sorts):

```
./shame  knucleotide-input25000000.txt
```

# how / what

This submission does a few things "better", namely:
* [SuperDictionary.cs](https://github.com/damageboy/shame/blob/master/shame/SuperDictionary.cs): a hacked up version of the coreclr [`Dictionary<T,K>`](https://github.com/dotnet/coreclr/blob/master/src/mscorlib/src/System/Collections/Generic/Dictionary.cs)
with ref-return based `this[TKey key]` operator
* Some more complex / yet more efficient chunking/ordering of the parallelization code that reduces the "straggeler effect" of having one task/thread work on a more complex part of the problem while all other tasks/threads have completed

