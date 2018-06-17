// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.


using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace shame
{
    public class SuperDictionary<TKey, TValue> : IEnumerable<KeyValuePair<TKey, TValue>>
        where TKey : IEquatable<TKey>
        where TValue : IEquatable<TValue>
    {
        struct Entry
        {
            public int hashCode;    // Lower 31 bits of hash code, -1 if unused
            public int next;        // Index of next entry, -1 if last
            public TKey key;           // Key of entry
            public TValue value;         // Value of entry
        }

        int[] _buckets;
        Entry[] _entries;
        int _count;
        int _freeList;
        int _freeCount;

        public SuperDictionary() : this(0, null) { }

        public SuperDictionary(int capacity) : this(capacity, null) { }

        public SuperDictionary(IEqualityComparer<TKey> comparer) : this(0, comparer) { }

        public SuperDictionary(int capacity, IEqualityComparer<TKey> comparer)
        {
            if (capacity < 0) ThrowHelper.ThrowArgumentOutOfRangeException(ExceptionArgument.capacity);
            if (capacity > 0) Initialize(capacity);
        }

        public SuperDictionary(IDictionary<TKey, TValue> dictionary) : this(dictionary, null) { }

        public SuperDictionary(IDictionary<TKey, TValue> dictionary, IEqualityComparer<TKey> comparer) :
            this(dictionary != null ? dictionary.Count : 0, comparer)
        {
            if (dictionary == null)
                ThrowHelper.ThrowArgumentNullException(ExceptionArgument.dictionary);

            // It is likely that the passed-in dictionary is Dictionary<TKey,TValue>. When this is the case,
            // avoid the enumerator allocation and overhead by looping through the entries array directly.
            // We only do this when dictionary is Dictionary<TKey,TValue> and not a subclass, to maintain
            // back-compat with subclasses that may have overridden the enumerator behavior.
            if (dictionary.GetType() == typeof(SuperDictionary<TKey, TValue>))
            {
                var d = (SuperDictionary<TKey, TValue>)dictionary;
                var count = d._count;
                var entries = d._entries;
                for (var i = 0; i < count; i++)
                {
                    if (entries[i].hashCode >= 0)
                        Add(entries[i].key, entries[i].value);
                }
                return;
            }

            foreach (var pair in dictionary)
                Add(pair.Key, pair.Value);
        }

        public SuperDictionary(IEnumerable<KeyValuePair<TKey, TValue>> collection) :
            this(collection, null)
        { }

        public SuperDictionary(IEnumerable<KeyValuePair<TKey, TValue>> collection, IEqualityComparer<TKey> comparer) :
            this((collection as ICollection<KeyValuePair<TKey, TValue>>)?.Count ?? 0, comparer)
        {
            if (collection == null)
                ThrowHelper.ThrowArgumentNullException(ExceptionArgument.collection);

            foreach (var pair in collection)
                Add(pair.Key, pair.Value);
        }

        public int Count => _count - _freeCount;

        public ref TValue this[TKey key] => ref TryInsert(key);

        public void Add(TKey key, TValue value) => TryInsert(key) = value;

        public void Clear()
        {
            var count = _count;
            if (count <= 0) return;
            Array.Clear(_buckets, 0, _buckets.Length);

            _count = 0;
            _freeList = -1;
            _freeCount = 0;
            Array.Clear(_entries, 0, count);
        }

        public bool ContainsKey(TKey key)
            => FindEntry(key) >= 0;

        public bool ContainsValue(TValue value)
        {
            var entries = _entries;
            if (value == null)
            {
                for (var i = 0; i < _count; i++)
                    if (entries[i].hashCode >= 0 && entries[i].value == null) return true;
            }
            else
            {
                if (default(TValue) != null)
                {
                    // ValueType: Devirtualize with EqualityComparer<TValue>.Default intrinsic
                    for (var i = 0; i < _count; i++)
                        if (entries[i].hashCode >= 0 && entries[i].value.Equals(value)) return true;
                }
                else
                {
                    // Object type: Shared Generic, EqualityComparer<TValue>.Default won't devirtualize
                    // https://github.com/dotnet/coreclr/issues/17273
                    // So cache in a local rather than get EqualityComparer per loop iteration
                    for (var i = 0; i < _count; i++)
                        if (entries[i].hashCode >= 0 && entries[i].value.Equals(value)) return true;
                }
            }
            return false;
        }

        void CopyTo(KeyValuePair<TKey, TValue>[] array, int index)
        {
            if (array == null)
                ThrowHelper.ThrowArgumentNullException(ExceptionArgument.array);

            if ((uint)index > (uint)array.Length)
                ThrowHelper.ThrowIndexArgumentOutOfRange_NeedNonNegNumException();

            if (array.Length - index < Count)
                ThrowHelper.ThrowArgumentException(ExceptionResource.Arg_ArrayPlusOffTooSmall);

            var count = _count;
            var entries = _entries;
            for (var i = 0; i < count; i++)
            {
                if (entries[i].hashCode >= 0)
                    array[index++] = new KeyValuePair<TKey, TValue>(entries[i].key, entries[i].value);
            }
        }

        public Enumerator GetEnumerator() => new Enumerator(this, Enumerator.KeyValuePair);


        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        int FindEntry(TKey key)
        {
            if (key == null)
                ThrowHelper.ThrowArgumentNullException(ExceptionArgument.key);

            var i = -1;
            var buckets = _buckets;
            var entries = _entries;
#if DETECT_CONCURRENT_USE
            var collisionCount = 0;
#endif
            if (buckets == null) return i;

            var hashCode = key.GetHashCode() & 0x7FFFFFFF;
            // Value in _buckets is 1-based
            i = buckets[hashCode % buckets.Length] - 1;
            do {
                // Should be a while loop https://github.com/dotnet/coreclr/issues/15476
                // Test in if to drop range check for following array access
                if ((uint) i >= (uint) entries.Length ||
                    (entries[i].hashCode == hashCode && entries[i].key.Equals(key)))
                    break;
                i = entries[i].next;
#if DETECT_CONCURRENT_USE
                if (collisionCount >= entries.Length)
                {
                    // The chain of entries forms a loop; which means a concurrent update has happened.
                    // Break out of the loop and throw, rather than looping forever.
                    ThrowHelper.ThrowInvalidOperationException_ConcurrentOperationsNotSupported();
                }

                collisionCount++;
#endif
            } while (true);

            return i;
        }

        int Initialize(int capacity)
        {
            var size = HashHelpers.GetPrime(capacity);

            _freeList = -1;
            _buckets = new int[size];
            _entries = new Entry[size];

            return size;
        }

        ref TValue TryInsert(TKey key)
        {
            if (_buckets == null)
                Initialize(0);

            var entries = _entries;

            var hashCode = key.GetHashCode() & 0x7FFFFFFF;

#if DETECT_CONCURRENT_USE
            var collisionCount = 0;
#endif
            ref var bucket = ref _buckets[hashCode % _buckets.Length];
            // Value in _buckets is 1-based
            var i = bucket - 1;

            var len = entries.Length;

            do
            {
                // Should be a while loop https://github.com/dotnet/coreclr/issues/15476
                // Test uint in if rather than loop condition to drop range check for following array access
                if ((uint) i >= (uint) len)
                    break;

                if (entries[i].hashCode == hashCode && entries[i].key.Equals(key))
                {
                    //entries[i].value = value;
                    return ref entries[i].value;
                }

                i = entries[i].next;
#if DETECT_CONCURRENT_USE
                if (collisionCount >= len)
                {
                    // The chain of entries forms a loop; which means a concurrent update has happened.
                    // Break out of the loop and throw, rather than looping forever.
                    ThrowHelper.ThrowInvalidOperationException_ConcurrentOperationsNotSupported();
                }

                collisionCount++;
#endif
            } while (true);

            var updateFreeList = false;
            int index;
            if (_freeCount > 0)
            {
                index = _freeList;
                updateFreeList = true;
                _freeCount--;
            }
            else
            {
                var count = _count;
                if (count == len) {
                    Resize();
                    bucket = ref _buckets[hashCode % _buckets.Length];
                }

                index = count;
                _count = count + 1;
                entries = _entries;
            }

            ref var entry = ref entries[index];

            if (updateFreeList)
            {
                _freeList = entry.next;
            }

            entry.hashCode = hashCode;
            // Value in _buckets is 1-based
            entry.next = bucket - 1;
            entry.key = key;
            //entry.value = value;
            // Value in _buckets is 1-based
            bucket = index + 1;
            return ref entry.value;
        }

        void Resize()
            => Resize(HashHelpers.ExpandPrime(_count), false);

        void Resize(int newSize, bool forceNewHashCodes)
        {
            // Value types never rehash
            Debug.Assert(!forceNewHashCodes || default(TKey) == null);
            Debug.Assert(newSize >= _entries.Length);

            var buckets = new int[newSize];
            var entries = new Entry[newSize];

            var count = _count;
            Array.Copy(_entries, 0, entries, 0, count);

            if (default(TKey) == null && forceNewHashCodes) {
                for (var i = 0; i < count; i++) {
                    if (entries[i].hashCode >= 0)
                        entries[i].hashCode = (entries[i].key.GetHashCode() & 0x7FFFFFFF);
                }
            }

            for (var i = 0; i < count; i++) {
                if (entries[i].hashCode >= 0) {
                    var bucket = entries[i].hashCode % newSize;
                    // Value in _buckets is 1-based
                    entries[i].next = buckets[bucket] - 1;
                    // Value in _buckets is 1-based
                    buckets[bucket] = i + 1;
                }
            }

            _buckets = buckets;
            _entries = entries;
        }

        // The overload Remove(TKey key, out TValue value) is a copy of this method with one additional
        // statement to copy the value for entry being removed into the output parameter.
        // Code has been intentionally duplicated for performance reasons.
        public bool Remove(TKey key)
        {
            if (key == null)
                ThrowHelper.ThrowArgumentNullException(ExceptionArgument.key);

            if (_buckets == null)
                return false;

            var hashCode = key.GetHashCode() & 0x7FFFFFFF;
            var bucket = hashCode % _buckets.Length;
            var last = -1;
            // Value in _buckets is 1-based
            var i = _buckets[bucket] - 1;
            while (i >= 0)
            {
                ref var entry = ref _entries[i];

                if (entry.hashCode == hashCode && entry.key.Equals(key))
                {
                    // Value in _buckets is 1-based
                    if (last < 0)
                        _buckets[bucket] = entry.next + 1;
                    else
                        _entries[last].next = entry.next;
                    entry.hashCode = -1;
                    entry.next = _freeList;

                    if (RuntimeHelpers.IsReferenceOrContainsReferences<TKey>())
                        entry.key = default;
                    if (RuntimeHelpers.IsReferenceOrContainsReferences<TValue>())
                        entry.value = default;
                    _freeList = i;
                    _freeCount++;
                    return true;
                }

                last = i;
                i = entry.next;
            }
            return false;
        }

        // This overload is a copy of the overload Remove(TKey key) with one additional
        // statement to copy the value for entry being removed into the output parameter.
        // Code has been intentionally duplicated for performance reasons.
        public bool Remove(TKey key, out TValue value)
        {
            if (_buckets != null)
            {
                var hashCode = key.GetHashCode() & 0x7FFFFFFF;
                var bucket = hashCode % _buckets.Length;
                var last = -1;
                // Value in _buckets is 1-based
                var i = _buckets[bucket] - 1;
                while (i >= 0)
                {
                    ref var entry = ref _entries[i];

                    if (entry.hashCode == hashCode && entry.key.Equals(key))
                    {
                        if (last < 0)
                        {
                            // Value in _buckets is 1-based
                            _buckets[bucket] = entry.next + 1;
                        }
                        else
                        {
                            _entries[last].next = entry.next;
                        }

                        value = entry.value;

                        entry.hashCode = -1;
                        entry.next = _freeList;

                        if (RuntimeHelpers.IsReferenceOrContainsReferences<TKey>())
                        {
                            entry.key = default;
                        }

                        if (RuntimeHelpers.IsReferenceOrContainsReferences<TValue>())
                        {
                            entry.value = default;
                        }

                        _freeList = i;
                        _freeCount++;
                        return true;
                    }

                    last = i;
                    i = entry.next;
                }
            }

            value = default;
            return false;
        }

        public bool TryGetValue(TKey key, out TValue value)
        {
            var i = FindEntry(key);
            if (i >= 0)
            {
                value = _entries[i].value;
                return true;
            }
            value = default;
            return false;
        }

        /// <summary>
        /// Ensures that the dictionary can hold up to 'capacity' entries without any further expansion of its backing storage
        /// </summary>
        public int EnsureCapacity(int capacity)
        {
            if (capacity < 0)
                ThrowHelper.ThrowArgumentOutOfRangeException(ExceptionArgument.capacity);
            var currentCapacity = _entries == null ? 0 : _entries.Length;
            if (currentCapacity >= capacity)
                return currentCapacity;
            if (_buckets == null)
                return Initialize(capacity);
            var newSize = HashHelpers.GetPrime(capacity);
            Resize(newSize, forceNewHashCodes: false);
            return newSize;
        }

        /// <summary>
        /// Sets the capacity of this dictionary to what it would be if it had been originally initialized with all its entries
        ///
        /// This method can be used to minimize the memory overhead
        /// once it is known that no new elements will be added.
        ///
        /// To allocate minimum size storage array, execute the following statements:
        ///
        /// dictionary.Clear();
        /// dictionary.TrimExcess();
        /// </summary>
        public void TrimExcess()
            => TrimExcess(Count);

        /// <summary>
        /// Sets the capacity of this dictionary to hold up 'capacity' entries without any further expansion of its backing storage
        ///
        /// This method can be used to minimize the memory overhead
        /// once it is known that no new elements will be added.
        /// </summary>
        public void TrimExcess(int capacity)
        {
            if (capacity < Count)
                ThrowHelper.ThrowArgumentOutOfRangeException(ExceptionArgument.capacity);
            var newSize = HashHelpers.GetPrime(capacity);

            var oldEntries = _entries;
            var currentCapacity = oldEntries == null ? 0 : oldEntries.Length;
            if (newSize >= currentCapacity)
                return;

            var oldCount = _count;
            Initialize(newSize);
            var entries = _entries;
            var buckets = _buckets;
            var count = 0;
            for (var i = 0; i < oldCount; i++)
            {
                var hashCode = oldEntries[i].hashCode;
                if (hashCode >= 0)
                {
                    ref var entry = ref entries[count];
                    entry = oldEntries[i];
                    var bucket = hashCode % newSize;
                    // Value in _buckets is 1-based
                    entry.next = buckets[bucket] - 1;
                    // Value in _buckets is 1-based
                    buckets[bucket] = count + 1;
                    count++;
                }
            }
            _count = count;
            _freeCount = 0;
        }


        static bool IsCompatibleKey(object key)
        {
            if (key == null)
                ThrowHelper.ThrowArgumentNullException(ExceptionArgument.key);
            return (key is TKey);
        }

        IEnumerator<KeyValuePair<TKey, TValue>> IEnumerable<KeyValuePair<TKey, TValue>>.GetEnumerator() => GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public struct Enumerator : IEnumerator<KeyValuePair<TKey, TValue>>,
            IDictionaryEnumerator
        {
            SuperDictionary<TKey, TValue> _dictionary;
            int _index;
            KeyValuePair<TKey, TValue> _current;
            int _getEnumeratorRetType;  // What should Enumerator.Current return?

            internal const int DictEntry = 1;
            internal const int KeyValuePair = 2;

            internal Enumerator(SuperDictionary<TKey, TValue> dictionary, int getEnumeratorRetType)
            {
                _dictionary = dictionary;
                _index = 0;
                _getEnumeratorRetType = getEnumeratorRetType;
                _current = new KeyValuePair<TKey, TValue>();
            }

            public bool MoveNext()
            {
                // Use unsigned comparison since we set index to dictionary.count+1 when the enumeration ends.
                // dictionary.count+1 could be negative if dictionary.count is Int32.MaxValue
                while ((uint)_index < (uint)_dictionary._count)
                {
                    ref Entry entry = ref _dictionary._entries[_index++];

                    if (entry.hashCode >= 0)
                    {
                        _current = new KeyValuePair<TKey, TValue>(entry.key, entry.value);
                        return true;
                    }
                }

                _index = _dictionary._count + 1;
                _current = new KeyValuePair<TKey, TValue>();
                return false;
            }

            public KeyValuePair<TKey, TValue> Current => _current;

            public void Dispose()
            {
            }

            object IEnumerator.Current
            {
                get
                {
                    if (_index == 0 || (_index == _dictionary._count + 1))
                        ThrowHelper.ThrowInvalidOperationException_InvalidOperation_EnumOpCantHappen();

                    if (_getEnumeratorRetType == DictEntry)
                        return new DictionaryEntry(_current.Key, _current.Value);

                    return new KeyValuePair<TKey, TValue>(_current.Key, _current.Value);
                }
            }

            void IEnumerator.Reset()
            {
                _index = 0;
                _current = new KeyValuePair<TKey, TValue>();
            }

            DictionaryEntry IDictionaryEnumerator.Entry
            {
                get
                {
                    if (_index == 0 || (_index == _dictionary._count + 1))
                    {
                        ThrowHelper.ThrowInvalidOperationException_InvalidOperation_EnumOpCantHappen();
                    }

                    return new DictionaryEntry(_current.Key, _current.Value);
                }
            }

            object IDictionaryEnumerator.Key
            {
                get
                {
                    if (_index == 0 || (_index == _dictionary._count + 1))
                    {
                        ThrowHelper.ThrowInvalidOperationException_InvalidOperation_EnumOpCantHappen();
                    }

                    return _current.Key;
                }
            }

            object IDictionaryEnumerator.Value
            {
                get
                {
                    if (_index == 0 || (_index == _dictionary._count + 1))
                    {
                        ThrowHelper.ThrowInvalidOperationException_InvalidOperation_EnumOpCantHappen();
                    }

                    return _current.Value;
                }
            }
        }
    }
}
