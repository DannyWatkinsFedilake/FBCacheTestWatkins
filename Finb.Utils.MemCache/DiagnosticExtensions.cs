using System.Runtime.CompilerServices;

namespace Finb.Utils.MemCache
{
    public static class DiagnosticExtensions
    {
        public static string GetMethodName(this object instance, [CallerMemberName] string? caller = null)
        {
            return $"{instance.GetType().FullName}.{caller}";
        }
    }
}
