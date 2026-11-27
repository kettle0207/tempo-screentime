// Seeds v1-format history to verify upgrades and mixed-format calendar reports.
namespace Tempo
{
    internal sealed class Slice
    {
        public string Day, App, Site;
        public int Hour;
        public long Ms;
    }
    internal sealed partial class History
    {
        public void Save(IEnumerable<Slice> slices)
        {
            lock (gate)
            {
                db.Run("BEGIN IMMEDIATE");
                try
                {
                    foreach (var g in slices.GroupBy(s => new { s.Day,s.Hour,s.App,s.Site }))
                        db.Run(@"INSERT INTO usage VALUES(?,?,?,?,?) ON CONFLICT(day,hour,app,site)
                            DO UPDATE SET ms=usage.ms+excluded.ms",g.Key.Day,g.Key.Hour,g.Key.App,g.Key.Site,g.Sum(s=>s.Ms));
                    db.Run("COMMIT");
                }
                catch { db.Run("ROLLBACK"); throw; }
            }
        }
    }
}
