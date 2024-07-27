using Microsoft.Extensions.Logging;
using System.Diagnostics;


namespace Finb.Utils.MemCache
{

    /// <summary>
    /// This utility class provides an in-memory object cache facility for use across the platform.
    /// Objects are stored and retrieved by key in a dictionary-style behaviour
    /// There's a configurable limit to the number of items held in the store. If additional items are added,
    /// the least recently used is removed.
    /// Callers can add an expiry-notification callback on AddOrUpdate - this is de-duped so safe to include this on all AddOrUpdate calls.
    /// The class is threadsafe, and the call-backs are on a different thread to avoid deadlocks if the cache is accessed in the callback.
    /// </summary>

    public class RecentMemoryCache : IRecentMemoryCache
    {
        #region interview design notes
        // INTERVIEW DESIGN NOTES
        // My approach in any non-trivial design is to look for at least two or three potential solutions rather than jumping at the first.

        // The obvious approach is a ConcurrentDictionary<key, object-wrapper>. However for thread-safety we will need to apply a lock around
        // both the dictionary and the mechanism that manages "least recently used", so an explicit lock is a better approach than a concurrent dictionary.

        // If the number of items were small, simply adding "last accessed time" to the object-wrapper would be a sufficient solution. However the
        // challenge comes where the number of items is large, and an iteration through all items to find the oldest access time will quickly be the least efficient 
        // part of the design. 

        // So I think we want a separate mechanism for determining "least recently accessed" .
        // Again the obvious solution is to think we need to store the time, but actually we're only interested in the order.
        // This implies a List<string> of the keys, where storing or accessing an item in the dictionary will remove that key from the list and put it at the front.
        // The least recently accessed item is then always the one at the end of the list.
        // The trouble with this is that again we have to iterate through the list to find our item, and removing something from the middle of a list is expensive,
        // as is inserting it at the front again!
        // I wonder if we can use something like a LinkedList<string> to identify least-recently-used, where the "object-wrapper" for a given key points to the
        // LinkedList item itself. This will make it much faster to find the item, remove it, and stick it at the front.
        // We won't be changing the items stored in the LinkedList, just moving them about, which is what LinkedLists are really good at.

        // https://learn.microsoft.com/en-us/dotnet/api/system.collections.generic.linkedlist-1?view=net-8.0 

        // I see that LinkedList<T> has just what we want. We can Remove(LinkedListNode<T>) a given node given a reference to it (without having to iterate through the list to find it).
        // This removal will be fast as no shuffling is needed as would be the case with an array / list.
        // The node can then be reinserted at the front, again without shuffling.
        // Each node is a LinkedListNode, which inherits from Object, so we can store a reference to the LinkedListNode in our object-wrapper, for instant access.
        // For the LinkedListNode<T>, T can just be a string, which will be the key to the object dictionary. This means we can just pick off the last item in the 
        // linked list, read the key, and remove that from the dictionary by key.

        // Clearly all this will need to be wrapped in a thread-safe lock, so I'm glad I didn't just go with the ConcurrentDictionary solution.

        // That's enough design, that was 20 minutes well spent on a Friday evening rather than dashing into coding. I'll do that in the morning.

        // SATURDAY MORNING...
        // Total time spent on the coding was about 90 minutes, about another 30 on the core tests and a bit more playing with performance compared to 
        // a solution without linked lists - as anticipated the simpler solution is more than a thousand times slower for the "expire-oldest" part.
        #endregion

        #region private fields
        private readonly Dictionary<string, MemCacheObjectWrapper> _objectDictionary = new Dictionary<string, MemCacheObjectWrapper>(); // holds the cached items
        private readonly LinkedList<string> _lastAccessedOrder = new LinkedList<string>(); // most recently accessed at the front, oldest at the end.
        private readonly ILogger<RecentMemoryCache> _logger;
        private readonly Object _lock = new Object();
        private int _maxItems = 0;
        #endregion

        #region public members
        public RecentMemoryCache(ILogger<RecentMemoryCache> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// set the MaxItems shortly after instantiation and before use.
        // It's safe to alter the size later if needed but it will protect against downsizing below current usage.
        /// </summary>
        public int MaxItems
        {
            set
            {
                lock (_lock) 
                {
                    if (value >= _objectDictionary.Count)
                    {
                        _maxItems = value;
                        _objectDictionary.TrimExcess(value);  // set the backing store to this size avoiding non-determinate delays due to resizing on insertion
                    }
                }
            }
            get
            {
                return _maxItems;
            }
        }

        /// <summary>
        /// Number of items currently stored, threadsafe
        /// </summary>
        public int Count
        {
            get
            {
                int count = 0;
                lock(_lock) 
                {
                    count = _objectDictionary.Count;
                }
                return count;
            }
        }

        /// <summary>
        /// Adds the object to the cache, or if already present updates the stored value.
        /// Expires the least recently used if we have hit the item-count threshold.
        /// Caller can specify a callback of form "void Expired(key)" which will be called if the item is forcibly expired.
        /// Note that the callback will be invoked on a different thread.
        /// A given caller's callback will be registered only once per key, so it's safe to specify the callback on every update call.

        /// Note that we are storing a reference to the value, rather than copying it. This is by design to handle large objects, but a knock-on effect
        /// is that if the caller subsequently modifies the content of the object, that modification will apply to our cached copy.
        /// </summary>
        /// <param name="key">item key</param>
        /// <param name="value">value stored</param>
        /// <param name="expiryCallback">called on forced expiry.</param>
        /// <returns>returns true if the item is added for the first time, false if it already existed</returns>
        /// 

        public bool AddOrUpdate(string key, Object value, Action<string>? expiryCallback = null)
        {
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentException("Key mustn't be null or empty", nameof(key));

            // note we're using .Net 8 so we're explicitely refusing to accept a null object in value.

            try
            {

                MemCacheObjectWrapper? wrapper = null;

                lock (_lock)
                {
                    bool exists = _objectDictionary.TryGetValue(key, out wrapper);

                    if (exists)
                    {
                        // item already exists so we have to move its recently used pointer to the front, and update the value in the dictionary

                        _lastAccessedOrder.Remove(wrapper!.Node);
                        _lastAccessedOrder.AddFirst(wrapper!.Node);
                        wrapper!.Item = value;

                        Debug.Assert(wrapper!.Node.Value == key);
                    }
                    else
                    {
                        // new item.

                        // need to expel the oldest if we are at the limit, and for safety if above.
                        while (_objectDictionary.Count >= _maxItems)
                        {
                            ExpireOldest();
                        }

                        // the linked list tells us which items have been accessed recently.
                        // this item should therefore be at the front of the list.

                        var node = new LinkedListNode<string>(key);
                        wrapper = new MemCacheObjectWrapper(value, node);
                        _objectDictionary.Add(key, wrapper);
                        _lastAccessedOrder.AddFirst(node);
                    }

                    // callback can be registered for new or existing item, caller probably doesn't know.
                    // This means we're likely to get the same callback registered thousands of times for a frequently updated item, so we need protection from this.
                    if (expiryCallback != null)
                    {
                        if (wrapper.ExpiryCallbacks == null)
                            wrapper.ExpiryCallbacks = new List<Action<string>> { expiryCallback };
                        else
                        {
                            if (!wrapper.ExpiryCallbacks.Contains(expiryCallback))    // this comparison should work, but test. (it did)
                                wrapper.ExpiryCallbacks.Add(expiryCallback);
                        }
                    }

                    return !exists;   // T if this is a fresh insert, F if an update.

                } // release the thread lock.


            }
            catch (OutOfMemoryException exMem)
            {
                // conceivable we'll get a memory exception if we're overloaded, report locally and throw.
                _logger.LogError(exMem, "{methodName} was unable to allocate memory", this.GetMethodName());
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "{methodName}", this.GetMethodName());
                throw;
            }
        }


        /// <summary>
        /// Attempt to retrieve an item from the cache.
        /// </summary>
        /// <param name="key">item key</param>
        /// <param name="value">item value, null if not found</param>
        /// <returns>T if found, F if it isn't there, possibly because it's been expired</returns>
        public bool TryGet(string key, out Object? value)
        {
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentException("Key mustn't be null or empty", nameof(key));

            try
            {
                lock (_lock)
                {

                    if (_objectDictionary.TryGetValue(key, out MemCacheObjectWrapper? wrapper))
                    {
                        // Accessing the item renews its status as recently accessed, by moving the node to the front of the linked list.

                        _lastAccessedOrder.Remove(wrapper.Node);
                        _lastAccessedOrder.AddFirst(wrapper.Node);

                        Debug.Assert(wrapper.Node.Value == key);

                        value = wrapper.Item;

                        return true;
                    }
                    else
                    {
                        value = null;
                        return false;
                    }
                }
            }
            catch (Exception ex)
            {
                // eg attempting to remove a node from the linked list when it isn't there, obviously shouldn't happen.

                _logger.LogError(ex, "{methodName}", this.GetMethodName());
                throw;
            }
        }

        /// <summary>
        /// Remove an item from the cache
        /// Manual removal doesn't trigger the expiry-notification callbacks by design.
        /// </summary>
        /// <param name="key"></param>
        /// <returns>True if it was there.</returns>

        public bool TryRemove(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentException("Key mustn't be null or empty", nameof(key));

            try
            {
                lock (_lock)
                {

                    if (_objectDictionary.TryGetValue(key, out MemCacheObjectWrapper? wrapper))
                    {
                        Debug.Assert(wrapper.Node.Value == key);

                        _lastAccessedOrder.Remove(wrapper.Node);
                        _objectDictionary.Remove(key);

                        return true;
                    }
                    else
                    {
                        return false;
                    }
                }
            }
            catch (Exception ex)
            {
                // eg attempting to remove a node from the linked list when it isn't there.

                _logger.LogError(ex, "{methodName}", this.GetMethodName());
                throw;
            }
        }
        #endregion

        #region private implementation
        private void ExpireOldest()
        {
            // remove the least recently used item, alerting anyone who has registered an expiry-callback.

            var oldest = _lastAccessedOrder.Last;
            if (oldest != null)
            {
                _lastAccessedOrder.Remove(oldest!);

                bool foundInDictionary = _objectDictionary.TryGetValue(oldest.Value, out MemCacheObjectWrapper? wrapper);
                Debug.Assert(foundInDictionary);

                _objectDictionary.Remove(oldest!.Value);

                // Alert users by calling their expiry-callbacks, passing the key of the item that has expired.
                // We're doing this while adding an item to the cache, which we want to be super fast. But we don't know how long these callbacks 
                // are going to take.
                // So we'll run the callbacks in another thread, without waiting for them to complete.
                // I'm deliberately not making the callbacks async, because we want to discourage callers from using the callback to refetch the item from the DB in 
                // our thread. 
                // The callback thread is not part of the "lock" on the caller's insertion thread, so we won't have a deadlock if they access the cache in the callback.

                if (wrapper!.ExpiryCallbacks != null)
                {
                    // This single thread sequentially calls each registered callback.
                    // If call-back performance is critical, we could launch a new thread for each.
                    var callbackThread = new Thread(() =>
                    {
                        try
                        {
                            wrapper!.ExpiryCallbacks.ForEach(x => x.Invoke(oldest.Value));
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "{methodName}", this.GetMethodName());
                        }
                    });
                    callbackThread.Start();
                }
            }
        }

        class MemCacheObjectWrapper
        {
            // This is the wrapper we store as the dictionary item.
            // As well as the cached object supplied by user, it references the LinkedListNode that records the order of access for "least recently used" expiry.
            // also holds the list of callers who've asked to be informed if this item is expired due to item-count threshold.

            public MemCacheObjectWrapper(Object item, LinkedListNode<string> node)
            {
                Item = item;
                Node = node;
                ExpiryCallbacks = null;
            }

            public List<Action<string>>? ExpiryCallbacks { get; set; }
            public Object Item { get; set; }

            public LinkedListNode<string> Node { get; set; }
        }
        #endregion

    }


}
