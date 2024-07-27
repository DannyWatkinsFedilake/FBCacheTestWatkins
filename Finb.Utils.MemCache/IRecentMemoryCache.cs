
namespace Finb.Utils.MemCache
{
    public interface IRecentMemoryCache
    {
        int MaxItems { get; set; }  // set at startup or increase later. Adding more items beyond this threshold will expire the least-recently-used
        int Count { get; }          // Number of items currently stored

        bool AddOrUpdate(string key, object value, Action<string>? expiryCallback = null);  // returns true if the item was added, false if updated. 
                                                                                            // optionally specify a callback for notification of forced expiry.
        bool TryRemove(string key);                                                            // this doesn't trigger the expiry callback
        bool TryGet(string key, out object? value);                                         // true if found.
    }
}