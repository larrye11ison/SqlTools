using System;
using System.Collections.Generic;
using System.Linq;

namespace SqlTools.Shell
{
    public static class IListExtensions
    {
        public static void Delete<T>(this ICollection<T> list, Func<T, bool> predicate)
        {
            foreach (var item in list.Where(item => predicate(item)).ToArray())
            {
                list.Remove(item);
            }
        }
    }
}