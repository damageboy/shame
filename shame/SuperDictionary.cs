// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

/*============================================================
**
**
**
**
** Purpose: Generic hash table implementation
**
** #DictionaryVersusHashtableThreadSafety
** Hashtable has multiple reader/single writer (MR/SW) thread safety built into
** certain methods and properties, whereas Dictionary doesn't. If you're
** converting framework code that formerly used Hashtable to Dictionary, it's
** important to consider whether callers may have taken a dependence on MR/SW
** thread safety. If a reader writer lock is available, then that may be used
** with a Dictionary to get the same thread safety guarantee.
**
** Reader writer locks don't exist in silverlight, so we do the following as a
** result of removing non-generic collections from silverlight:
** 1. If the Hashtable was fully synchronized, then we replace it with a
**    Dictionary with full locks around reads/writes (same thread safety
**    guarantee).
** 2. Otherwise, the Hashtable has the default MR/SW thread safety behavior,
**    so we do one of the following on a case-by-case basis:
**    a. If the race condition can be addressed by rearranging the code and using a temp
**       variable (for example, it's only populated immediately after created)
**       then we address the race condition this way and use Dictionary.
**    b. If there's concern about degrading performance with the increased
**       locking, we ifdef with FEATURE_NONGENERIC_COLLECTIONS so we can at
**       least use Hashtable in the desktop build, but Dictionary with full
**       locks in silverlight builds. Note that this is heavier locking than
**       MR/SW, but this is the only option without rewriting (or adding back)
**       the reader writer lock.
**    c. If there's no performance concern (e.g. debug-only code) we
**       consistently replace Hashtable with Dictionary plus full locks to
**       reduce complexity.
**    d. Most of serialization is dead code in silverlight. Instead of updating
**       those Hashtable occurences in serialization, we carved out references
**       to serialization such that this code doesn't need to build in
**       silverlight.
===========================================================*/

using System.Runtime.CompilerServices;

namespace System.Collections.Generic
{
    using System;
    using System.Collections;
    using System.Diagnostics;
    //using System.Diagnostics.Contracts;
    //using System.Runtime.Serialization;

    public class SuperDictionary<TKey, TValue>
    {
        struct Entry
        {
            public int hashCode;    // Lower 31 bits of hash code, -1 if unused
            public int next;        // Index of next entry, -1 if last
            public TKey key;           // Key of entry
            public TValue value;         // Value of entry
        }

        int[] buckets;
        Entry[] entries;
        int count;
        int version;
        int freeList;
        int freeCount;
        IEqualityComparer<TKey> comparer;
        KeyCollection keys;
        ValueCollection values;
        Object _syncRoot;

        // constants for serialization
        const string VersionName = "Version";
        const string HashSizeName = "HashSize";  // Must save buckets.Length
        const string KeyValuePairsName = "KeyValuePairs";
        const string ComparerName = "Comparer";

        public SuperDictionary() : this(0, null) { }

        public SuperDictionary(int capacity) : this(capacity, null) { }

        public SuperDictionary(IEqualityComparer<TKey> comparer) : this(0, comparer) { }

        public SuperDictionary(int capacity, IEqualityComparer<TKey> comparer)
        {
            if (capacity < 0) ThrowHelper.ThrowArgumentOutOfRangeException(ExceptionArgument.capacity);
            if (capacity > 0) Initialize(capacity);
            this.comparer = comparer ?? EqualityComparer<TKey>.Default;

#if FEATURE_RANDOMIZED_STRING_HASHING
            if (HashHelpers.s_UseRandomizedStringHashing && comparer == EqualityComparer<string>.Default)
            {
                this.comparer = (IEqualityComparer<TKey>)NonRandomizedStringEqualityComparer.Default;
            }
#endif // FEATURE_RANDOMIZED_STRING_HASHING
        }

        public SuperDictionary(IDictionary<TKey, TValue> dictionary) : this(dictionary, null) { }

        public SuperDictionary(IDictionary<TKey, TValue> dictionary, IEqualityComparer<TKey> comparer) :
            this(dictionary != null ? dictionary.Count : 0, comparer)
        {
            if (dictionary == null)
            {
                ThrowHelper.ThrowArgumentNullException(ExceptionArgument.dictionary);
            }

            // It is likely that the passed-in dictionary is Dictionary<TKey,TValue>. When this is the case,
            // avoid the enumerator allocation and overhead by looping through the entries array directly.
            // We only do this when dictionary is Dictionary<TKey,TValue> and not a subclass, to maintain
            // back-compat with subclasses that may have overridden the enumerator behavior.
            if (dictionary.GetType() == typeof(SuperDictionary<TKey, TValue>))
            {
                var d = (SuperDictionary<TKey, TValue>)dictionary;
                int count = d.count;
                var entries = d.entries;
                for (var i = 0; i < count; i++)
                {
                    if (entries[i].hashCode >= 0)
                    {
                        Add(entries[i].key, entries[i].value);
                    }
                }
                return;
            }

            foreach (var pair in dictionary)
            {
                Add(pair.Key, pair.Value);
            }
        }

        public SuperDictionary(IEnumerable<KeyValuePair<TKey, TValue>> collection) :
            this(collection, null)
        { }

        public SuperDictionary(IEnumerable<KeyValuePair<TKey, TValue>> collection, IEqualityComparer<TKey> comparer) :
            this((collection as ICollection<KeyValuePair<TKey, TValue>>)?.Count ?? 0, comparer)
        {
            if (collection == null)
            {
                ThrowHelper.ThrowArgumentNullException(ExceptionArgument.collection);
            }

            foreach (KeyValuePair<TKey, TValue> pair in collection)
            {
                Add(pair.Key, pair.Value);
            }
        }

        public IEqualityComparer<TKey> Comparer
        {
            get
            {
                return comparer;
            }
        }

        public int Count
        {
            get { return count - freeCount; }
        }

        public KeyCollection Keys
        {
            get
            {
                //Contract.Ensures(Contract.Result<KeyCollection>() != null);
                if (keys == null) keys = new KeyCollection(this);
                return keys;
            }
        }

        public ValueCollection Values
        {
            get
            {
                //Contract.Ensures(Contract.Result<ValueCollection>() != null);
                if (values == null) values = new ValueCollection(this);
                return values;
            }
        }

        public ref TValue this[TKey key]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                int i = FindOrInsertDefault(key);

                return ref entries[i].value;
            }
        }

        public void Add(TKey key, TValue value)
        {
            Insert(key, value, true);
        }

        public void Clear()
        {
            if (count <= 0)
                return;
            for (var i = 0; i < buckets.Length; i++) buckets[i] = -1;
            Array.Clear(entries, 0, count);
            freeList = -1;
            count = 0;
            freeCount = 0;
            version++;
        }

        public bool ContainsKey(TKey key)
        {
            return FindEntry(key) >= 0;
        }

        public bool ContainsValue(TValue value)
        {
            if (value == null)
            {
                for (var i = 0; i < count; i++)
                {
                    if (entries[i].hashCode >= 0 && entries[i].value == null) return true;
                }
            }
            else
            {
                EqualityComparer<TValue> c = EqualityComparer<TValue>.Default;
                for (int i = 0; i < count; i++)
                {
                    if (entries[i].hashCode >= 0 && c.Equals(entries[i].value, value)) return true;
                }
            }
            return false;
        }

        void CopyTo(KeyValuePair<TKey, TValue>[] array, int index)
        {
            if (array == null)
                ThrowHelper.ThrowArgumentNullException(ExceptionArgument.array);

            if (index < 0 || index > array.Length)
                ThrowHelper.ThrowIndexArgumentOutOfRange_NeedNonNegNumException();

            if (array.Length - index < Count)
                ThrowHelper.ThrowArgumentException(ExceptionResource.Arg_ArrayPlusOffTooSmall);

            int count = this.count;
            Entry[] entries = this.entries;
            for (int i = 0; i < count; i++)
                if (entries[i].hashCode >= 0)
                    array[index++] = new KeyValuePair<TKey, TValue>(entries[i].key, entries[i].value);
        }

        public Enumerator GetEnumerator() => new Enumerator(this, Enumerator.KeyValuePair);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        int FindEntry(TKey key)
        {
            if (buckets == null)
                return -1;

            int hashCode = comparer.GetHashCode(key) & 0x7FFFFFFF;
            for (int i = buckets[hashCode % buckets.Length]; i >= 0; i = entries[i].next)
                if (entries[i].hashCode == hashCode && comparer.Equals(entries[i].key, key))
                    return i;
            return -1;
        }

        void Initialize(int capacity)
        {
            int size = HashHelpers.GetPrime(capacity);
            buckets = new int[size];
            for (int i = 0; i < buckets.Length; i++) buckets[i] = -1;
            entries = new Entry[size];
            freeList = -1;
        }

        int Insert(TKey key, TValue value, bool add)
        {
            if (key == null)
                ThrowHelper.ThrowArgumentNullException(ExceptionArgument.key);

            if (buckets == null) Initialize(0);
            int hashCode = comparer.GetHashCode(key) & 0x7FFFFFFF;
            int targetBucket = hashCode % buckets.Length;

#if FEATURE_RANDOMIZED_STRING_HASHING
            int collisionCount = 0;
#endif

            for (int i = buckets[targetBucket]; i >= 0; i = entries[i].next)
            {
                if (entries[i].hashCode == hashCode && comparer.Equals(entries[i].key, key))
                {
                    if (add)
                        ThrowHelper.ThrowAddingDuplicateWithKeyArgumentException(key);
                    entries[i].value = value;
                    version++;
                    return i;
                }

#if FEATURE_RANDOMIZED_STRING_HASHING
                collisionCount++;
#endif
            }
            int index;
            if (freeCount > 0)
            {
                index = freeList;
                freeList = entries[index].next;
                freeCount--;
            }
            else
            {
                if (count == entries.Length) {
                    Resize();
                    targetBucket = hashCode % buckets.Length;
                }
                index = count;
                count++;
            }

            entries[index].hashCode = hashCode;
            entries[index].next = buckets[targetBucket];
            entries[index].key = key;
            entries[index].value = value;
            buckets[targetBucket] = index;
            version++;

#if FEATURE_RANDOMIZED_STRING_HASHING
// In case we hit the collision threshold we'll need to switch to the comparer which is using randomized string hashing
// in this case will be EqualityComparer<string>.Default.
// Note, randomized string hashing is turned on by default on coreclr so EqualityComparer<string>.Default will
// be using randomized string hashing

            if (collisionCount > HashHelpers.HashCollisionThreshold && comparer == NonRandomizedStringEqualityComparer.Default)
            {
                comparer = (IEqualityComparer<TKey>)EqualityComparer<string>.Default;
                Resize(entries.Length, true);
            }
#endif
            return index;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        int FindOrInsertDefault(TKey key)
        {
            if (buckets == null) Initialize(0);
            var hashCode = comparer.GetHashCode(key) & 0x7FFFFFFF;
            var targetBucket = hashCode % buckets.Length;

#if FEATURE_RANDOMIZED_STRING_HASHING
            int collisionCount = 0;
#endif

            for (int i = buckets[targetBucket]; i >= 0; i = entries[i].next)
            {
                if (entries[i].hashCode == hashCode && comparer.Equals(entries[i].key, key))
                {
                    //entries[i].value = value;
                    version++;
                    return i;
                }

#if FEATURE_RANDOMIZED_STRING_HASHING
                collisionCount++;
#endif
            }
            int index;
            if (freeCount > 0)
            {
                index = freeList;
                freeList = entries[index].next;
                freeCount--;
            }
            else
            {
                if (count == entries.Length)
                {
                    Resize();
                    targetBucket = hashCode % buckets.Length;
                }
                index = count;
                count++;
            }

            entries[index].hashCode = hashCode;
            entries[index].next = buckets[targetBucket];
            entries[index].key = key;
            entries[index].value = default(TValue);
            buckets[targetBucket] = index;
            version++;

#if FEATURE_RANDOMIZED_STRING_HASHING
// In case we hit the collision threshold we'll need to switch to the comparer which is using randomized string hashing
// in this case will be EqualityComparer<string>.Default.
// Note, randomized string hashing is turned on by default on coreclr so EqualityComparer<string>.Default will
// be using randomized string hashing

            if (collisionCount > HashHelpers.HashCollisionThreshold && comparer == NonRandomizedStringEqualityComparer.Default)
            {
                comparer = (IEqualityComparer<TKey>)EqualityComparer<string>.Default;
                Resize(entries.Length, true);
            }
#endif
            return index;
        }


        void Resize()
        {
            Resize(HashHelpers.ExpandPrime(count), false);
        }

        void Resize(int newSize, bool forceNewHashCodes)
        {
            Debug.Assert(newSize >= entries.Length);
            int[] newBuckets = new int[newSize];
            for (int i = 0; i < newBuckets.Length; i++) newBuckets[i] = -1;
            Entry[] newEntries = new Entry[newSize];
            Array.Copy(entries, 0, newEntries, 0, count);
            if (forceNewHashCodes)
            {
                for (int i = 0; i < count; i++)
                    if (newEntries[i].hashCode != -1)
                        newEntries[i].hashCode = (comparer.GetHashCode(newEntries[i].key) & 0x7FFFFFFF);
            }
            for (int i = 0; i < count; i++)
            {
                if (newEntries[i].hashCode >= 0)
                {
                    int bucket = newEntries[i].hashCode % newSize;
                    newEntries[i].next = newBuckets[bucket];
                    newBuckets[bucket] = i;
                }
            }
            buckets = newBuckets;
            entries = newEntries;
        }

        public bool Remove(TKey key)
        {
            if (key == null)
                ThrowHelper.ThrowArgumentNullException(ExceptionArgument.key);

            if (buckets != null)
            {
                int hashCode = comparer.GetHashCode(key) & 0x7FFFFFFF;
                int bucket = hashCode % buckets.Length;
                int last = -1;
                for (int i = buckets[bucket]; i >= 0; last = i, i = entries[i].next)
                {
                    if (entries[i].hashCode == hashCode && comparer.Equals(entries[i].key, key))
                    {
                        if (last < 0)
                        {
                            buckets[bucket] = entries[i].next;
                        }
                        else
                        {
                            entries[last].next = entries[i].next;
                        }
                        entries[i].hashCode = -1;
                        entries[i].next = freeList;
                        entries[i].key = default(TKey);
                        entries[i].value = default(TValue);
                        freeList = i;
                        freeCount++;
                        version++;
                        return true;
                    }
                }
            }
            return false;
        }

        public bool TryGetValue(TKey key, out TValue value)
        {
            int i = FindEntry(key);
            if (i >= 0)
            {
                value = entries[i].value;
                return true;
            }
            value = default(TValue);
            return false;
        }

        // Method similar to TryGetValue that returns the value instead of putting it in an out param.
        public TValue GetValueOrDefault(TKey key) => GetValueOrDefault(key, default(TValue));

        // Method similar to TryGetValue that returns the value instead of putting it in an out param. If the entry
        // doesn't exist, returns the defaultValue instead.
        public TValue GetValueOrDefault(TKey key, TValue defaultValue)
        {
            int i = FindEntry(key);
            if (i >= 0)
            {
                return entries[i].value;
            }
            return defaultValue;
        }

        static bool IsCompatibleKey(object key)
        {
            if (key == null)
                ThrowHelper.ThrowArgumentNullException(ExceptionArgument.key);
            return (key is TKey);
        }

        public struct Enumerator : IEnumerator<KeyValuePair<TKey, TValue>>,
            IDictionaryEnumerator
        {
            SuperDictionary<TKey, TValue> dictionary;
            int version;
            int index;
            KeyValuePair<TKey, TValue> current;
            int getEnumeratorRetType;  // What should Enumerator.Current return?

            internal const int DictEntry = 1;
            internal const int KeyValuePair = 2;

            internal Enumerator(SuperDictionary<TKey, TValue> dictionary, int getEnumeratorRetType)
            {
                this.dictionary = dictionary;
                version = dictionary.version;
                index = 0;
                this.getEnumeratorRetType = getEnumeratorRetType;
                current = new KeyValuePair<TKey, TValue>();
            }

            public bool MoveNext()
            {
                if (version != dictionary.version)
                {
                    ThrowHelper.ThrowInvalidOperationException_InvalidOperation_EnumFailedVersion();
                }

                // Use unsigned comparison since we set index to dictionary.count+1 when the enumeration ends.
                // dictionary.count+1 could be negative if dictionary.count is Int32.MaxValue
                while ((uint)index < (uint)dictionary.count)
                {
                    if (dictionary.entries[index].hashCode >= 0)
                    {
                        current = new KeyValuePair<TKey, TValue>(dictionary.entries[index].key, dictionary.entries[index].value);
                        index++;
                        return true;
                    }
                    index++;
                }

                index = dictionary.count + 1;
                current = new KeyValuePair<TKey, TValue>();
                return false;
            }

            public KeyValuePair<TKey, TValue> Current => current;

            public void Dispose()
            {
            }

            object IEnumerator.Current
            {
                get
                {
                    if (index == 0 || (index == dictionary.count + 1))
                        ThrowHelper.ThrowInvalidOperationException_InvalidOperation_EnumOpCantHappen();

                    if (getEnumeratorRetType == DictEntry)
                        return new System.Collections.DictionaryEntry(current.Key, current.Value);
                    return new KeyValuePair<TKey, TValue>(current.Key, current.Value);
                }
            }

            void IEnumerator.Reset()
            {
                if (version != dictionary.version)
                    ThrowHelper.ThrowInvalidOperationException_InvalidOperation_EnumFailedVersion();

                index = 0;
                current = new KeyValuePair<TKey, TValue>();
            }

            DictionaryEntry IDictionaryEnumerator.Entry
            {
                get
                {
                    if (index == 0 || (index == dictionary.count + 1))
                        ThrowHelper.ThrowInvalidOperationException_InvalidOperation_EnumOpCantHappen();

                    return new DictionaryEntry(current.Key, current.Value);
                }
            }

            object IDictionaryEnumerator.Key
            {
                get
                {
                    if (index == 0 || (index == dictionary.count + 1))
                        ThrowHelper.ThrowInvalidOperationException_InvalidOperation_EnumOpCantHappen();

                    return current.Key;
                }
            }

            object IDictionaryEnumerator.Value
            {
                get
                {
                    if (index == 0 || (index == dictionary.count + 1))
                        ThrowHelper.ThrowInvalidOperationException_InvalidOperation_EnumOpCantHappen();

                    return current.Value;
                }
            }
        }

        public sealed class KeyCollection : ICollection<TKey>, ICollection, IReadOnlyCollection<TKey>
        {
            private SuperDictionary<TKey, TValue> dictionary;

            public KeyCollection(SuperDictionary<TKey, TValue> dictionary)
            {
                if (dictionary == null)
                {
                    ThrowHelper.ThrowArgumentNullException(ExceptionArgument.dictionary);
                }
                this.dictionary = dictionary;
            }

            public Enumerator GetEnumerator() => new Enumerator(dictionary);

            public void CopyTo(TKey[] array, int index)
            {
                if (array == null)
                    ThrowHelper.ThrowArgumentNullException(ExceptionArgument.array);

                if (index < 0 || index > array.Length)
                    ThrowHelper.ThrowIndexArgumentOutOfRange_NeedNonNegNumException();

                if (array.Length - index < dictionary.Count)
                    ThrowHelper.ThrowArgumentException(ExceptionResource.Arg_ArrayPlusOffTooSmall);

                int count = dictionary.count;
                Entry[] entries = dictionary.entries;
                for (int i = 0; i < count; i++)
                    if (entries[i].hashCode >= 0) array[index++] = entries[i].key;
            }

            public int Count => dictionary.Count;

            bool ICollection<TKey>.IsReadOnly => true;

            void ICollection<TKey>.Add(TKey item) => ThrowHelper.ThrowNotSupportedException(ExceptionResource.NotSupported_KeyCollectionSet);

            void ICollection<TKey>.Clear() => ThrowHelper.ThrowNotSupportedException(ExceptionResource.NotSupported_KeyCollectionSet);

            bool ICollection<TKey>.Contains(TKey item)
            {
                return dictionary.ContainsKey(item);
            }

            bool ICollection<TKey>.Remove(TKey item)
            {
                ThrowHelper.ThrowNotSupportedException(ExceptionResource.NotSupported_KeyCollectionSet);
                return false;
            }

            IEnumerator<TKey> IEnumerable<TKey>.GetEnumerator()
            {
                return new Enumerator(dictionary);
            }

            IEnumerator IEnumerable.GetEnumerator()
            {
                return new Enumerator(dictionary);
            }

            void ICollection.CopyTo(Array array, int index)
            {
                if (array == null)
                {
                    ThrowHelper.ThrowArgumentNullException(ExceptionArgument.array);
                }

                if (array.Rank != 1)
                {
                    ThrowHelper.ThrowArgumentException(ExceptionResource.Arg_RankMultiDimNotSupported);
                }

                if (array.GetLowerBound(0) != 0)
                {
                    ThrowHelper.ThrowArgumentException(ExceptionResource.Arg_NonZeroLowerBound);
                }

                if (index < 0 || index > array.Length)
                {
                    ThrowHelper.ThrowIndexArgumentOutOfRange_NeedNonNegNumException();
                }

                if (array.Length - index < dictionary.Count)
                {
                    ThrowHelper.ThrowArgumentException(ExceptionResource.Arg_ArrayPlusOffTooSmall);
                }

                TKey[] keys = array as TKey[];
                if (keys != null)
                {
                    CopyTo(keys, index);
                }
                else
                {
                    object[] objects = array as object[];
                    if (objects == null)
                    {
                        ThrowHelper.ThrowArgumentException_Argument_InvalidArrayType();
                    }

                    int count = dictionary.count;
                    Entry[] entries = dictionary.entries;
                    try
                    {
                        for (int i = 0; i < count; i++)
                        {
                            if (entries[i].hashCode >= 0) objects[index++] = entries[i].key;
                        }
                    }
                    catch (ArrayTypeMismatchException)
                    {
                        ThrowHelper.ThrowArgumentException_Argument_InvalidArrayType();
                    }
                }
            }

            bool ICollection.IsSynchronized
            {
                get { return false; }
            }

            Object ICollection.SyncRoot
            {
                get { return ((ICollection)dictionary).SyncRoot; }
            }

            public struct Enumerator : IEnumerator<TKey>, System.Collections.IEnumerator
            {
                SuperDictionary<TKey, TValue> dictionary;
                int index;
                int version;
                TKey currentKey;

                internal Enumerator(SuperDictionary<TKey, TValue> dictionary)
                {
                    this.dictionary = dictionary;
                    version = dictionary.version;
                    index = 0;
                    currentKey = default(TKey);
                }

                public void Dispose()
                {
                }

                public bool MoveNext()
                {
                    if (version != dictionary.version)
                    {
                        ThrowHelper.ThrowInvalidOperationException_InvalidOperation_EnumFailedVersion();
                    }

                    while ((uint)index < (uint)dictionary.count)
                    {
                        if (dictionary.entries[index].hashCode >= 0)
                        {
                            currentKey = dictionary.entries[index].key;
                            index++;
                            return true;
                        }
                        index++;
                    }

                    index = dictionary.count + 1;
                    currentKey = default(TKey);
                    return false;
                }

                public TKey Current
                {
                    get
                    {
                        return currentKey;
                    }
                }

                Object System.Collections.IEnumerator.Current
                {
                    get
                    {
                        if (index == 0 || (index == dictionary.count + 1))
                        {
                            ThrowHelper.ThrowInvalidOperationException_InvalidOperation_EnumOpCantHappen();
                        }

                        return currentKey;
                    }
                }

                void System.Collections.IEnumerator.Reset()
                {
                    if (version != dictionary.version)
                    {
                        ThrowHelper.ThrowInvalidOperationException_InvalidOperation_EnumFailedVersion();
                    }

                    index = 0;
                    currentKey = default(TKey);
                }
            }
        }

        public sealed class ValueCollection : ICollection<TValue>, ICollection, IReadOnlyCollection<TValue>
        {
            SuperDictionary<TKey, TValue> dictionary;

            public ValueCollection(SuperDictionary<TKey, TValue> dictionary)
            {
                if (dictionary == null)
                {
                    ThrowHelper.ThrowArgumentNullException(ExceptionArgument.dictionary);
                }
                this.dictionary = dictionary;
            }

            public Enumerator GetEnumerator()
            {
                return new Enumerator(dictionary);
            }

            public void CopyTo(TValue[] array, int index)
            {
                if (array == null)
                {
                    ThrowHelper.ThrowArgumentNullException(ExceptionArgument.array);
                }

                if (index < 0 || index > array.Length)
                {
                    ThrowHelper.ThrowIndexArgumentOutOfRange_NeedNonNegNumException();
                }

                if (array.Length - index < dictionary.Count)
                {
                    ThrowHelper.ThrowArgumentException(ExceptionResource.Arg_ArrayPlusOffTooSmall);
                }

                int count = dictionary.count;
                Entry[] entries = dictionary.entries;
                for (int i = 0; i < count; i++)
                {
                    if (entries[i].hashCode >= 0) array[index++] = entries[i].value;
                }
            }

            public int Count
            {
                get { return dictionary.Count; }
            }

            bool ICollection<TValue>.IsReadOnly
            {
                get { return true; }
            }

            void ICollection<TValue>.Add(TValue item)
            {
                ThrowHelper.ThrowNotSupportedException(ExceptionResource.NotSupported_ValueCollectionSet);
            }

            bool ICollection<TValue>.Remove(TValue item)
            {
                ThrowHelper.ThrowNotSupportedException(ExceptionResource.NotSupported_ValueCollectionSet);
                return false;
            }

            void ICollection<TValue>.Clear()
            {
                ThrowHelper.ThrowNotSupportedException(ExceptionResource.NotSupported_ValueCollectionSet);
            }

            bool ICollection<TValue>.Contains(TValue item)
            {
                return dictionary.ContainsValue(item);
            }

            IEnumerator<TValue> IEnumerable<TValue>.GetEnumerator()
            {
                return new Enumerator(dictionary);
            }

            IEnumerator IEnumerable.GetEnumerator()
            {
                return new Enumerator(dictionary);
            }

            void ICollection.CopyTo(Array array, int index)
            {
                if (array == null)
                {
                    ThrowHelper.ThrowArgumentNullException(ExceptionArgument.array);
                }

                if (array.Rank != 1)
                {
                    ThrowHelper.ThrowArgumentException(ExceptionResource.Arg_RankMultiDimNotSupported);
                }

                if (array.GetLowerBound(0) != 0)
                {
                    ThrowHelper.ThrowArgumentException(ExceptionResource.Arg_NonZeroLowerBound);
                }

                if (index < 0 || index > array.Length)
                {
                    ThrowHelper.ThrowIndexArgumentOutOfRange_NeedNonNegNumException();
                }

                if (array.Length - index < dictionary.Count)
                    ThrowHelper.ThrowArgumentException(ExceptionResource.Arg_ArrayPlusOffTooSmall);

                TValue[] values = array as TValue[];
                if (values != null)
                {
                    CopyTo(values, index);
                }
                else
                {
                    object[] objects = array as object[];
                    if (objects == null)
                    {
                        ThrowHelper.ThrowArgumentException_Argument_InvalidArrayType();
                    }

                    int count = dictionary.count;
                    Entry[] entries = dictionary.entries;
                    try
                    {
                        for (int i = 0; i < count; i++)
                        {
                            if (entries[i].hashCode >= 0) objects[index++] = entries[i].value;
                        }
                    }
                    catch (ArrayTypeMismatchException)
                    {
                        ThrowHelper.ThrowArgumentException_Argument_InvalidArrayType();
                    }
                }
            }

            bool ICollection.IsSynchronized
            {
                get { return false; }
            }

            Object ICollection.SyncRoot
            {
                get { return ((ICollection)dictionary).SyncRoot; }
            }

            public struct Enumerator : IEnumerator<TValue>, System.Collections.IEnumerator
            {
                SuperDictionary<TKey, TValue> dictionary;
                int index;
                int version;
                TValue currentValue;

                internal Enumerator(SuperDictionary<TKey, TValue> dictionary)
                {
                    this.dictionary = dictionary;
                    version = dictionary.version;
                    index = 0;
                    currentValue = default(TValue);
                }

                public void Dispose()
                {
                }

                public bool MoveNext()
                {
                    if (version != dictionary.version)
                    {
                        ThrowHelper.ThrowInvalidOperationException_InvalidOperation_EnumFailedVersion();
                    }

                    while ((uint)index < (uint)dictionary.count)
                    {
                        if (dictionary.entries[index].hashCode >= 0)
                        {
                            currentValue = dictionary.entries[index].value;
                            index++;
                            return true;
                        }
                        index++;
                    }
                    index = dictionary.count + 1;
                    currentValue = default(TValue);
                    return false;
                }

                public TValue Current
                {
                    get
                    {
                        return currentValue;
                    }
                }

                Object System.Collections.IEnumerator.Current
                {
                    get
                    {
                        if (index == 0 || (index == dictionary.count + 1))
                        {
                            ThrowHelper.ThrowInvalidOperationException_InvalidOperation_EnumOpCantHappen();
                        }

                        return currentValue;
                    }
                }

                void System.Collections.IEnumerator.Reset()
                {
                    if (version != dictionary.version)
                    {
                        ThrowHelper.ThrowInvalidOperationException_InvalidOperation_EnumFailedVersion();
                    }
                    index = 0;
                    currentValue = default(TValue);
                }
            }
        }
    }
}