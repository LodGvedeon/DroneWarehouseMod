using System.Runtime.CompilerServices;

namespace DroneWarehouseMod.Core
{
    // Утилиты для Qualified Item ID
    internal static class Qid
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static string Qualify(string id)
            => string.IsNullOrEmpty(id) ? id
               : (id.Length > 2 && id[0] == '(' ? id : "(O)" + id);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static string Unqualify(string qid)
            => string.IsNullOrEmpty(qid) ? qid
               : (qid.Length > 3 && qid[0] == '(' && qid[2] == ')' ? qid.Substring(3) : qid);
    }
}
