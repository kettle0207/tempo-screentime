using System;

namespace Tempo
{
    internal static class CalendarRange
    {
        public static (DateTime start, DateTime end, DateTime previous) Get(string period, DateTime anchor)
        {
            var d = anchor.Date;
            switch (period)
            {
                case "day": return (d, d.AddDays(1), d.AddDays(-1));
                case "week":
                    d = d.AddDays(-(((int)d.DayOfWeek + 6) % 7)); // Monday is the first day.
                    return (d, d.AddDays(7), d.AddDays(-7));
                case "month":
                    d = new DateTime(d.Year, d.Month, 1);
                    return (d, d.AddMonths(1), d.AddMonths(-1));
                case "year":
                    d = new DateTime(d.Year, 1, 1);
                    return (d, d.AddYears(1), d.AddYears(-1));
                default: throw new ArgumentException("集計期間が正しくありません。");
            }
        }
    }
}
