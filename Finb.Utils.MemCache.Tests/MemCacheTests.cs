using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using Finb.Utils.MemCache;
using System.Diagnostics;

namespace Finb.Utils.MemCache.Tests
{
    public class MemCacheTests
    {


        public IRecentMemoryCache GetEmptyMemCache()
        {
            var mock = new Mock<ILogger<RecentMemoryCache>>();
            ILogger<RecentMemoryCache> logger = mock.Object;

            var cache = new RecentMemoryCache(logger);
            return cache;
        }

        [Fact]
        public void MemCache_BasicInsertionAndRetrieval()
        {
            // Test adding, updating, getting and removing items.
            // Performance and functionality won't be affected by the object type so no point in testing complex or large objects.



            var cache = GetEmptyMemCache();
            cache.MaxItems = 10;

            string key = "A";
            var o1 = new Object();
            bool inserted = cache.AddOrUpdate(key, o1);
            Assert.True(inserted);
            Assert.Equal(1, cache!.Count);

            var o2 = new Object();
            inserted = cache.AddOrUpdate(key, o2); // same key so shouldn't increase the count, just update the object.
            Assert.False(inserted);
            Assert.Equal(1, cache!.Count);

            bool found = cache.TryGet(key, out object? o3);
            Assert.True(found);
            Assert.True(Object.ReferenceEquals(o3, o2));
            Assert.Equal(1, cache!.Count);

            cache.TryRemove(key);

            Assert.Equal(0, cache!.Count);


        }

        [Fact]
        public void MemCache_ForcedExpiry()
        {
            // Test that the oldest key is expired and exactly one notification callback is triggered even though we've registered it several times.
            // As well as testing the callback operation, this confirms that the de-duping of callbacks that I wasn't sure about does indeed work.


            var cache = GetEmptyMemCache();
            cache.MaxItems = 3;
            expiredKeys.Clear();

            cache.AddOrUpdate("A", new object(), ExpiryCallback);
            cache.AddOrUpdate("B", new object(), ExpiryCallback);
            cache.AddOrUpdate("C", new object(), ExpiryCallback);

            // update A several times, should not cause a callback.

            cache.AddOrUpdate("A", new object(), ExpiryCallback);
            cache.AddOrUpdate("A", new object(), ExpiryCallback);
            cache.AddOrUpdate("A", new object(), ExpiryCallback);
            cache.AddOrUpdate("A", new object(), ExpiryCallback);

            Assert.Empty(expiredKeys);

            // reference B and C so A becomes the oldest

            cache.TryGet("B", out _);
            cache.TryGet("C", out _);

            // now add D, causing the expiry of A and a single callback.

            cache.AddOrUpdate("D", new object(), ExpiryCallback);

            Thread.Sleep(10); // callback is in another thread.

            Assert.Single(expiredKeys); // even though we registered the same callback multiple times, we should only get called back once
            Assert.Equal("A", expiredKeys[0]);
        }




        [Fact]
        public void MemCache_PerformanceComparison()
        {
            // Just a bit of fun to compare the performance of LinkedLists with an iterative approach.

            // An implementation without the linked list would need to either use a list or the dictionary to iterate through 
            // the collection to find the least recently accessed item. A List would involve shuffling, so Dictionary-lookup is probably faster.
            // Let's do a performance comparison to validate my assumption that LinkedLists would be much faster still.
            // For simplicity we'll add an integer-representation of time as the content of a Dictionary, similar to if we were storing accessed-time as a field in the wrapper.

            // The comparison sets up both a dictionary and the cache with a million entries, randomised access times, then just does the "find and remove oldest" step a thousand times.
            // Results of this expensive expiry stage are then compared.

            var timeDictionary = new Dictionary<string, int>();

            int itemCount = 1000000;
            var rand = new Random();

            // dictionary 
            for(int i = 0; i<itemCount; i++)
            {
                timeDictionary.Add(i.ToString(), rand.Next(itemCount));
            }
            Stopwatch stopWatch = new Stopwatch();
            stopWatch.Start();

            // The step we're testing is just the "find the oldest" bit.
            for (int loop = 0; loop < 1000; loop++)
            {
                // remove the oldest. This is two iterations, though we could probably do it in one...

                var oldesttime = timeDictionary.Values.Min();
                var oldest = timeDictionary.FirstOrDefault(x => x.Value == oldesttime);

                timeDictionary.Remove(oldest.Key);

                timeDictionary.Add(oldest.Key, rand.Next(itemCount));
            }

            stopWatch.Stop();
            var dictionaryMs = stopWatch.Elapsed.TotalMilliseconds;
            // about 5 seconds.

            var cache = GetEmptyMemCache();
            cache.MaxItems = itemCount;

            for (int i = 0; i < itemCount; i++)
            {
                cache.AddOrUpdate(i.ToString(), new Object());
            }

            // Randomly access the items so they're all shuffled up.

            for (int i = 0; i < itemCount * 5; i++)  
            {
                string key = rand.Next(itemCount).ToString();
                cache.TryGet(key, out _);
            }

            // Now do the same expiry performance test by adding further items beyond the max limit, causing it to lookup and expire the least recently used.

            stopWatch = new Stopwatch();
            stopWatch.Start();

            for (int loop = 0; loop < 1000; loop++)
            {
                cache.AddOrUpdate((itemCount + loop).ToString(), new Object());
            }

            stopWatch.Stop();
            var cacheMs = stopWatch.Elapsed.TotalMilliseconds;

            // about 1ms compared to about 5 seconds.
            // That'll do.

            Debug.Assert(dictionaryMs > 1000 * cacheMs);


        }

        




        void ExpiryCallback(string expiredKey)
        {
            expiredKeys.Add(expiredKey);
        }

        List<string> expiredKeys = new List<string>();

    }
}
