﻿namespace Tenray.ZoneTree.Comparers;

public class StringInvariantIgnoreCaseComparerAscending : IRefComparer<string>
{
    public int Compare(in string x, in string y)
    {
        return string.Compare(x, y, StringComparison.InvariantCultureIgnoreCase);
    }
}
