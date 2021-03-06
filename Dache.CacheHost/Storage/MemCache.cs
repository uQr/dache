﻿using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Linq;
using System.Runtime.Caching;
using System.Text.RegularExpressions;
using System.Threading;
using Dache.Core.Performance;
using Microsoft.VisualBasic.Devices;
using SharpMemoryCache;

namespace Dache.CacheHost.Storage
{
    /// <summary>
    /// Encapsulates a memory cache that can store byte arrays. This type is thread safe.
    /// </summary>
    public class MemCache : IMemCache
    {
        // The underlying memory cache
        private MemoryCache _memoryCache = null;
        // The memory cache lock
        private readonly ReaderWriterLockSlim _memoryCacheLock = new ReaderWriterLockSlim();
        // The performance data manager
        private readonly PerformanceDataManager _performanceDataManager = null;
        // The dictionary that serves as an intern set, with the key being the cache key and the value being a hash code to a potentially shared object
        private readonly IDictionary<string, string> _internDictionary = null;
        // The dictionary that serves as an intern reference count, with the key being the hash code and the value being the number of references to the object
        private readonly IDictionary<string, int> _internReferenceDictionary = null;
        // The interned object cache item policy
        private static readonly CacheItemPolicy _internCacheItemPolicy = new CacheItemPolicy { Priority = CacheItemPriority.NotRemovable };
        // The intern dictionary lock
        private readonly ReaderWriterLockSlim _internDictionaryLock = new ReaderWriterLockSlim();
        // The cache name
        private readonly string _cacheName;
        // The cache configuration
        private readonly NameValueCollection _cacheConfig;
        // The performance counter for the current memory of the process
        private readonly PerformanceCounter _currentMemoryPerformanceCounter = new PerformanceCounter("Process", "Private Bytes", Process.GetCurrentProcess().ProcessName, true);
        // The per second timer
        private readonly Timer _perSecondTimer = null;

        /// <summary>
        /// The constructor.
        /// </summary>
        /// <param name="physicalMemoryLimitPercentage">The cache memory limit, as a percentage of the total system memory.</param>
        /// <param name="performanceDataManager">The performance data manager.</param>
        internal MemCache(int physicalMemoryLimitPercentage, PerformanceDataManager performanceDataManager)
        {
            // Sanitize
            if (physicalMemoryLimitPercentage <= 0)
            {
                throw new ArgumentException("cannot be <= 0", "physicalMemoryLimitPercentage");
            }
            if (performanceDataManager == null)
            {
                throw new ArgumentNullException("performanceDataManager");
            }

            var cacheMemoryLimitMegabytes = (int)(((double)physicalMemoryLimitPercentage / 100) * (new ComputerInfo().TotalPhysicalMemory / 1048576)); // bytes / (1024 * 1024) for MB;

            _cacheName = "Dache";
            _cacheConfig = new NameValueCollection();
            _cacheConfig.Add("pollingInterval", "00:00:05");
            _cacheConfig.Add("cacheMemoryLimitMegabytes", cacheMemoryLimitMegabytes.ToString());
            _cacheConfig.Add("physicalMemoryLimitPercentage", physicalMemoryLimitPercentage.ToString());

            _memoryCache = new TrimmingMemoryCache(_cacheName, _cacheConfig);
            _internDictionary = new Dictionary<string, string>(100);
            _internReferenceDictionary = new Dictionary<string, int>(100);

            _performanceDataManager = performanceDataManager;

            // Configure per second timer to fire every 1000 ms starting 1000ms from now
            _perSecondTimer = new Timer(PerSecondOperations, null, 1000, 1000);
        }

        /// <summary>
        /// Inserts or updates a byte array in the cache at the given key with the specified cache item policy.
        /// </summary>
        /// <param name="key">The key of the byte array. Null is not supported.</param>
        /// <param name="value">The byte array. Null is not supported.</param>
        /// <param name="cacheItemPolicy">The cache item policy.</param>
        public void Add(string key, byte[] value, CacheItemPolicy cacheItemPolicy)
        {
            // Sanitize
            if (string.IsNullOrWhiteSpace(key))
            {
                throw new ArgumentException("cannot be null, empty, or white space", "key");
            }
            if (value == null)
            {
                // MemoryCache does not support null values
                throw new ArgumentNullException("value");
            }
            if (cacheItemPolicy == null)
            {
                throw new ArgumentNullException("cacheItemPolicy");
            }

            _memoryCacheLock.EnterReadLock();
            try
            {
                // Add to the cache
                _memoryCache.Set(key, value, cacheItemPolicy);
            }
            finally
            {
                _memoryCacheLock.ExitReadLock();
            }

            // Increment the Adds
            _performanceDataManager.IncrementAddsPerSecond();
        }

        /// <summary>
        /// Inserts or updates an interned byte array in the cache at the given key. 
        /// Interned values cannot expire or be evicted unless removed manually.
        /// </summary>
        /// <param name="key">The key of the byte array. Null is not supported.</param>
        /// <param name="value">The byte array. Null is not supported.</param>
        public void AddInterned(string key, byte[] value)
        {
            // Sanitize
            if (string.IsNullOrWhiteSpace(key))
            {
                throw new ArgumentException("cannot be null, empty, or white space", "key");
            }
            if (value == null)
            {
                // MemoryCache does not support null values
                throw new ArgumentNullException("value");
            }

            // Intern this key
            var hashKey = CalculateHash(value);
            int referenceCount = 0;

            _internDictionaryLock.EnterWriteLock();
            try
            {
                // Get the old hash key if it exists
                if (_internDictionary.ContainsKey(key))
                {
                    var oldHashKey = _internDictionary[key];
                    // Do a remove to decrement intern reference count
                    referenceCount = --_internReferenceDictionary[oldHashKey];

                    // Check if reference is dead
                    if (referenceCount == 0)
                    {
                        _memoryCacheLock.EnterReadLock();
                        try
                        {
                            // Remove actual old object
                            _memoryCache.Remove(oldHashKey);
                        }
                        finally
                        {
                            _memoryCacheLock.ExitReadLock();
                        }
                    }
                }
                // Intern the value
                _internDictionary[key] = hashKey;
                if (!_internReferenceDictionary.TryGetValue(hashKey, out referenceCount))
                {
                    _internReferenceDictionary[hashKey] = referenceCount;
                }

                _internReferenceDictionary[hashKey]++;
            }
            finally
            {
                _internDictionaryLock.ExitWriteLock();
            }

            // Now possibly add to MemoryCache
            if (!_memoryCache.Contains(hashKey))
            {
                _memoryCacheLock.EnterReadLock();
                try
                {
                    _memoryCache.Set(hashKey, value, _internCacheItemPolicy);
                }
                finally
                {
                    _memoryCacheLock.ExitReadLock();
                }
            }

            // Increment the Adds
            _performanceDataManager.IncrementAddsPerSecond();
        }

        /// <summary>
        /// Gets a byte array from the cache.
        /// </summary>
        /// <param name="key">The key of the byte array.</param>
        /// <returns>The byte array if found, otherwise null.</returns>
        public byte[] Get(string key)
        {
            // Sanitize
            if (string.IsNullOrWhiteSpace(key))
            {
                return null;
            }

            // Increment the Gets
            _performanceDataManager.IncrementGetsPerSecond();

            // Check for interned
            string hashKey = null;
            _internDictionaryLock.EnterReadLock();
            try
            {
                if (!_internDictionary.TryGetValue(key, out hashKey))
                {
                    // Not interned
                    _memoryCacheLock.EnterReadLock();
                    try
                    {
                        return _memoryCache.Get(key) as byte[];
                    }
                    finally
                    {
                        _memoryCacheLock.ExitReadLock();
                    }
                }
            }
            finally
            {
                _internDictionaryLock.ExitReadLock();
            }

            _memoryCacheLock.EnterReadLock();
            try
            {
                return _memoryCache.Get(hashKey) as byte[];
            }
            finally
            {
                _memoryCacheLock.ExitReadLock();
            }
        }

        /// <summary>
        /// Removes a byte array from the cache.
        /// </summary>
        /// <param name="key">The key of the byte array.</param>
        /// <returns>The byte array if the key was found in the cache, otherwise null.</returns>
        public byte[] Remove(string key)
        {
            // Sanitize
            if (string.IsNullOrWhiteSpace(key))
            {
                return null;
            }

            // Increment the Removes
            _performanceDataManager.IncrementRemovesPerSecond();

            string hashKey = null;
            int referenceCount = 0;
            // Delete this interned key
            _internDictionaryLock.EnterReadLock();
            try
            {
                if (!_internDictionary.TryGetValue(key, out hashKey))
                {
                    // Not interned, do normal work
                    _memoryCacheLock.EnterReadLock();
                    try
                    {
                        return _memoryCache.Remove(key) as byte[];
                    }
                    finally
                    {
                        _memoryCacheLock.ExitReadLock();
                    }
                }
            }
            finally
            {
                _internDictionaryLock.ExitReadLock();
            }

            // Is interned, remove it
            _internDictionaryLock.EnterWriteLock();
            try
            {
                // Double lock check to ensure still interned
                if (_internDictionary.TryGetValue(key, out hashKey))
                {
                    _internDictionary.Remove(key);
                    referenceCount = --_internReferenceDictionary[hashKey];

                    // Check if reference is dead
                    if (referenceCount == 0)
                    {
                        // Remove actual object
                        _memoryCacheLock.EnterReadLock();
                        try
                        {
                            return _memoryCache.Remove(hashKey) as byte[];
                        }
                        finally
                        {
                            _memoryCacheLock.ExitReadLock();
                        }
                    }
                }
            }
            finally
            {
                _internDictionaryLock.EnterWriteLock();
            }

            // Interned object still exists, so fake the removal return of the object
            _memoryCacheLock.EnterReadLock();
            try
            {
                return _memoryCache.Get(hashKey) as byte[];
            }
            finally
            {
                _memoryCacheLock.ExitReadLock();
            }
        }

        /// <summary>
        /// Clears the cache.
        /// </summary>
        public void Clear()
        {
            _memoryCacheLock.EnterWriteLock();
            try
            {
                var oldCache = _memoryCache;
                _memoryCache = new TrimmingMemoryCache(_cacheName, _cacheConfig);
                oldCache.Dispose();
            }
            finally
            {
                _memoryCacheLock.ExitWriteLock();
            }
        }

        /// <summary>
        /// Gets all the keys in the cache.
        /// WARNING: THIS IS A VERY EXPENSIVE OPERATION FOR LARGE CACHES. USE WITH CAUTION.
        /// </summary>
        /// <param name="pattern">The regular expression search pattern. If no pattern is provided, default "*" (all) is used.</param>
        public List<string> Keys(string pattern)
        {
            Regex regex = null;
            // Check if we have a pattern
            if (!string.IsNullOrWhiteSpace(pattern) && pattern != "*")
            {
                try
                {
                    regex = new Regex(pattern, RegexOptions.IgnoreCase);
                }
                catch (ArgumentException)
                {
                    return null;
                }
            }

            _memoryCacheLock.EnterWriteLock();
            try
            {
                // Lock ensures single thread, so parallelize to improve response time
                return _memoryCache.AsParallel().Where(kvp => regex == null ? true : regex.IsMatch(kvp.Key)).Select(kvp => kvp.Key).ToList();
            }
            finally
            {
                _memoryCacheLock.ExitWriteLock();
            }
        }

        /// <summary>
        /// Total number of objects in the cache.
        /// </summary>
        public long Count
        {
            get
            {
                // The total interned keys minus the actual hash keys plus the regular count
                return _internDictionary.Count - _internReferenceDictionary.Count + _memoryCache.GetCount();
            }
        }

        /// <summary>
        /// Gets the amount of memory on the computer, in megabytes, that can be used by the cache.
        /// </summary>
        public int MemoryLimit
        {
            get
            {
                return (int)(_memoryCache.CacheMemoryLimit / 1048576); // bytes / (1024 * 1024) for MB
            }
        }

        /// <summary>
        /// Called when disposed.
        /// </summary>
        public void Dispose()
        {
            _currentMemoryPerformanceCounter.Dispose();
            _memoryCache.Dispose();
        }

        /// <summary>
        /// Performs per second operations.
        /// </summary>
        /// <param name="state">The state. Ignored but required for timer callback methods. Pass null.</param>
        private void PerSecondOperations(object state)
        {
            // Lock to ensure atomicity (no overlap)
            lock (_perSecondTimer)
            {
                // Update performance data
                _performanceDataManager.NumberOfCachedObjects = Count;
                var usedMemoryMb = (int)(_currentMemoryPerformanceCounter.RawValue / 1048576); // bytes / (1024 * 1024) for MB

                _performanceDataManager.CacheMemoryUsageMb = usedMemoryMb;
                _performanceDataManager.CacheMemoryUsageLimitMb = MemoryLimit;
                _performanceDataManager.CacheMemoryUsagePercent = (int)(usedMemoryMb * 100 / MemoryLimit);
            }
        }

        /// <summary>
        /// Calculates a unique hash for a byte array.
        /// </summary>
        /// <param name="value">The byte array.</param>
        /// <returns>The resulting hash value.</returns>
        private static string CalculateHash(byte[] value)
        {
            int result = 13 * value.Length;
            for (int i = 0; i < value.Length; i++)
            {
                result = (17 * result) + value[i];
            }
            
            // Return custom intern key
            return string.Format("__InternedCacheKey_{0}", result);
        }
    }
}
